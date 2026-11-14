using System.Buffers.Binary;
using System.Text;

namespace FilmLight.Processing;

internal static class SrgbProfile
{
    // ICC v2 matrix/shaper profile, D50-adapted sRGB primaries and the sRGB transfer curve.
    internal static byte[] Create()
    {
        var tags = new List<(string Name, byte[] Data)>
        {
            ("desc", Description("Film Light sRGB")),
            ("cprt", Encoding.ASCII.GetBytes("text\0\0\0\0Film Light\0")),
            ("wtpt", Xyz(.9642, 1, .8249)),
            ("rXYZ", Xyz(.4360747, .2225045, .0139322)),
            ("gXYZ", Xyz(.3850649, .7168786, .0971045)),
            ("bXYZ", Xyz(.1430804, .0606169, .7141733))
        };
        var curve = new byte[12 + 4096 * 2];
        Signature(curve, 0, "curv");
        U32(curve, 8, 4096);
        for (int i = 0; i < 4096; i++)
        {
            double v = i / 4095d;
            double linear = v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
            BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12 + i * 2), (ushort)Math.Round(linear * 65535));
        }
        foreach (var name in new[] { "rTRC", "gTRC", "bTRC" }) tags.Add((name, curve));
        int size = 132 + tags.Count * 12 + tags.Sum(tag => (tag.Data.Length + 3) & ~3);
        var profile = new byte[size];
        U32(profile, 0, size);
        U32(profile, 8, 0x02100000);
        Signature(profile, 12, "mntr");
        Signature(profile, 16, "RGB ");
        Signature(profile, 20, "XYZ ");
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(24), 2026);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(28), 1);
        Signature(profile, 36, "acsp");
        Fixed(profile, 68, .9642); Fixed(profile, 72, 1); Fixed(profile, 76, .8249);
        U32(profile, 128, tags.Count);
        int offset = 132 + tags.Count * 12;
        for (int i = 0; i < tags.Count; i++)
        {
            Signature(profile, 132 + i * 12, tags[i].Name);
            U32(profile, 136 + i * 12, offset);
            U32(profile, 140 + i * 12, tags[i].Data.Length);
            tags[i].Data.CopyTo(profile, offset);
            offset += (tags[i].Data.Length + 3) & ~3;
        }
        return profile;
    }

    private static byte[] Description(string text)
    {
        var data = new byte[12 + text.Length + 1 + 8 + 70];
        Signature(data, 0, "desc");
        U32(data, 8, text.Length + 1);
        Encoding.ASCII.GetBytes(text).CopyTo(data, 12);
        return data;
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        var data = new byte[20];
        Signature(data, 0, "XYZ ");
        Fixed(data, 8, x); Fixed(data, 12, y); Fixed(data, 16, z);
        return data;
    }

    private static void Signature(byte[] data, int offset, string value) =>
        Encoding.ASCII.GetBytes(value).CopyTo(data, offset);
    private static void U32(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
    private static void Fixed(byte[] data, int offset, double value) => U32(data, offset, (int)Math.Round(value * 65536));
}
