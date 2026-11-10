using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace FilmLight.Workflow;

internal sealed class ReconstructStep(bool isDemo) : IDevelopStep
{
    public async Task<DevelopState> ApplyAsync(DevelopContext context, DevelopState state, CancellationToken token)
    {
        var reconstruction = await ReferenceReconstruction.ReconstructAsync(context.Sources, context.Roll, context.Frame,
            context.Recipe, isDemo, context.Intent == DevelopIntent.Preview
                ? ReferenceReconstruction.PreviewSize(context.Recipe) : FrameSources.FullResolution, token, context.Progress).ConfigureAwait(false);
        return new(reconstruction.Image, reconstruction.FilmBase, context.Recipe);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class PreviewCropStep : IDevelopStep
{
    public Task<DevelopState> ApplyAsync(DevelopContext context, DevelopState state, CancellationToken token)
    {
        context.Progress?.Report(new(context.Frame.Id, "Preparing preview", "Cropping and sizing the image, then preparing the source view."));
        var image = ProcessingImages.CropForPreview(state.Image, context.Recipe, token);
        var controls = context.Recipe with { CropLeft = 0, CropTop = 0, CropRight = 0, CropBottom = 0 };
        ProcessingImages.WriteSourcePreview(Path.Combine(context.Staging, "source-preview.jpg"), image, controls, token);
        return Task.FromResult(state with { Image = image, Controls = controls });
    }
}

internal sealed class MonochromeBaseStep : IDevelopStep
{
    public Task<DevelopState> ApplyAsync(DevelopContext context, DevelopState state, CancellationToken token)
    {
        // negadoctor's B&W mode uses Dmin[0], so remove colored substrate before inversion.
        for (int i = 0; i < state.Image.Pixels.Length; i += 3)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            float gray = (state.Image.Pixels[i] / state.FilmBase[0] +
                state.Image.Pixels[i + 1] / state.FilmBase[1] + state.Image.Pixels[i + 2] / state.FilmBase[2]) / 3;
            state.Image.Pixels[i] = state.Image.Pixels[i + 1] = state.Image.Pixels[i + 2] = gray;
        }
        return Task.FromResult(state with { FilmBase = [1, 1, 1] });
    }
}

internal sealed class DarktableInversionStep(string cliPath) : IDevelopStep
{
    private readonly DarktableConversion conversion = new(cliPath);

    public async Task<DevelopState> ApplyAsync(DevelopContext context, DevelopState state, CancellationToken token)
    {
        void Report(string stage, string message) => context.Progress?.Report(new(context.Frame.Id, stage, message));
        string input = Path.Combine(context.Staging, "normalized-linear.tif");
        Report("Preparing positive conversion", "Writing normalized input and the develop recipe.");
        ProcessingImages.WriteTiff(input, state.Image, true,
            (context.Roll.IsDemo ? ProcessingImages.SyntheticLabel + "; " : "") + ProcessingImages.LinearLabel,
            token, ProcessingImages.LinearRec2020Profile());
        string xmp = Path.Combine(context.Staging, "develop.xmp");
        NegadoctorXmp.Write(xmp, context.Recipe, state.FilmBase, context.Recipe.FilmKind != FilmKind.Positive);
        PreviewInversionCache? cache = null;
        string? key = null;
        if (context.Intent == DevelopIntent.Preview)
        {
            cache = new(Path.Combine(Path.GetDirectoryName(context.Staging)!, ".develop-cache"));
            key = PreviewInversionCache.Key(state.Image, await File.ReadAllBytesAsync(xmp, token).ConfigureAwait(false),
                cliPath, await context.Sources.GetVersionAsync(token).ConfigureAwait(false));
            if (cache.TryRead(key, state.Image.Width, state.Image.Height, token, out var cached))
            {
                Report("Reusing positive conversion", "Using the cached darktable inversion; applying the current develop settings.");
                return state with { Image = cached! };
            }
        }
        string rendered = Path.Combine(context.Staging, "developed-linear.tif");
        Report(context.Recipe.FilmKind == FilmKind.Positive ? "Developing positive" : "Inverting negative",
            context.Recipe.FilmKind == FilmKind.Positive ? "darktable is developing the positive image."
                : "darktable is converting the negative to a positive with negadoctor.");
        string log = await conversion.RunAsync(input, xmp, rendered, "LIN_REC2020", 32, context.Staging, "develop", token).ConfigureAwait(false);
        if (context.Recipe.FilmKind != FilmKind.Positive && !Regex.IsMatch(log, @"(?im)^\s*\u25cf\s+negadoctor\b"))
            throw new InvalidDataException("darktable did not report enabled negadoctor in its verbose history. No positive export will be published; inspect develop logs/XMP.");
        Report("Applying develop settings", "Reading the converted image and applying exposure, color and levels.");
        var image = ProcessingImages.ReadLinear(rendered, token);
        if (image.Width != state.Image.Width || image.Height != state.Image.Height)
            throw new InvalidDataException("Unexpected darktable geometry; disable crop/orientation in the calibrated XMP.");
        if (cache is not null) cache.Write(key!, image, token);
        return state with { Image = image };
    }
}

internal sealed class DemoInversionStep : IDevelopStep
{
    public Task<DevelopState> ApplyAsync(DevelopContext context, DevelopState state, CancellationToken token)
    {
        var image = state.Image;
        if (context.Recipe.FilmKind != FilmKind.Positive)
        {
            for (int i = 0; i < image.Pixels.Length; i++)
            {
                if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                image.Pixels[i] = (float)Math.Clamp(Math.Log10(state.FilmBase[i % 3] / Math.Max(image.Pixels[i], 1e-10)) / 1.6, 0, 1);
            }
            if (context.Recipe.FilmKind == FilmKind.BlackAndWhiteNegative)
                for (int i = 0; i < image.Pixels.Length; i += 3)
                {
                    if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                    float gray = (image.Pixels[i] + image.Pixels[i + 1] + image.Pixels[i + 2]) / 3;
                    image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = gray;
                }
        }
        return Task.FromResult(state);
    }
}

internal sealed class DevelopControlsStep : IDevelopStep
{
    public Task<DevelopState> ApplyAsync(DevelopContext context, DevelopState state, CancellationToken token) =>
        Task.FromResult(state with { Image = ReferenceReconstruction.ApplyControls(state.Image, state.Controls, token) });
}

[SupportedOSPlatform("windows")]
internal sealed class PreviewEncodeStep : IDevelopStep
{
    public Task<DevelopState> ApplyAsync(DevelopContext context, DevelopState state, CancellationToken token)
    {
        context.Progress?.Report(new(context.Frame.Id, "Saving positive images", "Writing the lossless sRGB preview."));
        ProcessingImages.WritePreviewPng(ProcessingPublication.Paths(context.Staging, context.Roll.IsDemo, context.Intent)[0],
            state.Image, token);
        return Task.FromResult(state);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ExportEncodeStep : IDevelopStep
{
    public Task<DevelopState> ApplyAsync(DevelopContext context, DevelopState state, CancellationToken token)
    {
        void Report(string stage, string message) => context.Progress?.Report(new(context.Frame.Id, stage, message));
        Report("Saving positive images", "Writing the TIFF master and JPEG companion.");
        var paths = ProcessingPublication.Paths(context.Staging, context.Roll.IsDemo, context.Intent);
        Report("Encoding TIFF", "Saving the 16-bit linear ProPhoto RGB master in process.");
        Report("Encoding JPEG", "Saving the sRGB JPEG companion in process (quality 95, 4:4:4).");
        Parallel.Invoke(new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 2 },
            () => ProcessingImages.WriteProPhotoTiff(paths[0], state.Image, context.Roll.IsDemo, token),
            () => ProcessingImages.WriteExportJpeg(paths[1], state.Image, token));
        Report("Checking color profiles", "Validating the encoded master and preview.");
        ProcessingImages.ValidateMaster(paths[0], state.Image.Width, state.Image.Height, true, token);
        ProcessingImages.ValidateJpeg(paths[1], state.Image.Width, state.Image.Height, true);
        return Task.FromResult(state);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class DemoExportEncodeStep : IDevelopStep
{
    public Task<DevelopState> ApplyAsync(DevelopContext context, DevelopState state, CancellationToken token)
    {
        context.Progress?.Report(new(context.Frame.Id, "Saving positive images", "Writing the TIFF master and JPEG companion."));
        var paths = ProcessingPublication.Paths(context.Staging, true, context.Intent);
        ProcessingImages.WriteTiff(paths[0], state.Image, false, ProcessingImages.SyntheticLabel, token);
        ProcessingImages.WriteJpeg(paths[1], state.Image, token);
        return Task.FromResult(state);
    }
}

internal sealed class DarktableConversion(string cliPath)
{
    private readonly DarktableProcess cli = new(cliPath);
    internal const string InversionSettings = "LIN_REC2020;32;RELATIVE_COLORIMETRIC;tiff/compress=0;tiff/shortfile=0;CPU;presets=0";

    internal Task<string> RunAsync(string input, string xmp, string output, string profile,
        int bits, string directory, string log, CancellationToken token)
    {
        string config = Path.Combine(directory, "config");
        Directory.CreateDirectory(config);
        // darktable treats backslashes in output names as pattern escapes; use job-local filenames.
        return cli.RunAsync([Path.GetFileName(input), Path.GetFileName(xmp), Path.GetFileName(output),
            "--icc-type", profile, "--icc-intent", "RELATIVE_COLORIMETRIC",
            "--apply-custom-presets", "0", "--verbose", "--core", "--configdir", config,
            "--cachedir", Path.Combine(directory, "cache"), "--disable-opencl",
            "--conf", $"plugins/imageio/format/tiff/bpp={bits}",
            "--conf", "plugins/imageio/format/tiff/compress=0",
            "--conf", "plugins/imageio/format/tiff/shortfile=0",
            "--conf", "plugins/imageio/format/jpeg/quality=95",
            "--conf", "write_sidecar_files=never"], directory, log, token);
    }
}
