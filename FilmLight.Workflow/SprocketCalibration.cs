using System.Globalization;

namespace FilmLight.Workflow;

internal sealed record SprocketLocations(double LightX, double LightY, double FilmBaseX, double FilmBaseY);

internal static class PatchRule
{
    private const int GridEdge = 15;
    private const double Maximum = .99999, Minimum = .0001, BadFraction = .05, SpreadFraction = .15;

    internal sealed record Result(double[] Values, double Peak, int[] High, int[] Low, string? Rejection);

    internal static IEnumerable<(double X, double Y)> Points(double x, double y, double halfWidth, double halfHeight)
    {
        for (int gy = 0; gy < GridEdge; gy++)
            for (int gx = 0; gx < GridEdge; gx++)
                yield return (x - halfWidth + 2 * halfWidth * (gx + .5) / GridEdge,
                    y - halfHeight + 2 * halfHeight * (gy + .5) / GridEdge);
    }

    internal static Result Evaluate(List<double>[] samples, bool measureColor = true, bool allowClipped = false)
    {
        foreach (var channel in samples) channel.Sort();
        int[] high = samples.Select(s => s.Count(v => v >= Maximum)).ToArray();
        int[] low = samples.Select(s => s.Count(v => !double.IsFinite(v) || v <= Minimum)).ToArray();
        double[] values = samples.Select(s => s[s.Count / 2]).ToArray();
        string? rejection = null;
        for (int c = 0; c < samples.Length; c++)
        {
            if (measureColor && (low[c] + (allowClipped ? 0 : high[c])) > samples[c].Count * BadFraction)
                rejection = $"channel={c}; at/above maximum={high[c]}/{samples[c].Count}; at/below minimum={low[c]}/{samples[c].Count}";
            else if (!double.IsFinite(values[c]) || values[c] <= Minimum || (measureColor && !allowClipped && values[c] >= Maximum)
                || samples[c][samples[c].Count * 9 / 10] - samples[c][samples[c].Count / 10] > values[c] * SpreadFraction)
                rejection = $"channel={c}; dark, clipped or nonuniform median/spread";
            if (rejection is not null) break;
        }
        return new(values, samples.Max(s => s[^1]), high, low, rejection);
    }
}

internal static class SprocketCalibration
{
    private sealed record Hole(int Left, int Top, int Width, int Height)
    {
        internal double X => Left + Width / 2.0;
        internal double Y => Top + Height / 2.0;
    }

    private sealed record PhysicalRow(bool Horizontal, HashSet<Hole> Holes);

    internal static PictureCalibration Detect(LinearImage image, Guid frameId, bool needsBase,
        CancellationToken token, Action<string>? diagnostic = null) =>
        DetectWithEvidence(image, frameId, needsBase, token, diagnostic).Picture;

    internal static CalibrationState DetectWithEvidence(LinearImage image, Guid frameId, bool needsBase,
        CancellationToken token, Action<string>? diagnostic = null) =>
        Analyze(image, frameId, needsBase, token, measureColor: true, diagnostic);

    internal static SprocketLocations Locate(LinearImage image, CancellationToken token)
    {
        var result = Analyze(image, Guid.Empty, true, token, measureColor: false, null);
        return new(result.Picture.Light!.X, result.Picture.Light.Y, result.Picture.FilmBase!.X, result.Picture.FilmBase.Y);
    }

    private static CalibrationState Analyze(LinearImage image, Guid frameId, bool needsBase,
        CancellationToken token, bool measureColor, Action<string>? diagnostic)
    {
        token.ThrowIfCancellationRequested();
        diagnostic?.Invoke($"Linear input: {image.Width} x {image.Height}; measure-color={measureColor}; needs-base={needsBase}");
        var loggedPatches = new HashSet<(CalibrationTarget Target, double X, double Y)>();
        var evidence = new Dictionary<PictureSample, SampleEvidence>();
        int maxHoles = 0, totalRows = 0, totalRejectedLight = 0, totalRejectedBase = 0;
        bool highBacklight = false, darkBacklight = false;
        double scale = Math.Min(1, 1200.0 / Math.Max(image.Width, image.Height));
        int width = Math.Max(1, (int)(image.Width * scale)), height = Math.Max(1, (int)(image.Height * scale));
        var brightness = new float[width * height];
        var histogram = new int[1024];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int i = ((int)((y + .5) * image.Height / height) * image.Width
                    + (int)((x + .5) * image.Width / width)) * 3;
                float v = .2627f * image.Pixels[i] + .6780f * image.Pixels[i + 1] + .0593f * image.Pixels[i + 2];
                brightness[y * width + x] = v;
                histogram[(int)(Math.Clamp(v, 0, 1) * 1023)]++;
            }
        }
        int cumulative = 0, percentile = 0;
        while (percentile < 1023 && cumulative + histogram[percentile] < brightness.Length * .98)
            cumulative += histogram[percentile++];
        var candidates = new List<PictureCalibration>();
        foreach (float fraction in new[] { .85f, .93f, .98f })
        {
            int rows = 0, rejectedLight = 0, rejectedBase = 0, rejectedConsistency = 0;
            float threshold = percentile / 1023f * fraction;
            var visited = new bool[brightness.Length];
            var queue = new Queue<int>();
            var holes = new List<Hole>();
            for (int start = 0; start < brightness.Length; start++)
            {
                if ((start & 65535) == 0) token.ThrowIfCancellationRequested();
                if (visited[start] || brightness[start] <= threshold) continue;
                visited[start] = true;
                queue.Enqueue(start);
                int left = width, top = height, right = 0, bottom = 0, count = 0;
                while (queue.TryDequeue(out int pixel))
                {
                    if ((count++ & 65535) == 0) token.ThrowIfCancellationRequested();
                    int x = pixel % width, y = pixel / width;
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    if (x > 0) Visit(pixel - 1);
                    if (x + 1 < width) Visit(pixel + 1);
                    if (y > 0) Visit(pixel - width);
                    if (y + 1 < height) Visit(pixel + width);
                }
                int w = right - left + 1, h = bottom - top + 1;
                bool cropped = left == 0 || top == 0 || right == width - 1 || bottom == height - 1;
                if (w >= 5 && h >= 5 && w <= width * .15 && h <= height * .15
                    && w / (double)h >= (cropped ? .2 : .5)
                    && w / (double)h <= (cropped ? 5 : 2) && count >= w * h * .65)
                    holes.Add(new(left, top, w, h));

                void Visit(int pixel)
                {
                    if (visited[pixel] || brightness[pixel] <= threshold) return;
                    visited[pixel] = true;
                    queue.Enqueue(pixel);
                }
            }

            maxHoles = Math.Max(maxHoles, holes.Count);
            if (holes.Count > 200)
            {
                diagnostic?.Invoke($"Threshold={threshold:F6}; candidate-holes={holes.Count}; too many competing regions.");
                throw Failure("Too many competing bright regions.");
            }
            var physicalRows = new List<PhysicalRow>();
            foreach (bool horizontal in new[] { true, false })
                foreach (var seed in holes)
                {
                    token.ThrowIfCancellationRequested();
                    double across = horizontal ? seed.Y / height : seed.X / width;
                    if (across is > .25 and < .75) continue;
                    var row = holes.Where(h => (horizontal
                            ? h.Left > 0 && h.Left + h.Width < width
                            : h.Top > 0 && h.Top + h.Height < height)
                        && Math.Abs(Cross(h) - Cross(seed)) <= CrossSize(seed) * .5
                        && Math.Abs(h.Width / (double)seed.Width - 1) <= .25
                        && Math.Abs(h.Height / (double)seed.Height - 1) <= .25)
                        .OrderBy(Along).ToArray();
                    if (row.Length < 3) continue;
                    double[] spacings = row.Zip(row.Skip(1), (a, b) => Along(b) - Along(a)).ToArray();
                    double pitch = Median(spacings);
                    if (pitch < AlongSize(seed) * 1.25 || pitch > AlongSize(seed) * 3.5
                        || spacings.Any(s => Math.Abs(s / pitch - 1) > .15)) continue;
                    rows++;
                    // Seed hypotheses sharing holes describe one physical row, not independent evidence.
                    var overlaps = physicalRows.Where(r => r.Horizontal == horizontal && r.Holes.Overlaps(row)).ToArray();
                    var merged = new PhysicalRow(horizontal, row.ToHashSet());
                    foreach (var overlap in overlaps)
                    {
                        merged.Holes.UnionWith(overlap.Holes);
                        physicalRows.Remove(overlap);
                    }
                    physicalRows.Add(merged);

                    double Along(Hole h) => horizontal ? h.X : h.Y;
                    double Cross(Hole h) => horizontal ? h.Y : h.X;
                    double AlongSize(Hole h) => horizontal ? h.Width : h.Height;
                    double CrossSize(Hole h) => horizontal ? h.Height : h.Width;
                }
            foreach (var physical in physicalRows.OrderBy(r => r.Horizontal ? 0 : 1)
                .ThenBy(r => r.Holes.Average(h => r.Horizontal ? h.Y / height : h.X / width)))
            {
                bool horizontal = physical.Horizontal;
                double Along(Hole h) => horizontal ? h.X : h.Y;
                double Cross(Hole h) => horizontal ? h.Y : h.X;
                double AlongSize(Hole h) => horizontal ? h.Width : h.Height;
                var row = physical.Holes.OrderBy(Along).ToArray();
                double pitch = Median(row.Zip(row.Skip(1), (a, b) => Along(b) - Along(a)).ToArray());
                var light = new List<PictureSample>();
                var basis = new List<PictureSample>();
                var validHoles = new List<Hole>();
                foreach (var hole in row)
                {
                    var sample = Measure(hole.X, hole.Y, hole.Width * .5, hole.Height * .5, CalibrationTarget.Backlight);
                    if (sample is not null) { light.Add(sample); validHoles.Add(hole); }
                    else diagnostic?.Invoke(FormattableString.Invariant(
                        $"Excluded hole ({hole.X / width:F6}, {hole.Y / height:F6}) from physical row: invalid backlight patch."));
                }
                diagnostic?.Invoke($"Physical row: orientation={(horizontal ? "horizontal" : "vertical")}; edge={(horizontal
                    ? row[0].Y < height / 2.0 ? "top" : "bottom" : row[0].X < width / 2.0 ? "left" : "right")}; holes={row.Length}; valid-holes={light.Count}");
                // Excluded holes leave integer pitch gaps; they must not invalidate an otherwise regular row.
                if (light.Count < 3 || validHoles.Zip(validHoles.Skip(1), (a, b) => (Along(b) - Along(a)) / pitch)
                    .Any(s => Math.Abs(s / Math.Max(1, Math.Round(s)) - 1) > .15))
                { rejectedLight++; continue; }
                if (needsBase)
                {
                    foreach (var (a, b) in row.Zip(row.Skip(1)))
                    {
                        double gap = Along(b) - Along(a) - (AlongSize(a) + AlongSize(b)) / 2;
                        double center = (Along(a) + AlongSize(a) / 2 + Along(b) - AlongSize(b) / 2) / 2;
                        double cross = (Cross(a) + Cross(b)) / 2;
                        var sample = horizontal
                            ? Measure(center, cross, gap * .5, Math.Min(a.Height, b.Height) * .5, CalibrationTarget.FilmBase)
                            : Measure(cross, center, Math.Min(a.Width, b.Width) * .5, gap * .5, CalibrationTarget.FilmBase);
                        if (sample is not null) basis.Add(sample);
                        else diagnostic?.Invoke(FormattableString.Invariant(
                            $"Excluded film-base gap at ({center:F3}, {cross:F3}) from physical row."));
                    }
                    if (basis.Count < 2) { rejectedBase++; continue; }
                }
                var l = RowValue(light, evidence);
                var b0 = needsBase ? RowValue(basis, evidence) : null;
                if (!Consistent(light, l) || (b0 is not null && (!Consistent(basis, b0)
                    || Channels(b0).Zip(Channels(l), (b, l0) => b / l0).Any(r => r >= .98))))
                { rejectedConsistency++; continue; }
                candidates.Add(new() { Light = l, FilmBase = b0 });
                diagnostic?.Invoke("Accepted physical row: " + System.Text.Json.JsonSerializer.Serialize(candidates[^1]));
            }
            totalRows += physicalRows.Count;
            totalRejectedLight += rejectedLight;
            totalRejectedBase += rejectedBase;
            diagnostic?.Invoke(FormattableString.Invariant(
                $"Threshold={threshold:F6}; candidate-holes={holes.Count}; row-hypotheses={rows}; physical-rows={physicalRows.Count}; rejected-backlight={rejectedLight}; rejected-film-base={rejectedBase}; rejected-consistency={rejectedConsistency}; accepted={candidates.Count}"));
            if (candidates.Count > 0) break;
        }
        if (candidates.Count == 0)
        {
            if (totalRows > 0)
            {
                string reason = totalRejectedLight == totalRows
                    ? BacklightFailure(highBacklight, darkBacklight)
                    : totalRejectedBase > 0
                        ? "Clear film patches between the holes are dark, clipped or nonuniform. Avoid film markings and image content."
                        : "Light or film-base measurements are inconsistent, or the film base is not distinguishable from bare light.";
                throw Failure($"Detected {maxHoles} candidate sprocket holes, but no row passed calibration. {reason}",
                    needsGeometry: false);
            }
            throw Failure("No reliable, evenly spaced sprocket row with unclipped, uniform samples was found.");
        }
        var chosen = candidates[0];
        if (!Consistent(candidates.Select(c => c.Light!).ToList(), chosen.Light!)
            || (needsBase && !Consistent(candidates.Select(c => c.FilmBase!).ToList(), chosen.FilmBase!)))
            throw Failure("Sprocket rows give conflicting light or film-base measurements.", needsGeometry: false);
        if (measureColor)
        {
            chosen.Light!.Validate(allowClipped: true);
            chosen.FilmBase?.Validate();
        }
        diagnostic?.Invoke("Selected samples: " + System.Text.Json.JsonSerializer.Serialize(chosen));
        return new(chosen)
        {
            LightEvidence = evidence[chosen.Light!],
            FilmBaseEvidence = chosen.FilmBase is { } chosenBase ? evidence[chosenBase] : null
        };

        PictureSample? Measure(double cx, double cy, double w, double h, CalibrationTarget target)
        {
            if (w < 2 || h < 2) return null;
            var samples = new List<double>[] { [], [], [] };
            foreach (var (x, y) in PatchRule.Points(cx, cy, w / 2, h / 2))
            {
                token.ThrowIfCancellationRequested();
                int px = (int)(x * image.Width / width);
                int py = (int)(y * image.Height / height);
                int i = (py * image.Width + px) * 3;
                double luminance = .2627 * image.Pixels[i] + .6780 * image.Pixels[i + 1] + .0593 * image.Pixels[i + 2];
                for (int c = 0; c < 3; c++) samples[c].Add(measureColor ? image.Pixels[i + c] : luminance);
            }
            var result = PatchRule.Evaluate(samples, measureColor, allowClipped: target == CalibrationTarget.Backlight);
            if (result.Rejection is { } reason)
            {
                if (measureColor && target == CalibrationTarget.Backlight)
                {
                    highBacklight |= result.High.Any(v => v > 0);
                    darkBacklight |= result.Low.Any(v => v > 0);
                }
                return Reject(reason);
            }
            double[] values = result.Values;
            var sample = new PictureSample(frameId, cx / width, cy / height, values[0], values[1], values[2])
            {
                SourceWidth = image.Width,
                SourceHeight = image.Height,
                IsClipped = measureColor && target == CalibrationTarget.Backlight && result.High.Any(v => v > 0)
            };
            evidence[sample] = new(w / (2 * width), h / (2 * height),
                Math.Max(image.Width, image.Height), result.Peak);
            return sample;

            PictureSample? Reject(string reason)
            {
                if (diagnostic is not null && loggedPatches.Count < 64 && loggedPatches.Add((target, cx, cy)))
                {
                    string Rgb(Func<List<double>, double> value) =>
                        string.Join(", ", samples.Select(s => value(s).ToString("F6", CultureInfo.InvariantCulture)));
                    diagnostic(FormattableString.Invariant(
                        $"Rejected {target} patch ({cx / width:F6}, {cy / height:F6}): {reason}; RGB min=({Rgb(s => s[0])}); RGB median=({Rgb(s => s[s.Count / 2])}); RGB max=({Rgb(s => s[^1])})"));
                }

                return null;
            }
        }
    }

    private static double[] Channels(PictureSample s) => [s.Red, s.Green, s.Blue];

    internal static InvalidDataException InvalidPatch(CalibrationTarget target, bool high, bool low) =>
        Failure(target == CalibrationTarget.Backlight ? BacklightFailure(high, low)
            : "Clear film patches between the holes are dark, clipped or nonuniform. Avoid film markings and image content.",
            needsGeometry: false);

    private static string BacklightFailure(bool high, bool low) => high && low
        ? "Backlight patches include both dark and clipped or out-of-range samples. Check illumination, obstructions and camera exposure, then recapture."
        : high
            ? "Backlight samples are clipped or above the supported linear RGB range. Reduce camera exposure and recapture before measuring white balance."
            : low
                ? "Backlight samples are too dark. Check the backlight and camera exposure, then recapture."
                : "Backlight patches are not sufficiently uniform. Avoid shadows, gradients and obstructed perforations.";

    private static PictureSample RowValue(List<PictureSample> samples, Dictionary<PictureSample, SampleEvidence> evidence)
    {
        double[] median = Enumerable.Range(0, 3).Select(c => Median(samples.Select(s => Channels(s)[c]).ToArray())).ToArray();
        var scores = samples.Select(s => (Sample: s, Score: Channels(s).Zip(median, (v, m) => Math.Abs(v / m - 1)).Sum())).ToArray();
        double best = scores.Min(s => s.Score);
        // Scores are scale-free; treat roundoff ties alike before selecting by capture geometry.
        var representative = scores.Where(s => s.Score - best <= Math.Max(1e-12, Math.Max(s.Score, best) * 1e-6))
            .OrderBy(s => s.Sample.X).ThenBy(s => s.Sample.Y).First().Sample;
        var result = representative with
        {
            Red = median[0], Green = median[1], Blue = median[2], IsClipped = samples.Any(s => s.IsClipped)
        };
        evidence[result] = evidence[representative];
        return result;
    }

    private static bool Consistent(List<PictureSample> samples, PictureSample reference) =>
        samples.All(s => Channels(s).Zip(Channels(reference), (v, r) => Math.Abs(v / r - 1)).All(d => d <= .12));

    private static double Median(double[] values)
    {
        Array.Sort(values);
        return (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
    }

    private static InvalidDataException Failure(string reason, bool needsGeometry = true) => new(
        reason + (needsGeometry
            ? " Auto-pick needs at least three evenly spaced, roughly horizontal or vertical sprocket holes near a film edge. Include the perforations in the RAW."
            : "") + " Use the manual pickers only on unclipped, uniform patches. Existing picks have not changed.");
}
