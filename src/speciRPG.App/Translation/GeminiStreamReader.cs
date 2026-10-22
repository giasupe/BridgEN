using System.IO;
using System.Text;
using System.Text.Json;

namespace speciRPG.App.Translation;

/// <summary>Reads Gemini SSE events and preserves whitespace between text chunks.</summary>
internal static class GeminiStreamReader
{
    public static async Task<string> ReadAsync(Stream stream, Func<string, Task> onTextReceived,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var eventData = new StringBuilder();
        var translation = new StringBuilder();
        var completed = false;
        while (!completed)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null || line.Length == 0)
            {
                if (eventData.Length > 0)
                {
                    completed = await ReadEventAsync(eventData.ToString(), translation,
                        onTextReceived, cancellationToken).ConfigureAwait(false);
                    eventData.Clear();
                }
                if (line is null) break;
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (eventData.Length > 0) eventData.Append('\n');
                var value = line.AsMemory(5);
                if (value.Span.StartsWith(" ")) value = value[1..];
                eventData.Append(value.Span);
            }
            // SSE comments, event names and ids do not contain translation text.
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!completed)
            throw new IOException("翻訳の受信が途中で終了しました。");
        var result = translation.ToString().Trim();
        if (result.Length == 0)
            throw new InvalidOperationException("Geminiから翻訳文を受け取れませんでした。");
        return result;
    }

    private static async Task<bool> ReadEventAsync(string data, StringBuilder translation,
        Func<string, Task> onTextReceived, CancellationToken cancellationToken)
    {
        if (data == "[DONE]") return false; // A marker alone is not a successful finish reason.
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out _))
            throw new InvalidOperationException("Geminiからストリーミング受信中にAPIエラーが返されました。");
        if (root.TryGetProperty("promptFeedback", out var feedback)
            && feedback.TryGetProperty("blockReason", out var blocked)
            && blocked.GetString() is { } blockReason && blockReason != "BLOCK_REASON_UNSPECIFIED")
            throw new InvalidOperationException("Geminiがこの文章の翻訳を返しませんでした。");

        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            return false;
        var candidate = candidates[0];
        var finishReason = candidate.TryGetProperty("finishReason", out var finish)
            ? finish.GetString() : null;
        if (finishReason is not (null or "STOP" or "FINISH_REASON_UNSPECIFIED"))
            throw new InvalidOperationException($"Geminiの翻訳が完了しませんでした（{finishReason}）。");

        var previousLength = translation.Length;
        if (candidate.TryGetProperty("content", out var content)
            && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
                    continue;
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    translation.Append(text.GetString());
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (translation.Length > previousLength && !string.IsNullOrWhiteSpace(translation.ToString()))
            await onTextReceived(translation.ToString()).ConfigureAwait(false);
        return finishReason == "STOP";
    }
}
