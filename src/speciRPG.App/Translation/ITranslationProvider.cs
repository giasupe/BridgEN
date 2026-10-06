namespace speciRPG.App.Translation;

/// <summary>
/// AI translation provider boundary.
/// Each provider returns only the translated text.
/// </summary>
public interface ITranslationProvider
{
    Task<string> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);
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
