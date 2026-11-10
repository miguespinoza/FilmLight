using System.Buffers.Binary;
using System.Text;

namespace FilmLight.Workflow;

internal static partial class ProcessingImages
{
    private static (float R, float G, float B) ToLinearSrgb(float r, float g, float b) =>
        (1.660491f * r - .587641f * g - .072850f * b,
         -.124550f * r + 1.132900f * g - .008349f * b,
         -.018151f * r - .100579f * g + 1.118730f * b);

    // Inverse darktable 5.6.2 PROPHOTO_RGB colorants * our input ICC colorants.
    // Both are Bradford-adapted to D50; applying another D65->D50 adaptation would be wrong.
    // Despite its enum name, darktable embeds "Linear ProPhoto RGB", with gamma 1.0.
    private static (float R, float G, float B) ToLinearProPhoto(float r, float g, float b) =>
        (.835190604f * r + .048800641f * g + .116033474f * b,
         .054021894f * r + .928934041f * g + .017034020f * b,
         -.002331743f * r + .036331318f * g + .966388882f * b);

    private static byte EncodeSrgb(float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite preview sample.");
        double v = Math.Clamp(value, 0, 1);
        return (byte)Math.Round(255 * (v <= .0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - .055));
    }

    internal static byte[] ProPhotoProfile()
    {
        byte[] curve = new byte[14]; "curv"u8.CopyTo(curve);
        BinaryPrimitives.WriteInt32BigEndian(curve.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12), 256);
        return MatrixProfile("FilmLight Linear ProPhoto RGB", curve,
            [[52278 / 65536.0, 18878 / 65536.0, 0],
             [8861 / 65536.0, 46653 / 65536.0, 1 / 65536.0],
             [2051 / 65536.0, 5 / 65536.0, 54060 / 65536.0]],
            [63190 / 65536.0, 1, 54061 / 65536.0]);
    }

    private static byte[] MatrixProfile(string name, byte[] curve, double[][] colorants, double[] white)
    {
        static byte[] Xyz(double[] values)
        {
            var b = new byte[20]; "XYZ "u8.CopyTo(b);
            for (int i = 0; i < 3; i++)
                BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8 + i * 4), (int)Math.Round(values[i] * 65536));
            return b;
        }
        byte[] desc = new byte[128]; "desc"u8.CopyTo(desc);
        byte[] text = Encoding.ASCII.GetBytes(name + "\0");
        BinaryPrimitives.WriteInt32BigEndian(desc.AsSpan(8), text.Length); text.CopyTo(desc, 12);
        (string Tag, byte[] Data)[] tags =
        [
            ("desc", desc), ("cprt", Encoding.ASCII.GetBytes("text\0\0\0\0Public domain colorimetry\0")),
            ("wtpt", Xyz(white)), ("rXYZ", Xyz(colorants[0])), ("gXYZ", Xyz(colorants[1])),
            ("bXYZ", Xyz(colorants[2])), ("rTRC", curve), ("gTRC", curve), ("bTRC", curve)
        ];
        int offset = 132 + tags.Length * 12;
        var result = new byte[offset + tags.Sum(t => (t.Data.Length + 3) & ~3)];
        BinaryPrimitives.WriteInt32BigEndian(result, result.Length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), 0x02100000);
        "mntr"u8.CopyTo(result.AsSpan(12)); "RGB "u8.CopyTo(result.AsSpan(16));
        "XYZ "u8.CopyTo(result.AsSpan(20)); "acsp"u8.CopyTo(result.AsSpan(36));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(24), 2026);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(28), 1);
        Xyz([.96422, 1, .82521]).AsSpan(8, 12).CopyTo(result.AsSpan(68));
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(128), tags.Length);
        for (int i = 0; i < tags.Length; i++)
        {
            var entry = result.AsSpan(132 + i * 12, 12);
            Encoding.ASCII.GetBytes(tags[i].Tag).CopyTo(entry);
            BinaryPrimitives.WriteInt32BigEndian(entry[4..], offset);
            BinaryPrimitives.WriteInt32BigEndian(entry[8..], tags[i].Data.Length);
            tags[i].Data.CopyTo(result, offset);
            offset += (tags[i].Data.Length + 3) & ~3;
        }
        return result;
    }
}
