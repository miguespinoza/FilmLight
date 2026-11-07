using System.Runtime.InteropServices;
using System.Text;

namespace FilmLight.Fujifilm;

public sealed class NativeCamera : INativeCamera
{
    private readonly Action<CameraActivity>? report;
    private readonly IntPtr library;
    private IntPtr camera;
    private bool initialized;
    private int quality;
    private int originalMedia;
    private bool mediaChanged;
    private int originalPriority;
    private bool priorityChanged;
    private bool disposed;

    public NativeCamera(string sdkDirectory, Action<CameraActivity>? report = null)
    {
        this.report = report;
        report?.Invoke(new(CameraStage.Loading, "Loading x64 Fujifilm SDK"));
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The Fujifilm Windows SDK requires the x64 camera helper.");
        var folder = Path.Combine(Path.GetFullPath(sdkDirectory), "REDISTRIBUTABLES", "Windows", "64bit");
        foreach (var file in new[] { "XAPI.dll", "XSDK.DAT", "FTLPTP.dll", "FTLPTPIP.dll" })
            if (!File.Exists(Path.Combine(folder, file)))
                throw new FileNotFoundException($"Missing SDK file: {file}. Select the SDK13410 root folder.");
        library = LoadLibraryEx(Path.Combine(folder, "XAPI.dll"), IntPtr.Zero, 0x1100);
        if (library == IntPtr.Zero)
            throw new CameraSdkException($"Cannot load x64 XAPI.dll (Windows error {Marshal.GetLastWin32Error()}).");
    }

    public CameraConnection Connect()
    {
        Check(Get<Initialize>("XSDK_Init")(library), "Init");
        initialized = true;
        report?.Invoke(new(CameraStage.Searching, "Searching for USB camera"));
        Check(Get<Detect>("XSDK_Detect")(1, IntPtr.Zero, IntPtr.Zero, out var count), "Detect USB");
        report?.Invoke(new(count == 0 ? CameraStage.Disconnected : CameraStage.Detected,
            count == 0 ? "No USB camera detected" : $"{count} USB camera(s) detected"));
        if (count != 1)
            throw new CameraSdkException(count == 0
                ? "No USB camera found. Connect the X-T5 in USB TETHER SHOOTING AUTO mode and close other tether apps."
                : "Connect only one USB camera before using Film Light.");
        Check(Get<Open>("XSDK_OpenEx")("ENUM:0", out camera, out var mode, IntPtr.Zero), "Open");
        if ((mode & 1) == 0)
            throw new CameraSdkException("Camera is not in tethered-shooting mode.");
        Check(Get<DeviceInfo>("XSDK_GetDeviceInfo")(camera, out var info), "GetDeviceInfo");
        var model = Decode(info.Product);
        if (!model.Contains("X-T5", StringComparison.OrdinalIgnoreCase))
            throw new CameraSdkException($"This integration is qualified for X-T5 only; connected camera is {model}.");
        report?.Invoke(new(CameraStage.Detected, $"{model} detected \u00b7 firmware {Decode(info.Firmware)}"));
        report?.Invoke(new(CameraStage.Configuring, $"{model} \u00b7 checking camera-priority triggering"));
        originalPriority = GetValue("XSDK_GetPriorityMode");
        if (originalPriority is not (1 or 2))
            throw new CameraSdkException($"Invalid camera priority mode: {originalPriority}.");
        if (originalPriority != 1)
        {
            if (!Capabilities("XSDK_CapPriorityMode").Contains(1))
                throw new CameraSdkException("Camera-priority operation is unsupported; Film Light will not take over exposure.");
            Check(Get<SetValue>("XSDK_SetPriorityMode")(camera, 1), "Set camera priority");
            priorityChanged = true;
        }
        if (GetValue("XSDK_GetPriorityMode") != 1)
            throw new CameraSdkException("Camera priority was not accepted. Exposure must remain controlled by the camera.");
        quality = GetProperty(0x2130);
        if (quality is not (1 or 2 or 3 or 4 or 5 or 6 or 7))
            throw new CameraSdkException($"Unsupported camera image quality {quality}.");
        var releases = Capabilities("XSDK_CapReleaseEx");
        if (!releases.Contains(0x10000) || !releases.Contains(0x20003) || !releases.Contains(1))
            throw new CameraSdkException("Camera does not advertise the required camera-priority release operations.");
        ValidateConfiguration();
        originalMedia = GetValue("XSDK_GetMediaRecord");
        var desiredMedia = quality == 1 ? 2 : quality is 4 or 5 or 7 ? 1 : 3;
        if (!Capabilities("XSDK_CapMediaRecord").Contains(desiredMedia))
            throw new CameraSdkException("Camera cannot save the selected RAW/JPEG format to its card during tethering.");
        Check(Get<SetValue>("XSDK_SetMediaRecord")(camera, desiredMedia), "SetMediaRecord");
        mediaChanged = true;
        if (GetValue("XSDK_GetMediaRecord") != desiredMedia)
            throw new CameraSdkException("Camera did not accept card recording. Check the card and camera settings.");
        var version = new byte[256];
        Check(Get<Version>("XSDK_GetVersionString")(version), "GetVersionString");
        return new(model, Decode(info.Serial), Decode(info.Firmware), Decode(version), quality);
    }

    public void ValidateConfiguration()
    {
        if (GetValue("XSDK_GetPriorityMode") != 1)
            throw new CameraSdkException("Camera priority changed. Reconnect; Film Light will not trigger using PC exposure settings.");
        // The X-T5 reports movie through drive mode (XT5_DRIVE_MODE_MOVIE); XSDK_GetMode fails on it
        // with UNSUPPORTED/UNKNOWN (both FATAL), so drive mode S is the still single-shot check.
        if (GetValue("XSDK_GetDriveMode") != 4)
            throw new CameraSdkException("Set the camera to still mode with the drive dial on S (single shot).");
        if (GetProperty(0x2202) != 1)
            throw new CameraSdkException("Set the focus selector to M. Manual focus avoids focus changes between RGB exposures.");
        if (GetProperty(0x2130) != quality)
            throw new CameraSdkException("Image quality changed. Disconnect and reconnect to reconfigure transfers.");
        if (quality != 1 && GetProperty(0x219F) != 7)
            throw new CameraSdkException("Select JPEG, not HEIF, in the camera image-format settings.");
        if ((GetValue("XSDK_GetReleaseStatus") & (0x200 | 0x400 | 0x100)) != 0)
            throw new CameraSdkException("Camera shutter controls are active. Wait until the camera is idle.");
        if (mediaChanged && GetValue("XSDK_GetMediaRecord") != (quality == 1 ? 2 : quality is 4 or 5 or 7 ? 1 : 3))
            throw new CameraSdkException("Camera-card recording changed. Reconnect before scanning.");
    }

    public ImageInfo ReadImageInfo()
    {
        Check(Get<ReadInfo>("XSDK_ReadImageInfo")(camera, out var info), "ReadImageInfo");
        return new(info.Format & 0xFF, info.Size);
    }

    public void CheckConnection()
    {
        Check(Get<DeviceInfo>("XSDK_GetDeviceInfo")(camera, out _), "Check USB connection");
    }

    public byte[] ReadImage(ImageInfo info)
    {
        if (info.Size is <= 0 or > 512 * 1024 * 1024)
            throw new CameraSdkException($"Invalid image size: {info.Size}.");
        var bytes = new byte[info.Size];
        Check(Get<ReadData>("XSDK_ReadImage")(camera, bytes, (uint)bytes.Length), "ReadImage");
        return bytes;
    }

    public void HalfPress()
    {
        var shots = 1;
        Check(Release(0x10000, ref shots, out var af), "HalfPress");
        if (af == 0)
            throw new CameraSdkException("Camera rejected the half-press. Check manual focus and exposure settings.");
    }

    public void FullPress()
    {
        var shots = 1;
        Check(Release(0x20003, ref shots, out _), "FullPress");
    }

    public void ReleaseHalfPress()
    {
        var shots = 1;
        Check(Release(1, ref shots, out _), "ReleaseHalfPress");
    }

    private int Release(int mode, ref int shots, out int af) =>
        Get<ReleaseCall>("XSDK_ReleaseEx")(camera, mode, ref shots, out af);

    // X-T5 GetProp with one integer-pointer argument; no guessed generic variadic binding.
    private int GetProperty(int code)
    {
        Check(Get<Property>("XSDK_GetProp")(camera, code, 1, out var value), $"GetProp 0x{code:X}");
        return value;
    }

    private int GetValue(string name)
    {
        Check(Get<GetValueCall>(name)(camera, out var value), name);
        return value;
    }

    private int[] Capabilities(string name)
    {
        var function = Get<Capability>(name);
        var count = 0;
        Check(function(camera, ref count, IntPtr.Zero), name);
        if (count is <= 0 or > 4096)
            throw new CameraSdkException($"Invalid capability count for {name}: {count}.");
        var buffer = Marshal.AllocHGlobal(checked(count * sizeof(int)));
        try
        {
            var capacity = count;
            Check(function(camera, ref count, buffer), name);
            if (count < 0 || count > capacity)
                throw new CameraSdkException($"Capability list changed for {name}.");
            var values = new int[count];
            Marshal.Copy(buffer, values, 0, count);
            return values;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private T Get<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private void Check(int result, string operation)
    {
        if (result == 0) return;
        var errorResult = Get<ErrorNumber>("XSDK_GetErrorNumber")(camera, out var api, out var error);
        if (errorResult != 0)
            throw new CameraSdkException($"{operation} failed (result {result}); SDK error lookup also failed (result {errorResult}).");
        var details = 0;
        var detailResult = 0;
        if (errorResult == 0 && error == 0x1017)
            detailResult = Get<GetValueCall>("XSDK_GetErrorDetails")(camera, out details);
        throw new CameraSdkException($"{operation} failed: API 0x{api:X}, error 0x{error:X}, " +
            (detailResult == 0 ? $"details 0x{details:X}." : $"detail lookup failed (result {detailResult})."), error);
    }

    private static string Decode(byte[] bytes)
    {
        var length = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, length < 0 ? bytes.Length : length).Trim();
    }

    public void Dispose()
    {
        if (disposed) return;
        try
        {
            if (camera != IntPtr.Zero)
            {
                report?.Invoke(new(CameraStage.Disconnecting, "Disconnecting USB camera"));
                try
                {
                    if (mediaChanged)
                        Check(Get<SetValue>("XSDK_SetMediaRecord")(camera, originalMedia), "Restore card recording");
                    if (priorityChanged)
                        Check(Get<SetValue>("XSDK_SetPriorityMode")(camera, originalPriority), "Restore tethering priority");
                }
                finally
                {
                    Check(Get<CameraCall>("XSDK_Close")(camera), "Close");
                    camera = IntPtr.Zero;
                    Thread.Sleep(600);
                }
            }
            if (initialized)
            {
                Check(Get<EmptyCall>("XSDK_Exit")(), "Exit");
                initialized = false;
            }
        }
        finally
        {
            // A failed Close/Exit is unsafe to unload. The helper process owns final teardown.
            if (camera == IntPtr.Zero && !initialized && !FreeLibrary(library))
                throw new CameraSdkException($"Could not unload XAPI.dll (Windows error {Marshal.GetLastWin32Error()}).");
            disposed = true;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NativeImageInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Name;
        public int Format, Size, Height, Width, BitDepth, PreviewSize;
        public IntPtr Image;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NativeDeviceInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] Vendor;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] Manufacturer;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] Product;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] Firmware;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] DeviceType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] Serial;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] Framework;
        public byte DeviceId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] DeviceName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] YNo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Initialize(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EmptyCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CameraCall(IntPtr camera);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Detect(int interfaces, IntPtr address, IntPtr model, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Open([MarshalAs(UnmanagedType.LPStr)] string device, out IntPtr camera, out int mode, IntPtr option);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeviceInfo(IntPtr camera, out NativeDeviceInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadInfo(IntPtr camera, out NativeImageInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadData(IntPtr camera, [Out] byte[] data, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetValueCall(IntPtr camera, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetValue(IntPtr camera, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Capability(IntPtr camera, ref int count, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReleaseCall(IntPtr camera, int mode, ref int shots, out int af);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Property(IntPtr camera, int code, int parameterCount, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Version([Out] byte[] version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ErrorNumber(IntPtr camera, out int api, out int error);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}
