namespace BridgEN.App.Translation;

/// <summary>UI-thread cache of completed translations, scoped to the selected window and region.</summary>
internal sealed class TranslationResultCache
{
    private const int Capacity = 128;
    private const int MaximumEntryCharacters = 16_384;
    private readonly Dictionary<string, LinkedListNode<(string Source, string Translation)>> _entries =
        new(StringComparer.Ordinal);
    private readonly LinkedList<(string Source, string Translation)> _recent = new();

    public bool TryGet(string source, out string translation)
    {
        if (_entries.TryGetValue(source, out var entry))
        {
            _recent.Remove(entry);
            _recent.AddFirst(entry);
            translation = entry.Value.Translation;
            return true;
        }
        translation = string.Empty;
        return false;
    }

    public void Store(string source, string translation)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translation)
            || source.Length + (long)translation.Length > MaximumEntryCharacters) return;
        if (_entries.Remove(source, out var existing)) _recent.Remove(existing);
        var entry = _recent.AddFirst((source, translation));
        _entries[source] = entry;
        if (_entries.Count > Capacity && _recent.Last is { } oldest)
        {
            _entries.Remove(oldest.Value.Source);
            _recent.RemoveLast();
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _recent.Clear();
    }
}
