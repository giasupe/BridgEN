namespace speciRPG.App.Ocr;

/// <summary>A rectangle expressed as fractions of the captured window size.</summary>
public sealed record OcrCaptureRegion(double Left, double Top, double Width, double Height)
{
    public bool IsValid =>
        double.IsFinite(Left)
        && double.IsFinite(Top)
        && double.IsFinite(Width)
        && double.IsFinite(Height)
        && Left >= 0
        && Top >= 0
        && Width > 0
        && Height > 0
        && Left + Width <= 1
        && Top + Height <= 1;
}

public sealed class OcrRegionSelectedEventArgs(OcrCaptureRegion region) : EventArgs
{
    public OcrCaptureRegion Region { get; } = region;
}
