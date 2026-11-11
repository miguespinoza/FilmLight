namespace FilmLight.Workflow;

internal sealed class SprocketDetectionStep : ICalibrationStep
{
    internal const int DetectionEdge = 2000;

    public async Task<CalibrationState> ApplyAsync(CalibrationContext context, CalibrationState state, CancellationToken token)
    {
        var image = await context.Source.GetImageAsync(DetectionEdge, token, context.Progress).ConfigureAwait(false);
        context.Progress?.Report(new(context.Frame.Id, "Finding sprocket samples",
            "Detecting repeated perforations in the uncropped linear input and measuring light / clear film."));
        return await Task.Run(() => SprocketCalibration.DetectWithEvidence(image, context.Frame.Id,
            context.FilmKind != FilmKind.Positive, token, context.Diagnostic), token).ConfigureAwait(false);
    }
}

internal sealed class ClippingVerificationStep : ICalibrationStep
{
    internal const double HeadroomThreshold = .9;

    public async Task<CalibrationState> ApplyAsync(CalibrationContext context, CalibrationState state, CancellationToken token)
    {
        var light = await VerifyAsync(state.Picture.Light, state.LightEvidence, CalibrationTarget.Backlight).ConfigureAwait(false);
        var basis = await VerifyAsync(state.Picture.FilmBase, state.FilmBaseEvidence, CalibrationTarget.FilmBase).ConfigureAwait(false);
        if (light == state.Picture.Light && basis == state.Picture.FilmBase) return state;
        return state with { Picture = state.Picture with { Light = light, FilmBase = basis } };

        async Task<PictureSample?> VerifyAsync(PictureSample? sample, SampleEvidence? evidence, CalibrationTarget target)
        {
            if (sample is null || evidence is null)
                return sample;
            context.Diagnostic(FormattableString.Invariant(
                $"{target} peak={evidence.Peak:F6}; measured-edge={evidence.MeasuredLongEdge}; verification={(evidence.Peak >= HeadroomThreshold ? "full-resolution" : "skipped (headroom)")}"));
            if (evidence.Peak < HeadroomThreshold) return sample;

            // Full-resolution dimensions are needed to turn the evidence extents into a pixel radius.
            var center = await context.Source.MeasureAsync(sample.X, sample.Y, 0, token, context.Progress).ConfigureAwait(false);
            if (evidence.MeasuredLongEdge >= Math.Max(center.SourceWidth, center.SourceHeight)) return sample;
            double halfWidth = evidence.HalfWidth * center.SourceWidth;
            double halfHeight = evidence.HalfHeight * center.SourceHeight;
            int extent = (int)Math.Ceiling(Math.Max(halfWidth, halfHeight));
            int radius = Math.Clamp(extent, 0, 256);
            var patch = await context.Source.MeasureAsync(sample.X, sample.Y, radius, token, context.Progress).ConfigureAwait(false);
            if (radius >= extent && !patch.IsClipped && !patch.IsDarkOrInvalid) return sample;

            // Aggregate flags mean "any pixel"; apply the shared patch rule before rejecting.
            var values = new List<double>[] { [], [], [] };
            foreach (var (sx, sy) in PatchRule.Points(sample.X, sample.Y, evidence.HalfWidth, evidence.HalfHeight))
            {
                var point = await context.Source.MeasureAsync(sx, sy, 0, token, context.Progress).ConfigureAwait(false);
                for (int c = 0; c < 3; c++)
                    values[c].Add(point.Mean[c]);
            }
            var result = PatchRule.Evaluate(values, allowClipped: target == CalibrationTarget.Backlight);
            context.Diagnostic($"{target} full-resolution verification: high=({string.Join(", ", result.High)}); low=({string.Join(", ", result.Low)}); points={values[0].Count}");
            if (result.Rejection is not null)
                throw SprocketCalibration.InvalidPatch(target, result.High.Any(v => v > 0), result.Low.Any(v => v > 0));
            return target == CalibrationTarget.Backlight && result.High.Any(v => v > 0)
                ? sample with { IsClipped = true } : sample;
        }
    }
}

internal sealed class ManualPickStep(CalibrationTarget target, double x, double y, DevelopRecipe recipe) : ICalibrationStep
{
    public async Task<CalibrationState> ApplyAsync(CalibrationContext context, CalibrationState state, CancellationToken token)
    {
        state.Picture.Validate(context.Roll, context.Frame.Mode, context.Frame.Id);
        double sourceX = recipe.CropLeft + x * (1 - recipe.CropLeft - recipe.CropRight);
        double sourceY = recipe.CropTop + y * (1 - recipe.CropTop - recipe.CropBottom);
        var stats = await context.Source.MeasureAsync(sourceX, sourceY, 2, token, context.Progress).ConfigureAwait(false);
        if (stats.IsDarkOrInvalid)
            throw new InvalidDataException("The selected patch is dark or invalid. Pick evenly lit backlight / clear film away from edges.");
        if (stats.IsClipped && target != CalibrationTarget.Backlight)
            throw new InvalidDataException("The selected film-base patch is clipped. Pick unclipped clear film away from edges.");
        var sample = new PictureSample(context.Frame.Id, sourceX, sourceY, stats.Mean[0], stats.Mean[1], stats.Mean[2])
        {
            SourceWidth = stats.SourceWidth, SourceHeight = stats.SourceHeight, IsClipped = stats.IsClipped
        };
        sample.Validate(allowClipped: target == CalibrationTarget.Backlight);
        var evidence = new SampleEvidence(2.0 / stats.SourceWidth, 2.0 / stats.SourceHeight,
            Math.Max(stats.SourceWidth, stats.SourceHeight), stats.Maximum.Max());
        return target == CalibrationTarget.Backlight
            ? state with { Picture = state.Picture with { Light = sample }, LightEvidence = evidence }
            : state with { Picture = state.Picture with { FilmBase = sample }, FilmBaseEvidence = evidence };
    }
}
