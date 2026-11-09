using BitMiracle.LibTiff.Classic;
using FilmLight.Core;

namespace FilmLight.Workflow;

internal sealed record ReconstructedFrame(LinearImage Image, float[] FilmBase);

internal static class ReferenceReconstruction
{
    internal static ReconstructedFrame Reconstruct(RollDocument roll, RollCapture frame,
        DevelopRecipe recipe, bool demoOnly, CancellationToken token)
    {
        ValidateRecipe(recipe);
        if (frame.Purpose != CapturePurpose.Frame || !roll.Captures.Any(c => c.Id == frame.Id))
            throw new InvalidOperationException("The selected frame must belong to this roll.");
        if (roll.FilmKind != recipe.FilmKind)
            throw new InvalidOperationException("Recipe film type must match the roll.");
        if (demoOnly && (!roll.IsDemo || !frame.IsDemo))
            throw new InvalidOperationException("DemoFrameProcessor only accepts explicitly synthetic rolls and captures. Select DarktableFrameProcessor for real input.");
        if (frame.IsDemo != roll.IsDemo)
            throw new InvalidOperationException("Real and synthetic captures cannot be mixed.");
        var light = Reference(roll, frame, CapturePurpose.LightReference);
        var basis = recipe.FilmKind == FilmKind.Positive ? null : Reference(roll, frame, CapturePurpose.FilmBaseReference);
        var normalized = Normalize(frame, light, recipe, roll.IsDemo, token);
        float[] filmBase = [1, 1, 1];
        if (basis is not null)
        {
            var baseImage = Normalize(basis, light, recipe, roll.IsDemo, token);
            Match(normalized, baseImage);
            var (left, top, width, height) = Crop(baseImage, recipe);
            double[] sums = [0, 0, 0];
            for (int y = top; y < top + height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = left; x < left + width; x++)
                for (int c = 0; c < 3; c++)
                    sums[c] += baseImage.Pixels[(y * baseImage.Width + x) * 3 + c];
            }
            double[] adjustments = [recipe.FilmBaseRed, recipe.FilmBaseGreen, recipe.FilmBaseBlue];
            for (int c = 0; c < 3; c++)
            {
                filmBase[c] = (float)(sums[c] / (width * height) * adjustments[c]);
                if (filmBase[c] is < .00001f or > 1.5f)
                    throw new InvalidDataException("Measured film base times recipe coefficient must be in [0.00001, 1.5]. Check the base/light references.");
            }
        }
        return new(normalized, filmBase);
    }

    private static RollCapture Reference(RollDocument roll, RollCapture frame, CapturePurpose purpose) =>
        roll.Captures.Where(c => c.Purpose == purpose && c.Mode == frame.Mode && c.IsDemo == frame.IsDemo &&
                c.CapturedAt <= frame.CapturedAt)
            .OrderBy(c => c.CapturedAt).ThenBy(c => c.Id).LastOrDefault()
        ?? throw new InvalidOperationException($"Missing matching {frame.Mode} {purpose} captured before this frame. Capture no-film light and clear-film base references with identical camera exposure, focus and registration.");

    private static LinearImage Normalize(RollCapture capture, RollCapture light, DevelopRecipe recipe,
        bool synthetic, CancellationToken token)
    {
        var channels = capture.Mode == CaptureMode.White ? new[] { LightChannel.White } :
            new[] { LightChannel.Red, LightChannel.Green, LightChannel.Blue };
        ValidateFiles(capture, channels);
        ValidateFiles(light, channels);
        LinearImage? result = null;
        foreach (var channel in channels)
        {
            var source = Read(capture.Files.Single(f => f.Channel == channel).Path, synthetic, token);
            var reference = Read(light.Files.Single(f => f.Channel == channel).Path, synthetic, token);
            Match(source, reference);
            if (result is null) result = new(source.Width, source.Height, new float[source.Pixels.Length]);
            Match(source, result);
            var (left, top, width, height) = Crop(source, recipe);
            int selected = channel switch { LightChannel.Red => 0, LightChannel.Green => 1, LightChannel.Blue => 2, _ => -1 };
            for (int i = 0; i < source.Pixels.Length; i++)
            {
                if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                if (selected >= 0 && i % 3 != selected) continue;
                int pixel = i / 3, x = pixel % source.Width, y = pixel / source.Width;
                if (x < left || x >= left + width || y < top || y >= top + height) continue;
                float l = reference.Pixels[i], s = source.Pixels[i];
                if (l <= .0001f || l >= .99999f || s >= .99999f)
                    throw new InvalidDataException("Dark or clipped capture/reference sample inside the crop. Crop out film-holder borders or recapture at a physically controlled matching exposure; no automatic exposure compensation is assumed.");
                result.Pixels[i] = s / l;
            }
        }
        return result ?? throw new InvalidDataException("Capture has no channels.");
    }

    private static LinearImage Read(string path, bool synthetic, CancellationToken token)
    {
        if (!Path.GetExtension(path).Equals(".tif", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(path).Equals(".tiff", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Linear reconstruction currently requires qualified linear RGB TIFFs. RAF decoding/black levels/exposure metadata are not implemented; X-T5 lossy RAF is unsupported. Do not substitute camera JPEGs.");
        using (var tif = Tiff.Open(path, "r") ?? throw new InvalidDataException($"Cannot decode {path}"))
            if ((tif.GetField(TiffTag.IMAGEDESCRIPTION)?[0].ToString() ?? "").Contains(
                    ProcessingImages.SyntheticLabel, StringComparison.Ordinal) != synthetic)
                throw new InvalidDataException("Synthetic image labels and roll provenance disagree. Never mix real inputs with a demo roll, or relabel synthetic images as real.");
        return ProcessingImages.ReadLinear(path, token, requireQualification: true);
    }

    private static void ValidateFiles(RollCapture capture, LightChannel[] channels)
    {
        if (capture.Files.Count != channels.Length ||
            channels.Any(c => capture.Files.Count(f => f.Channel == c) != 1))
            throw new InvalidDataException($"Capture {capture.Id} requires exactly one TIFF for each of: {string.Join(", ", channels)}.");
    }

    private static void Match(LinearImage a, LinearImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new InvalidDataException("Frame and references must have identical dimensions and registration.");
    }

    internal static DevelopRecipe Sample(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
        CalibrationTarget target, double x, double y, CancellationToken token)
    {
        var baseline = Reconstruct(roll, frame, recipe with
        {
            FilmBaseRed = 1, FilmBaseGreen = 1, FilmBaseBlue = 1
        }, roll.IsDemo, token);
        var (left, top, width, height) = Crop(baseline.Image, recipe);
        int px = left + (int)(x * width), py = top + (int)(y * height);
        var values = Average(baseline.Image);
        if (target == CalibrationTarget.Backlight)
        {
            // Correct residual tint after light-reference normalization, not the illuminant twice.
            return Checked(recipe with
            {
                RedGain = values[1] / values[0], GreenGain = 1, BlueGain = values[1] / values[2]
            });
        }
        return Checked(recipe with
        {
            FilmBaseRed = values[0] / baseline.FilmBase[0],
            FilmBaseGreen = values[1] / baseline.FilmBase[1],
            FilmBaseBlue = values[2] / baseline.FilmBase[2]
        });

        DevelopRecipe Checked(DevelopRecipe changed)
        {
            ValidateRecipe(changed);
            return changed with { Accepted = false, AcceptedAt = null };
        }

        double[] Average(LinearImage image)
        {
            double[] sum = [0, 0, 0];
            int count = 0;
            for (int sy = Math.Max(top, py - 2); sy <= Math.Min(top + height - 1, py + 2); sy++)
            for (int sx = Math.Max(left, px - 2); sx <= Math.Min(left + width - 1, px + 2); sx++)
            {
                token.ThrowIfCancellationRequested();
                for (int c = 0; c < 3; c++)
                {
                    float value = image.Pixels[(sy * image.Width + sx) * 3 + c];
                    if (!float.IsFinite(value) || value <= .0001f || value > 1.5f)
                        throw new InvalidDataException("The selected patch is dark or clipped. Pick evenly lit backlight / clear film away from edges, or recapture.");
                    sum[c] += value;
                }
                count++;
            }
            return sum.Select(v => v / count).ToArray();
        }
    }

    internal static void ValidateRecipe(DevelopRecipe r)
    {
        if (!Enum.IsDefined(r.FilmKind) || r.Revision < 1 ||
            !double.IsFinite(r.ExposureEv) || Math.Abs(r.ExposureEv) > 16 ||
            new[] { r.RedGain, r.GreenGain, r.BlueGain, r.FilmBaseRed, r.FilmBaseGreen, r.FilmBaseBlue }
                .Any(v => !double.IsFinite(v) || v <= 0 || v > 16) ||
            !double.IsFinite(r.BlackPoint) || !double.IsFinite(r.WhitePoint) ||
            r.BlackPoint < 0 || r.WhitePoint > 1 || r.WhitePoint <= r.BlackPoint ||
            new[] { r.CropLeft, r.CropTop, r.CropRight, r.CropBottom }.Any(v => !double.IsFinite(v) || v < 0 || v >= 1) ||
            r.CropLeft + r.CropRight >= 1 || r.CropTop + r.CropBottom >= 1)
            throw new ArgumentException("Invalid develop recipe: require finite exposure [-16,16], gains/base (0,16], 0 <= black < white <= 1, and fractional edge crops leaving a nonempty image.");
    }

    internal static LinearImage ApplyControls(LinearImage source, DevelopRecipe recipe, CancellationToken token)
    {
        var (left, top, width, height) = Crop(source, recipe);
        var output = new LinearImage(width, height, new float[width * height * 3]);
        double[] gain = [recipe.RedGain, recipe.GreenGain, recipe.BlueGain];
        double exposure = Math.Pow(2, recipe.ExposureEv);
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            for (int c = 0; c < 3; c++)
            {
                double v = source.Pixels[((top + y) * source.Width + left + x) * 3 + c];
                output.Pixels[(y * width + x) * 3 + c] = (float)Math.Clamp(
                    (v * exposure * gain[c] - recipe.BlackPoint) / (recipe.WhitePoint - recipe.BlackPoint), 0, 1);
            }
        }
        return output;
    }

    internal static (int Left, int Top, int Width, int Height) Crop(LinearImage source, DevelopRecipe recipe)
    {
        int left = (int)Math.Floor(source.Width * recipe.CropLeft);
        int top = (int)Math.Floor(source.Height * recipe.CropTop);
        int width = source.Width - left - (int)Math.Floor(source.Width * recipe.CropRight);
        int height = source.Height - top - (int)Math.Floor(source.Height * recipe.CropBottom);
        if (width <= 0 || height <= 0) throw new ArgumentException("Recipe crop leaves no pixels.");
        return (left, top, width, height);
    }
}
