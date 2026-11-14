using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;


namespace FilmLight.Processing;

internal static class DarktableHistory
{
    private const string Dt = "http://darktable.sf.net/";
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly byte[] Linear = Load("linear");
    private static readonly byte[] Positive = Load("positive");
    internal static string TemplateHash { get; } = Convert.ToHexStringLower(SHA256.HashData(Linear.Concat(Positive).ToArray()));

    internal static void Write(string path, LightBalance light, NegadoctorParameters conversion, bool linear,
        bool cameraChannels = false)
    {
        NegativeConversionValidation.Validate(conversion);
        using var input = new MemoryStream(linear ? Linear : Positive);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var document = XDocument.Load(reader);
        var sequence = document.Descendants(XName.Get("Seq", Rdf)).Single();
        if (cameraChannels)
        {
            if (!linear) throw new ArgumentException("Camera channels are only a linear read.");
            light = new(new(1, 1, 1), "RGB capture");
            var profile = new XElement(sequence.Elements().First());
            profile.SetAttributeValue(XName.Get("operation", Dt), "colorin");
            profile.SetAttributeValue(XName.Get("modversion", Dt), "7");
            profile.SetAttributeValue(XName.Get("params", Dt), PackPassThroughColorIn());
            sequence.Add(profile);
        }
        foreach (var item in sequence.Elements().ToArray())
        {
            var operation = (string?)item.Attribute(XName.Get("operation", Dt));
            if (operation == "temperature")
            {
                if (light.Coefficients is not { } rgb) item.Remove();
                else item.SetAttributeValue(XName.Get("params", Dt), PackTemperature(rgb));
            }
            else if (operation == "negadoctor") item.SetAttributeValue(XName.Get("params", Dt), Pack(conversion));
        }
        int number = 0;
        foreach (var item in sequence.Elements()) item.SetAttributeValue(XName.Get("num", Dt), number++);
        document.Descendants(XName.Get("Description", Rdf)).Single().SetAttributeValue(XName.Get("history_end", Dt), number);
        using var writer = XmlWriter.Create(path, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true });
        document.WriteTo(writer);
    }

    internal static (LightBalance Light, NegadoctorParameters Conversion) Read(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 4_000_000 });
        var document = XDocument.Load(reader);
        var description = document.Descendants(XName.Get("Description", Rdf)).FirstOrDefault()
            ?? throw new InvalidDataException("The XMP has no darktable history.");
        var end = int.TryParse((string?)description.Attribute(XName.Get("history_end", Dt)), out var count) ? count : int.MaxValue;
        var entries = document.Descendants(XName.Get("li", Rdf)).Where(e =>
            int.TryParse((string?)e.Attribute(XName.Get("num", Dt)), out var n) && n < end).ToArray();
        var negative = entries.LastOrDefault(e => (string?)e.Attribute(XName.Get("operation", Dt)) == "negadoctor" &&
            (string?)e.Attribute(XName.Get("enabled", Dt)) == "1") ?? throw new InvalidDataException("No enabled negadoctor history was found.");
        if ((string?)negative.Attribute(XName.Get("modversion", Dt)) != "2")
            throw new NotSupportedException("Only negadoctor modversion 2 is supported.");
        var parameters = Unpack((string?)negative.Attribute(XName.Get("params", Dt)) ?? "");
        var temperature = entries.LastOrDefault(e => (string?)e.Attribute(XName.Get("operation", Dt)) == "temperature" &&
            (string?)e.Attribute(XName.Get("enabled", Dt)) == "1");
        LightBalance light = new(null, null);
        if (temperature is not null)
        {
            if ((string?)temperature.Attribute(XName.Get("modversion", Dt)) != "4")
                throw new NotSupportedException("Only temperature modversion 4 is supported.");
            var hex = (string?)temperature.Attribute(XName.Get("params", Dt)) ?? "";
            int preset = BinaryPrimitives.ReadInt32LittleEndian(Decode(hex, 20).AsSpan(16));
            light = new(UnpackTemperature(hex), preset == 4
                ? $"{Path.GetFileName(path)} (camera-reference correction not imported)"
                : Path.GetFileName(path));
        }
        return (light, parameters);
    }

    internal static ValueTask<(NegadoctorParameters Conversion, LightBalance Light)> ReadProfileParametersAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var profile = Read(path);
        return ValueTask.FromResult((profile.Conversion, profile.Light));
    }

    internal static string Pack(NegadoctorParameters p)
    {
        NegativeConversionValidation.Validate(p);
        byte[] bytes = new byte[76];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)p.FilmType);
        double[] values = [p.FilmBase.R, p.FilmBase.G, p.FilmBase.B, 1,
            p.HighlightsBalance.R, p.HighlightsBalance.G, p.HighlightsBalance.B, 1,
            p.ShadowsBalance.R, p.ShadowsBalance.G, p.ShadowsBalance.B, 1,
            p.DensityRange, p.ScanExposureBias, p.PaperBlack, p.PaperGrade, p.PaperGloss, p.PrintExposure];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4 + i * 4), (float)values[i]);
        return Convert.ToHexStringLower(bytes);
    }

    internal static NegadoctorParameters Unpack(string hex)
    {
        var bytes = Decode(hex, 76);
        double F(int i) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4 + i * 4));
        var p = new NegadoctorParameters((FilmType)BinaryPrimitives.ReadInt32LittleEndian(bytes),
            new(F(0), F(1), F(2)), new(F(4), F(5), F(6)), new(F(8), F(9), F(10)),
            F(12), F(13), F(14), F(15), F(16), F(17));
        NegativeConversionValidation.Validate(p);
        return p;
    }

    internal static string PackTemperature(RgbTriple rgb)
    {
        ValidateLight(rgb);
        byte[] bytes = new byte[20];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, (float)(rgb.R / rgb.G));
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), (float)(rgb.B / rgb.G));
        // The fourth coefficient is unused for RGB/X-Trans. preset=2 is user-defined, not D65.
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 2);
        return Convert.ToHexStringLower(bytes);
    }

    internal static string PackPassThroughColorIn()
    {
        // release-5.6.2 colorin v7: type, filename[512], intent, normalize,
        // blue_mapping, type_work, filename_work[512]. Identical linear profiles
        // cancel input/output transforms; camera RGB is deliberately not colorimetric.
        byte[] bytes = new byte[1044];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 4); // linear Rec.2020 input
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(528), 4); // same working profile
        return Convert.ToHexStringLower(bytes);
    }

    internal static RgbTriple UnpackTemperature(string hex)
    {
        var bytes = Decode(hex, 20);
        double F(int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset));
        var rgb = new RgbTriple(F(0), F(4), F(8));
        ValidateLight(rgb);
        return new(rgb.R / rgb.G, 1, rgb.B / rgb.G);
    }

    private static void ValidateLight(RgbTriple rgb)
    {
        if (!double.IsFinite(rgb.R) || !double.IsFinite(rgb.G) || !double.IsFinite(rgb.B) ||
            rgb.R <= 0 || rgb.G <= 0 || rgb.B <= 0 || rgb.R / rgb.G > 8 || rgb.B / rgb.G > 8)
            throw new ArgumentException("Light Balance coefficients must be finite, positive and at most 8 after green normalization.");
    }

    private static byte[] Decode(string hex, int size)
    {
        try
        {
            var bytes = Convert.FromHexString(hex);
            if (bytes.Length != size) throw new InvalidDataException($"Expected {size} parameter bytes.");
            return bytes;
        }
        catch (FormatException error) { throw new InvalidDataException("Invalid darktable parameter hex.", error); }
    }

    private static byte[] Load(string name)
    {
        using var stream = typeof(DarktableHistory).Assembly.GetManifestResourceStream($"FilmLight.Processing.Templates.{name}.xmp")
            ?? throw new InvalidOperationException("The darktable history template is missing.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}

