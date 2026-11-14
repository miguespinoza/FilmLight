namespace FilmLight.Processing;

public readonly record struct PixelSize(int Width, int Height);
public readonly record struct PixelRect(int X, int Y, int Width, int Height);
public sealed record PhotoEditTransform(PixelRect CropBounds, double RotationDegrees,
    bool FlipHorizontal, bool FlipVertical);
