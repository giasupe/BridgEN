namespace BridgEN.App.Translation;

/// <summary>
/// AI translation provider boundary.
/// Each provider returns only the translated text.
/// </summary>
public interface ITranslationProvider
{
    Task<string> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>The callback receives the full translation accumulated so far.</summary>
    async Task<string> TranslateStreamingAsync(
        TranslationRequest request,
        Func<string, Task> onTextReceived,
        CancellationToken cancellationToken = default)
    {
        var translation = await TranslateAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await onTextReceived(translation);
        return translation;
    }
}

/// <summary>
/// Text and context sent to a translation provider.
/// Image data is intentionally not part of this request.
/// </summary>
public sealed record TranslationRequest(
    string Text,
    string SourceLanguage,
    string TargetLanguage,
    string? SpeakerName,
    string? CharacterMemory,
    IReadOnlyList<string> RecentConversation);
