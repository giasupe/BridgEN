namespace BridgEN.App.Ocr;

/// <summary>
/// Separates text changes from animated scenery. A visual difference alone is
/// advisory; near-identical OCR readings corroborate a small dialogue edit.
/// </summary>
internal sealed class OcrTextStabilizer
{
    private OcrFrameSignature? _scene;
    private string _text = string.Empty;
    private string _key = string.Empty;
    private string _pendingKey = string.Empty;
    private int _pendingReads;
    private TimeSpan _pendingStartedAt;
    private long _revision;
    private TimeSpan _revisionStartedAt;
    private int _confirmations;
    private int _emptyFrames;

    public bool HasVisualChange(OcrFrameSignature frame) =>
        _scene is null || !_scene.IsSameContent(frame);

    public bool HasContentChanged(OcrFrameSignature frame, string candidate)
    {
        if (_scene is null || !_scene.HasSameSize(frame)) return true;
        var key = OcrTextSimilarity.Key(candidate);
        if (key.Length == 0) return true;
        if (key == _key) return false;
        if (IsGrowth(key) || !OcrTextSimilarity.IsSamePassage(key, _key)) return true;
        return HasVisualChange(frame) && _pendingKey == key && _pendingReads >= 1;
    }

    public OcrTextObservation Observe(OcrFrameSignature frame, string candidate, TimeSpan now)
    {
        candidate = OcrTextQuality.Normalize(candidate);
        var key = OcrTextSimilarity.Key(candidate);
        var visualChanged = HasVisualChange(frame);
        var resized = _scene is not null && !_scene.HasSameSize(frame);

        if (key.Length == 0)
        {
            _emptyFrames++;
            StartRevision(now);
            _pendingReads = 0;
            if (_emptyFrames >= 2)
            {
                _text = _key = string.Empty;
                _scene = frame;
            }
            return Result(now, false, "empty", visualChanged);
        }

        var recoveredFromEmpty = _emptyFrames > 0;
        _emptyFrames = 0;
        if (_scene is null || resized || _key.Length == 0 || recoveredFromEmpty)
        {
            Accept(frame, candidate, key, now);
            return Result(now, false, resized ? "resized" : "first-reading", visualChanged);
        }

        if (key == _key)
        {
            _pendingReads = 0;
            if (candidate != _text && OcrTextQuality.IsBetterCandidate(candidate, _text))
            {
                Accept(frame, candidate, key, now);
                return Result(now, false, "better-spacing", visualChanged);
            }
            _confirmations++;
            return Result(now, _confirmations >= 2, "same-text", visualChanged);
        }

        if (IsGrowth(key) || !OcrTextSimilarity.IsSamePassage(key, _key))
        {
            Accept(frame, candidate, key, now);
            return Result(now, false, "new-passage", visualChanged);
        }

        if (_pendingReads > 0 && _pendingKey == key)
        {
            _pendingReads++;
        }
        else
        {
            _pendingKey = key;
            _pendingReads = 1;
            _pendingStartedAt = now;
        }

        var better = OcrTextQuality.IsBetterCandidate(candidate, _text);
        var substantialImprovement = better && OcrTextQuality.Score(candidate) - OcrTextQuality.Score(_text)
            >= Math.Max(4, Math.Abs(OcrTextQuality.Score(_text)) * 0.12);
        if (substantialImprovement || (better && _pendingReads >= 2))
        {
            Accept(frame, candidate, key, now);
            return Result(now, false, "better-reading", visualChanged);
        }

        // A real small edit may have a lower score. Confirm the same new reading
        // twice with changed pixels; A/B OCR alternation cannot satisfy this.
        if (visualChanged && _pendingReads >= 2)
        {
            var since = _pendingStartedAt;
            Accept(frame, candidate, key, since);
            _confirmations = 2;
            return Result(now, true, "confirmed-small-edit", visualChanged);
        }

        _confirmations++;
        return Result(now, _confirmations >= 2, "held-ocr-variant", visualChanged);
    }

    private bool IsGrowth(string key) =>
        _key.Length > 0 && key.Length > _key.Length && key.StartsWith(_key, StringComparison.Ordinal);

    private void Accept(OcrFrameSignature frame, string text, string key, TimeSpan now)
    {
        _scene = frame;
        _text = text;
        _key = key;
        _pendingKey = string.Empty;
        _pendingReads = 0;
        StartRevision(now);
    }

    private OcrTextObservation Result(TimeSpan now, bool stable, string reason, bool visualChanged) =>
        new(_text, _revision, stable,
            now > _revisionStartedAt ? now - _revisionStartedAt : TimeSpan.Zero, reason, visualChanged);

    private void StartRevision(TimeSpan now)
    {
        _revision++;
        _revisionStartedAt = now;
        _confirmations = 1;
    }
}

internal sealed record OcrTextObservation(string Text, long Revision, bool IsStable,
    TimeSpan UnchangedFor, string Reason, bool VisualChanged);
