namespace BridgEN.App.Ocr;

/// <summary>Wall-clock durations for one OCR pass, measured with Stopwatch.</summary>
public sealed record OcrProcessingTimings(
    long RevisionStartedTimestamp,
    TimeSpan Capture,
    TimeSpan? Crop,
    TimeSpan Preprocessing,
    TimeSpan Recognition,
    TimeSpan Layout,
    TimeSpan? Recovery,
    TimeSpan StabilityCheck,
    TimeSpan Total,
    TimeSpan InitialPassTotal);
