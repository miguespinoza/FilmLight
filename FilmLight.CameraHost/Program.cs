using FilmLight.Fujifilm;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        using var owner = new Mutex(false, @"Local\FilmLight.FujifilmSdkOwner");
        bool acquired;
        try { acquired = owner.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            Console.Error.WriteLine("Another Film Light camera helper owns the SDK. Close it and other tether apps.");
            return 2;
        }
        var comResult = CoInitializeEx(IntPtr.Zero, 2);
        if (comResult < 0)
        {
            Console.Error.WriteLine($"COM initialization failed: 0x{comResult:X}.");
            owner.ReleaseMutex();
            return 3;
        }
        using var requests = new BlockingCollection<(CameraRequest Request, CancellationTokenSource Cancellation)>();
        var cancellations = new ConcurrentDictionary<string, CancellationTokenSource>();
        var reader = Task.Run(() =>
        {
            try
            {
                while (Console.ReadLine() is { } line)
                {
                    var request = JsonSerializer.Deserialize<CameraRequest>(line)
                        ?? throw new InvalidDataException("Empty camera request.");
                    if (request.Command == "cancel")
                    {
                        if (cancellations.TryGetValue(request.Id, out var cancellation)) cancellation.Cancel();
                        continue;
                    }
                    var token = new CancellationTokenSource();
                    if (!cancellations.TryAdd(request.Id, token))
                    {
                        token.Dispose();
                        throw new InvalidDataException("Duplicate request identifier.");
                    }
                    requests.Add((request, token));
                }
                foreach (var cancellation in cancellations.Values) cancellation.Cancel();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine($"Camera protocol input failed: {ex.Message}");
                foreach (var cancellation in cancellations.Values) cancellation.Cancel();
            }
            finally { requests.CompleteAdding(); }
        });
        CameraSession? session = null;
        var activeRequestId = "";
        void WriteResponse(CameraResponse response)
        {
            Console.WriteLine(JsonSerializer.Serialize(response));
            Console.Out.Flush();
        }
        void Report(CameraActivity activity) => WriteResponse(new(activeRequestId, Activity: activity));
        try
        {
            foreach (var (request, cancellation) in requests.GetConsumingEnumerable())
            {
                activeRequestId = request.Id;
                CameraResponse response;
                try
                {
                    switch (request.Command)
                    {
                        case "connect":
                            if (session is not null) throw new InvalidOperationException("Camera already connected.");
                            if (request.SdkDirectory is null || request.OutputDirectory is null)
                                throw new InvalidDataException("SDK and output directories are required.");
                            session = new(new NativeCamera(request.SdkDirectory, Report), request.OutputDirectory, report: Report);
                            response = new(request.Id, Connection: session.Connect());
                            break;
                        case "capture":
                            if (session is null || request.ScanId is null)
                                throw new InvalidOperationException("Camera is not connected or scan ID is missing.");
                            var result = session.Capture(request.Channel, request.ScanId, cancellation.Token);
                            response = new(request.Id, FilePaths: result.FilePaths.ToArray());
                            break;
                        case "disconnect":
                            session?.Dispose();
                            session = null;
                            response = new(request.Id);
                            break;
                        case "status":
                            if (session is null) throw new InvalidOperationException("Camera is not connected.");
                            response = new(request.Id, Busy: !session.CheckConnection());
                            break;
                        default:
                            throw new InvalidDataException($"Unknown camera command: {request.Command}");
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    response = new(request.Id, Canceled: true, Faulted: session?.Faulted ?? false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
                    or ArgumentException or TimeoutException or PlatformNotSupportedException or EntryPointNotFoundException
                    or BadImageFormatException or DllNotFoundException)
                {
                    Console.Error.WriteLine($"{request.Command}/{request.Id}: {ex}");
                    response = new(request.Id, Error: ex.Message, Faulted: true);
                }
                finally
                {
                    cancellations.TryRemove(request.Id, out _);
                    cancellation.Dispose();
                }
                WriteResponse(response);
            }
            return 0;
        }
        finally
        {
            try { session?.Dispose(); }
            catch (CameraSdkException ex) { Console.Error.WriteLine($"Camera teardown failed: {ex}"); }
            CoUninitialize();
            owner.ReleaseMutex();
            GC.KeepAlive(reader);
        }
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
