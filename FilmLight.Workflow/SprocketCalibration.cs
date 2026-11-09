namespace FilmLight.Workflow;

internal sealed record SprocketLocations(double LightX, double LightY, double FilmBaseX, double FilmBaseY);

internal static class SprocketCalibration
{
    private sealed record Hole(int Left, int Top, int Width, int Height)
    {
        internal double X => Left + Width / 2.0;
        internal double Y => Top + Height / 2.0;
    }

    internal static PictureCalibration Detect(LinearImage image, Guid frameId, bool needsBase,
        CancellationToken token) => Analyze(image, frameId, needsBase, token, measureColor: true);

    internal static SprocketLocations Locate(LinearImage image, CancellationToken token)
    {
        var result = Analyze(image, Guid.Empty, true, token, measureColor: false);
        return new(result.Light!.X, result.Light.Y, result.FilmBase!.X, result.FilmBase.Y);
    }

    private static PictureCalibration Analyze(LinearImage image, Guid frameId, bool needsBase,
        CancellationToken token, bool measureColor)
    {
        token.ThrowIfCancellationRequested();
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

            if (holes.Count > 200)
                throw Failure("Too many competing bright regions.");
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
                    var light = new List<PictureSample>();
                    var basis = new List<PictureSample>();
                    foreach (var hole in row)
                    {
                        var sample = Measure(hole.X, hole.Y, hole.Width * .5, hole.Height * .5);
                        if (sample is not null) light.Add(sample);
                    }
                    if (light.Count != row.Length) continue;
                    if (needsBase)
                    {
                        foreach (var (a, b) in row.Zip(row.Skip(1)))
                        {
                            double gap = Along(b) - Along(a) - (AlongSize(a) + AlongSize(b)) / 2;
                            double center = (Along(a) + AlongSize(a) / 2 + Along(b) - AlongSize(b) / 2) / 2;
                            double cross = (Cross(a) + Cross(b)) / 2;
                            var sample = horizontal
                                ? Measure(center, cross, gap * .5, Math.Min(a.Height, b.Height) * .5)
                                : Measure(cross, center, Math.Min(a.Width, b.Width) * .5, gap * .5);
                            if (sample is not null) basis.Add(sample);
                        }
                        if (basis.Count != row.Length - 1) continue;
                    }
                    var l = Representative(light);
                    var b0 = needsBase ? Representative(basis) : null;
                    if (!Consistent(light, l) || (b0 is not null && (!Consistent(basis, b0)
                        || Channels(b0).Zip(Channels(l), (b, l0) => b / l0).Any(r => r >= .98)))) continue;
                    candidates.Add(new() { Light = l, FilmBase = b0 });

                    double Along(Hole h) => horizontal ? h.X : h.Y;
                    double Cross(Hole h) => horizontal ? h.Y : h.X;
                    double AlongSize(Hole h) => horizontal ? h.Width : h.Height;
                    double CrossSize(Hole h) => horizontal ? h.Height : h.Width;
                }
            if (candidates.Count > 0) break;
        }
        if (candidates.Count == 0)
            throw Failure("No reliable, evenly spaced sprocket row with unclipped, uniform samples was found.");
        var chosen = candidates[0];
        if (!Consistent(candidates.Select(c => c.Light!).ToList(), chosen.Light!)
            || (needsBase && !Consistent(candidates.Select(c => c.FilmBase!).ToList(), chosen.FilmBase!)))
            throw Failure("Sprocket rows give conflicting light or film-base measurements.");
        if (measureColor)
        {
            chosen.Light!.Validate();
            chosen.FilmBase?.Validate();
        }
        return chosen;

        PictureSample? Measure(double cx, double cy, double w, double h)
        {
            if (w < 2 || h < 2) return null;
            var samples = new List<double>[] { [], [], [] };
            for (int y = 0; y < 15; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < 15; x++)
                {
                    int px = (int)((cx - w / 2 + w * (x + .5) / 15) * image.Width / width);
                    int py = (int)((cy - h / 2 + h * (y + .5) / 15) * image.Height / height);
                    int i = (py * image.Width + px) * 3;
                    double luminance = .2627 * image.Pixels[i] + .6780 * image.Pixels[i + 1] + .0593 * image.Pixels[i + 2];
                    for (int c = 0; c < 3; c++) samples[c].Add(measureColor ? image.Pixels[i + c] : luminance);
                }
            }
            double[] values = new double[3];
            for (int c = 0; c < 3; c++)
            {
                samples[c].Sort();
                // Preview localization tolerates clipping; RAW color calibration must not.
                if (measureColor && samples[c].Count(v => v <= .0001 || v >= .99999) > samples[c].Count * .05) return null;
                values[c] = samples[c][samples[c].Count / 2];
                if (values[c] <= .0001 || (measureColor && values[c] >= .99999)
                    || samples[c][samples[c].Count * 9 / 10] - samples[c][samples[c].Count / 10] > values[c] * .15)
                    return null;
            }
            return new(frameId, cx / width, cy / height, values[0], values[1], values[2]);
        }
    }

    private static double[] Channels(PictureSample s) => [s.Red, s.Green, s.Blue];

    private static PictureSample Representative(List<PictureSample> samples)
    {
        double[] median = Enumerable.Range(0, 3).Select(c => Median(samples.Select(s => Channels(s)[c]).ToArray())).ToArray();
        return samples.MinBy(s => Channels(s).Zip(median, (v, m) => Math.Abs(v / m - 1)).Sum())!;
    }

    private static bool Consistent(List<PictureSample> samples, PictureSample reference) =>
        samples.All(s => Channels(s).Zip(Channels(reference), (v, r) => Math.Abs(v / r - 1)).All(d => d <= .12));

    private static double Median(double[] values)
    {
        Array.Sort(values);
        return (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
    }

    private static InvalidDataException Failure(string reason) => new(
        reason + " Auto-pick needs at least three evenly spaced, roughly horizontal or vertical sprocket holes near a film edge. " +
        "Include the perforations in the RAW, avoid clipping the backlight, or use the manual pickers. Existing picks have not changed.");
}
