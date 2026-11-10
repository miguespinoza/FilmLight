using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BitMiracle.LibTiff.Classic;

[assembly: InternalsVisibleTo("FilmLight.Tests")]

namespace FilmLight.Workflow;

// Private decoding implementation used by FrameSources; no roll snapshots or consumer-facing paths.
internal static class CameraInputDecoder
{
    private static readonly XNamespace Dt = "http://darktable.sf.net/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    internal static void WriteIdentityXmp(string path, bool raw)
    {
        var history = new XElement(Rdf + "Seq");
        // Pinned 5.6.2 layouts: temperature v4 is four floats + user preset; colorin v7 is 1044 bytes.
        var temperature = new byte[20];
        for (int c = 0; c < 4; c++) BinaryPrimitives.WriteSingleLittleEndian(temperature.AsSpan(c * 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(temperature.AsSpan(16), 2);
        var color = new byte[1044];
        BinaryPrimitives.WriteInt32LittleEndian(color, raw ? 11 : 9); // standard camera matrix / embedded ICC
        BinaryPrimitives.WriteInt32LittleEndian(color.AsSpan(516), 1); // relative colorimetric
        BinaryPrimitives.WriteInt32LittleEndian(color.AsSpan(528), 4); // linear Rec2020 working space
        Add("temperature", 4, raw, temperature);
        Add("colorin", 7, true, color);
        if (raw)
        {
            // Empty auto-init parameters retain per-file CFA geometry and black/white levels.
            Add("rawprepare", 2, true, []);
            Add("demosaic", 4, true, []);
        }
        foreach (string operation in new[] { "exposure", "basecurve", "filmicrgb", "sigmoid", "tonecurve",
            "rgbcurve", "levels", "rgblevels", "shadhi", "highlights", "sharpen", "colorbalancergb",
            "channelmixerrgb", "negadoctor", "lens" })
            Add(operation, 1, false, []);
        var description = new XElement(Rdf + "Description",
            new XAttribute(XNamespace.Xmlns + "darktable", Dt), new XAttribute(Dt + "xmp_version", 5),
            new XAttribute(Dt + "raw_params", 0), new XAttribute(Dt + "auto_presets_applied", 1),
            new XAttribute(Dt + "iop_order_version", 2), new XAttribute(Dt + "history_end", history.Elements().Count()),
            new XElement(Dt + "history", history));
        new XDocument(new XElement(XName.Get("xmpmeta", "adobe:ns:meta/"),
            new XAttribute(XNamespace.Xmlns + "x", "adobe:ns:meta/"),
            new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf), description))).Save(path);

        void Add(string operation, int version, bool enabled, byte[] parameters) =>
            history.Add(new XElement(Rdf + "li", new XAttribute(Dt + "num", history.Elements().Count()),
                new XAttribute(Dt + "operation", operation), new XAttribute(Dt + "enabled", enabled ? 1 : 0),
                new XAttribute(Dt + "modversion", version), new XAttribute(Dt + "params", Convert.ToHexString(parameters).ToLowerInvariant()),
                new XAttribute(Dt + "multi_priority", 0), new XAttribute(Dt + "multi_name", ""),
                new XAttribute(Dt + "blendop_version", 13)));
    }

    internal static Task<string> ConvertAsync(DarktableProcess process, string directory,
        string input, string output, CancellationToken token, int? edge = null) =>
        process.RunAsync(new[] { input, "identity.xmp", output }.Concat(edge is { } size ?
            new[] { "--width", size.ToString(), "--height", size.ToString(), "--hq", "true" } : [])
            .Concat(["--icc-type", "LIN_REC2020",
            "--icc-intent", "RELATIVE_COLORIMETRIC", "--apply-custom-presets", "0", "--verbose",
            "--core", "--configdir", Path.Combine(directory, "config"),
            "--cachedir", Path.Combine(directory, "cache"), "--disable-opencl",
            "--conf", "plugins/darkroom/workflow=none",
            "--conf", "ui_last/gui_language=C",
            "--conf", "plugins/imageio/format/tiff/bpp=32",
            "--conf", "plugins/imageio/format/tiff/compress=0",
            "--conf", "plugins/imageio/format/tiff/shortfile=0",
            "--conf", "plugins/lighttable/export/pixel_interpolator=bilinear",
            "--conf", "plugins/lighttable/export/pixel_interpolator_warp=bilinear",
            "--conf", "write_sidecar_files=never"]), directory, "decode", token);

    internal static void ValidateHistory(string log, string errors, bool raw)
    {
        string[] allowed = ["raw black/white point", "white balance", "demosaic", "input color profile",
            "output color profile", "orientation", "display encoding"];
        var enabled = Regex.Matches(log, @"(?m)^\s*\u25cf\s+([^\r\n]+)").Select(m => m.Groups[1].Value.Trim()).ToHashSet();
        if (!enabled.Contains("input color profile") ||
            (raw && new[] { "raw black/white point", "white balance", "demosaic" }.Any(m => !enabled.Contains(m))) ||
            enabled.Any(m => !allowed.Contains(m)) ||
            Regex.IsMatch(log + "\n" + errors,
                @"(?i)color matrix not found|input profile could not|unsupported|failed to|version mismatch|unknown version|not supported"))
            throw new InvalidDataException("darktable did not apply the qualified identity/camera-profile pipeline; inspect decode logs/XMP. No default-profile or tone-mapping fallback is allowed.");
    }

    internal static void ValidateFloatProfile(string path)
    {
        using var tif = Tiff.Open(path, "r") ?? throw new InvalidDataException($"Missing decoded TIFF: {path}");
        if (tif.GetField(TiffTag.BITSPERSAMPLE)?[0].ToInt() != 32 ||
            tif.GetFieldDefaulted(TiffTag.SAMPLEFORMAT)[0].ToInt() != (int)SampleFormat.IEEEFP)
            throw new InvalidDataException("Camera decode did not produce 32-bit floating-point TIFF.");
        byte[] profile = tif.GetField(TiffTag.ICCPROFILE)?[1].ToByteArray() ?? [];
        byte[] expected = ProcessingImages.LinearRec2020Profile();
        if (profile.Length < 132 || !profile.AsSpan(36, 4).SequenceEqual("acsp"u8) ||
            !profile.AsSpan(16, 8).SequenceEqual("RGB XYZ "u8))
            throw new InvalidDataException("Decoded TIFF has no RGB matrix ICC profile.");
        var tags = ProfileTags(profile);
        var reference = ProfileTags(expected);
        foreach (string channel in new[] { "r", "g", "b" })
        {
            if (!tags.TryGetValue(channel + "XYZ", out var xyz) || xyz.Length < 20 ||
                !xyz.AsSpan(0, 4).SequenceEqual("XYZ "u8))
                throw new InvalidDataException("Decoded ICC lacks Rec2020 primaries.");
            for (int c = 0; c < 3; c++)
                if (Math.Abs(BinaryPrimitives.ReadInt32BigEndian(xyz.AsSpan(8 + c * 4)) / 65536.0 -
                    BinaryPrimitives.ReadInt32BigEndian(reference[channel + "XYZ"].AsSpan(8 + c * 4)) / 65536.0) >= .003)
                    throw new InvalidDataException("Decoded ICC primaries are not Rec2020.");
            if (!tags.TryGetValue(channel + "TRC", out var trc) || trc.Length < 12 ||
                !((trc.AsSpan(0, 4).SequenceEqual("curv"u8) &&
                   (BinaryPrimitives.ReadUInt32BigEndian(trc.AsSpan(8)) == 0 ||
                    (trc.Length >= 14 && BinaryPrimitives.ReadUInt32BigEndian(trc.AsSpan(8)) == 1 &&
                     BinaryPrimitives.ReadUInt16BigEndian(trc.AsSpan(12)) == 256))) ||
                  (trc.Length >= 16 && trc.AsSpan(0, 4).SequenceEqual("para"u8) &&
                   BinaryPrimitives.ReadUInt16BigEndian(trc.AsSpan(8)) == 0 &&
                   BinaryPrimitives.ReadInt32BigEndian(trc.AsSpan(12)) == 65536)))
                throw new InvalidDataException("Decoded ICC transfer curve is not linear.");
        }
    }

    private static Dictionary<string, byte[]> ProfileTags(byte[] data)
    {
        uint count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(128));
        if (count > (data.Length - 132) / 12) throw new InvalidDataException("Malformed decoded ICC tag table.");
        var tags = new Dictionary<string, byte[]>();
        for (int i = 0; i < count; i++)
        {
            var entry = data.AsSpan(132 + i * 12, 12);
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            if (offset > data.Length || size > data.Length - offset)
                throw new InvalidDataException("Malformed decoded ICC tag extent.");
            tags[Encoding.ASCII.GetString(entry[..4])] = data.AsSpan((int)offset, (int)size).ToArray();
        }
        return tags;
    }

}
