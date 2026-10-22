namespace speciRPG.App.Ocr;

/// <summary>Fast confirmation requires repeated readable text, not just similar OCR variants.</summary>
internal sealed class OcrTranslationReadiness
{
    private static readonly TimeSpan FastDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CautiousDelay = TimeSpan.FromSeconds(1);
    private long _revision = -1;
    private int _matchingReads;
    private bool _hadVariant;
    private bool _ready;
    private TimeSpan _firstFrameAt;

    public bool Observe(OcrTextObservation observation, string reading, TimeSpan frameCapturedAt)
    {
        if (_revision != observation.Revision)
        {
            _revision = observation.Revision;
            _matchingReads = 0;
            _hadVariant = false;
            _ready = false;
            _firstFrameAt = frameCapturedAt;
        }

        // Preserve word boundaries as well as letters: merged or dropped words
        // must not qualify for the fast path merely because a similarity key matches.
        var matches = string.Equals(OcrTextQuality.Normalize(reading), observation.Text,
            StringComparison.Ordinal);
        _matchingReads = matches ? _matchingReads + 1 : 0;
        _hadVariant |= !matches;
        var readable = !string.IsNullOrWhiteSpace(observation.Text)
            && !observation.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(OcrTextQuality.ShouldRetry);
        if (!observation.IsStable) return false;
        var fastConfirmed = readable && !_hadVariant && _matchingReads >= 2
            && frameCapturedAt - _firstFrameAt >= FastDelay;
        // Once this accepted passage is confirmed, a held lower-quality variant
        // cannot restart its API request. A new revision resets the gate.
        // OCR work on the first image already consumes time. Measure the gap
        // between independent captures instead of waiting again after that work.
        _ready |= fastConfirmed || observation.UnchangedFor >= CautiousDelay;
        return _ready;
    }
}
