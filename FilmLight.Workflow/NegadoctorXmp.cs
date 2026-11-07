using System.Buffers.Binary;
using System.Xml;
using System.Xml.Linq;

namespace FilmLight.Workflow;

internal static class NegadoctorXmp
{
    private static readonly XNamespace Dt = "http://darktable.sf.net/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    internal static void Write(string path, DevelopRecipe recipe, float[] filmBase, bool inversion)
    {
        XDocument document;
        XElement description, history;
        if (inversion && recipe.XmpTemplatePath is not null)
        {
            using var reader = XmlReader.Create(recipe.XmpTemplatePath, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000
            });
            document = XDocument.Load(reader);
            description = document.Descendants(Rdf + "Description").FirstOrDefault(e => e.Element(Dt + "history") is not null)
                ?? throw new InvalidDataException("Calibrated XMP must contain a darktable history.");
            history = description.Element(Dt + "history")!.Element(Rdf + "Seq")
                ?? throw new InvalidDataException("Expected modern darktable XMP rdf:Seq history.");
            if (!int.TryParse((string?)description.Attribute(Dt + "history_end"), out int end) ||
                end != history.Elements().Count())
                throw new InvalidDataException("Template has an undone/partial history. Truncate the undone history and save the accepted negadoctor-only XMP before using it.");
            // Exposure, channel gains, levels and crop belong to the immutable DevelopRecipe, not a hidden second transform.
            if (history.Elements().Any(e => (string?)e.Attribute(Dt + "enabled") == "1" &&
                (string?)e.Attribute(Dt + "operation") != "negadoctor"))
                throw new InvalidDataException("Calibrated XMP must have only negadoctor enabled in its history; Film Light handles linear input profiling, exposure, crop, channel gains and levels. Save a negadoctor-only template on the normalized linear Rec2020 TIFF.");
            if ((string?)description.Attribute(Dt + "iop_order_version") != "2" ||
                description.Attribute(Dt + "iop_order_list") is not null)
                throw new InvalidDataException("Template requires the standard v3.0 (iop_order_version=2) pipeline, not a custom module order.");
            var modules = history.Elements().Where(e => (string?)e.Attribute(Dt + "operation") == "negadoctor" &&
                (string?)e.Attribute(Dt + "enabled") == "1").ToArray();
            if (modules.Length != 1 || (string?)modules[0].Attribute(Dt + "modversion") != "2")
                throw new InvalidDataException("Template requires exactly one enabled negadoctor v2 (darktable 5.6.2).");
            byte[] parameters;
            try { parameters = Convert.FromHexString((string?)modules[0].Attribute(Dt + "params") ?? ""); }
            catch (FormatException error)
            {
                throw new InvalidDataException("Template negadoctor parameters must be uncompressed hex v2; re-save a calibrated darktable 5.6.2 XMP.", error);
            }
            if (parameters.Length != 76) throw new InvalidDataException("Unsupported negadoctor v2 parameter layout; expected 76 bytes.");
            for (int offset = 4; offset < parameters.Length; offset += 4)
                if (!float.IsFinite(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(parameters.AsSpan(offset)))))
                    throw new InvalidDataException("Calibrated negadoctor template contains non-finite parameters.");
            for (int c = 0; c < 3; c++)
            {
                Check(parameters, 20 + c * 4, .25f, 2);
                Check(parameters, 36 + c * 4, .25f, 2);
            }
            Check(parameters, 52, .1f, 6);
            Check(parameters, 56, -1, 1);
            Check(parameters, 60, -.5f, .5f);
            Check(parameters, 64, 1, 8);
            Check(parameters, 68, .0001f, 1);
            Check(parameters, 72, .5f, 2);
            SetBase(parameters, recipe, filmBase);
            modules[0].SetAttributeValue(Dt + "params", Convert.ToHexString(parameters).ToLowerInvariant());
        }
        else
        {
            history = new XElement(Rdf + "Seq");
            description = new XElement(Rdf + "Description",
                new XAttribute(XNamespace.Xmlns + "darktable", Dt), new XAttribute(Dt + "xmp_version", 5),
                new XAttribute(Dt + "raw_params", 0), new XAttribute(Dt + "auto_presets_applied", 1),
                new XAttribute(Dt + "iop_order_version", 2), new XElement(Dt + "history", history));
            document = new XDocument(new XElement(XName.Get("xmpmeta", "adobe:ns:meta/"),
                new XAttribute(XNamespace.Xmlns + "x", "adobe:ns:meta/"),
                new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf), description)));
            if (inversion)
            {
                // Version-pinned v2 wire layout from the published negadoctor parameter schema.
                var parameters = new byte[76];
                for (int i = 0; i < 12; i++) Float(parameters, 4 + i * 4, 1);
                Float(parameters, 52, recipe.FilmKind == FilmKind.BlackAndWhiteNegative ? 2.2f : 1.6f);
                Float(parameters, 56, -.05f);
                Float(parameters, 60, .0755f);
                Float(parameters, 64, recipe.FilmKind == FilmKind.BlackAndWhiteNegative ? 5 : 4);
                Float(parameters, 68, .75f);
                Float(parameters, 72, .9245f);
                SetBase(parameters, recipe, filmBase);
                history.Add(new XElement(Rdf + "li", new XAttribute(Dt + "num", 0),
                    new XAttribute(Dt + "operation", "negadoctor"), new XAttribute(Dt + "enabled", 1),
                    new XAttribute(Dt + "modversion", 2), new XAttribute(Dt + "params", Convert.ToHexString(parameters).ToLowerInvariant()),
                    new XAttribute(Dt + "multi_priority", 0), new XAttribute(Dt + "multi_name", ""),
                    new XAttribute(Dt + "blendop_version", 13)));
            }
        }
        description.SetAttributeValue(Dt + "auto_presets_applied", 1);
        int index = 0;
        foreach (var entry in history.Elements()) entry.SetAttributeValue(Dt + "num", index++);
        description.SetAttributeValue(Dt + "history_end", history.Elements().Count());
        document.Save(path);
    }

    private static void SetBase(byte[] parameters, DevelopRecipe recipe, float[] filmBase)
    {
        BinaryPrimitives.WriteInt32LittleEndian(parameters, recipe.FilmKind == FilmKind.BlackAndWhiteNegative ? 0 : 1);
        for (int c = 0; c < 3; c++) Float(parameters, 4 + c * 4, filmBase[c]);
        Float(parameters, 16, 1);
    }

    private static void Float(byte[] parameters, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(parameters.AsSpan(offset), BitConverter.SingleToInt32Bits(value));

    private static void Check(byte[] parameters, int offset, float min, float max)
    {
        float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(parameters.AsSpan(offset)));
        if (value < min || value > max)
            throw new InvalidDataException($"Calibrated negadoctor parameter at byte {offset} is outside [{min}, {max}]. Recalibrate/save the template in darktable 5.6.2.");
    }
}
