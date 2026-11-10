using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace FilmLight.Workflow;

[SupportedOSPlatform("windows")]
internal sealed class WicJpegEncoder : IDisposable
{
    private readonly List<object> objects = [];
    private readonly IWICBitmapEncoder encoder;
    private readonly IWICBitmapFrameEncode frame;
    private readonly uint stride;

    internal WicJpegEncoder(string path, int width, int height, byte[] profile)
    {
        try
        {
            var factory = Own((IWICImagingFactory)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new("cacaf262-9370-4615-a13b-9f5539da4c0a"), true)!)!);
            const uint createWrite = 0x1000 | 1, normalFile = 0x80, noCache = 2;
            Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, createWrite, normalFile, false, null, out var stream));
            Own(stream);
            Guid jpeg = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");
            factory.CreateEncoder(ref jpeg, IntPtr.Zero, out encoder); Own(encoder);
            encoder.Initialize(stream, noCache);
            encoder.CreateNewFrame(out frame, out var options); Own(frame); Own(options);
            var properties = new[]
            {
                new PropertyBag { Name = "ImageQuality", Type = 4 },
                new PropertyBag { Name = "JpegYCrCbSubsampling", Type = 17 }
            };
            const byte subsampling444 = 3;
            options.Write(2, properties, [.95f, subsampling444]);
            frame.Initialize(options);
            frame.SetSize((uint)width, (uint)height);
            Guid format = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");
            Guid requested = format;
            frame.SetPixelFormat(ref format);
            if (format != requested) throw new InvalidDataException("WIC JPEG encoder rejected 24-bit BGR.");
            factory.CreateColorContext(out var color); Own(color);
            color.InitializeFromMemory(profile, (uint)profile.Length);
            frame.SetColorContexts(1, [color]);
            stride = checked((uint)width * 3);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void WriteRow(byte[] row) => frame.WritePixels(1, stride, (uint)row.Length, row);
    internal void Commit() { frame.Commit(); encoder.Commit(); }

    private T Own<T>(T value) where T : class { objects.Add(value); return value; }
    public void Dispose()
    {
        for (int i = objects.Count - 1; i >= 0; i--) Marshal.FinalReleaseComObject(objects[i]);
        objects.Clear();
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create, IStream? template, out IStream stream);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PropertyBag
    {
        public uint Kind;
        public ushort Type;
        public ushort ClipboardFormat;
        public uint Hint;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public Guid ClassId;
    }

    // Unused methods retain the SDK's vtable order; they are never invoked.
    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        void CreateDecoderFromFilename();
        void CreateDecoderFromStream();
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        void CreateEncoder(ref Guid format, IntPtr vendor, out IWICBitmapEncoder encoder);
        void CreatePalette();
        void CreateFormatConverter();
        void CreateBitmapScaler();
        void CreateBitmapClipper();
        void CreateBitmapFlipRotator();
        void CreateStream();
        void CreateColorContext(out IWICColorContext context);
    }

    [ComImport, Guid("00000103-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapEncoder
    {
        void Initialize(IStream stream, uint cache);
        void GetContainerFormat();
        void GetEncoderInfo();
        void SetColorContexts();
        void SetPalette();
        void SetThumbnail();
        void SetPreview();
        void CreateNewFrame(out IWICBitmapFrameEncode frame, out IPropertyBag2 options);
        void Commit();
    }

    [ComImport, Guid("00000105-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapFrameEncode
    {
        void Initialize(IPropertyBag2 options);
        void SetSize(uint width, uint height);
        void SetResolution(double x, double y);
        void SetPixelFormat(ref Guid format);
        void SetColorContexts(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IWICColorContext[] contexts);
        void SetPalette();
        void SetThumbnail();
        void WritePixels(uint lines, uint stride, uint size, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] pixels);
        void WriteSource();
        void Commit();
    }

    [ComImport, Guid("3c613a02-34b2-44ea-9a7c-45aea9c6fd6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICColorContext
    {
        void InitializeFromFilename([MarshalAs(UnmanagedType.LPWStr)] string path);
        void InitializeFromMemory([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] profile, uint size);
    }

    [ComImport, Guid("22f55882-280b-11d0-a8a9-00a0c90c2004"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag2
    {
        void Read();
        void Write(uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] PropertyBag[] properties,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0, ArraySubType = UnmanagedType.Struct)] object[] values);
    }
}
