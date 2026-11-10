using System.Diagnostics;
using System.Runtime.Versioning;
using FilmLight.Workflow;
using Xunit;
using Xunit.Abstractions;

namespace FilmLight.Tests;

[SupportedOSPlatform("windows")]
[Collection("Export encoding")]
public sealed class PreviewPerformanceTests(ITestOutputHelper output)
{
    [RafFact]
    public async Task RealRafPreviewTimingsAndLaunchCounts()
    {
        string directory = Path.Combine(Path.GetTempPath(), "FilmLight-preview-performance-" + Guid.NewGuid().ToString("N"));
        string cli = Environment.GetEnvironmentVariable("FILMLIGHT_TEST_DARKTABLE")
            ?? @"C:\Program Files\darktable\bin\darktable-cli.exe";
        try
        {
            var frame = new RollCapture(Guid.NewGuid(), 1, CapturePurpose.Frame, CaptureMode.White,
                [new(FilmLight.Core.LightChannel.White, Environment.GetEnvironmentVariable("FILMLIGHT_TEST_RAF")!)], false, DateTimeOffset.UtcNow);
            var roll = new RollDocument { Captures = [frame] };
            var recipe = new DevelopRecipe { FrameId = frame.Id, PictureCalibration = new() };
            using var sources = new FrameSources(Path.Combine(directory, "camera-cache"), cli);
            await sources.Open(roll, frame).GetImageAsync(1600, default);
            var processor = new DarktableFrameProcessor(cli, sources);
            foreach (var (name, settings, launches) in new[]
            {
                ("Cold inversion", recipe, 1),
                ("Controls only", recipe with { ExposureEv = .5, RedGain = 1.1 }, 0),
                ("Base change", recipe with { FilmBaseRed = 1.1 }, 1)
            })
            {
                var clock = Stopwatch.StartNew();
                var result = await processor.PreviewAsync(roll, frame, settings, directory, null, default);
                output.WriteLine($"{name}: {clock.Elapsed.TotalSeconds:F3}s");
                Assert.Equal(launches, Directory.GetFiles(Path.GetDirectoryName(result.ImagePath)!, "*.command.txt").Length);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class RafFactAttribute : FactAttribute
    {
        public RafFactAttribute()
        {
            string cli = Environment.GetEnvironmentVariable("FILMLIGHT_TEST_DARKTABLE")
                ?? @"C:\Program Files\darktable\bin\darktable-cli.exe";
            if (!File.Exists(cli) || !File.Exists(Environment.GetEnvironmentVariable("FILMLIGHT_TEST_RAF")))
                Skip = "Requires installed darktable and FILMLIGHT_TEST_RAF.";
        }
    }
}
