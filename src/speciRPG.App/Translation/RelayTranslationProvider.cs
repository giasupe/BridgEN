using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace speciRPG.App.Translation;

/// <summary>Calls the BridgEN relay with a per-user invitation token, never a Gemini key.</summary>
public sealed class RelayTranslationProvider : ITranslationProvider
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(15)
    }) { Timeout = TimeSpan.FromSeconds(55) };
    private readonly Uri _baseUri;
    private readonly string _token;

    public RelayTranslationProvider(string relayUrl, string inviteToken)
    {
        _baseUri = ValidateBaseUri(relayUrl);
        _token = inviteToken.Trim();
        if (!Regex.IsMatch(_token, "^[A-Za-z0-9_-]{40,128}$"))
            throw new ArgumentException("招待トークンの形式が正しくありません。設定欄で登録し直してください。");
    }

    public static Uri ValidateBaseUri(string relayUrl)
    {
        if (!Uri.TryCreate(relayUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("サーバーURLには、https:// で始まるWorkerのURLを指定してください。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    public async Task WarmConnectionAsync()
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await Client.GetAsync(new Uri(_baseUri, "health"),
                HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { }
    }

    public Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default) =>
        TranslateStreamingAsync(request, _ => Task.CompletedTask, cancellationToken);

    public async Task<string> TranslateStreamingAsync(TranslationRequest request,
        Func<string, Task> onTextReceived, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onTextReceived);
        if (string.IsNullOrWhiteSpace(request.Text)) return string.Empty;
        if (request.Text.Trim().Length > 2000)
            throw new InvalidOperationException("中継サーバーへ送れる英文は1回2,000文字までです。読む範囲を小さくしてください。");
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "translate"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new StringContent(JsonSerializer.Serialize(new { text = request.Text }), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        message.Headers.Accept.ParseAdd("text/event-stream");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(55));
        try
        {
            using var response = await Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await CreateExceptionAsync(response, deadline.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                throw new HttpRequestException("翻訳サーバーの応答形式が異なります。URLを確認してください。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            return await GeminiStreamReader.ReadAsync(stream, onTextReceived, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("翻訳サーバーからの応答が時間内に完了しませんでした。", exception);
        }
    }

    private static async Task<HttpRequestException> CreateExceptionAsync(HttpResponseMessage response, CancellationToken token)
    {
        string? code = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[4096];
            var length = await reader.ReadBlockAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(new string(buffer, 0, length));
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("code", out var value)) code = value.GetString();
        }
        catch (Exception exception) when (exception is JsonException or IOException) { }
        var description = code switch
        {
            "INVALID_TOKEN" => "招待トークンが無効または失効しています。",
            "DAILY_LIMIT" => "本日の翻訳利用上限に達しました。上限は日本時間の午前9時にリセットされます。",
            "RATE_LIMIT" => "短時間の翻訳回数が上限に達しました。少し待ってからOCRを再開してください。",
            "TEXT_TOO_LONG" or "INVALID_BODY" when response.StatusCode == HttpStatusCode.RequestEntityTooLarge
                => "英文が長すぎます。読み取り範囲を小さくしてください。",
            "SERVICE_NOT_CONFIGURED" => "翻訳サーバーのSecretまたは設定が未登録です。管理者に連絡してください。",
            "SERVICE_PAUSED" => "翻訳サービスを一時停止しています。",
            "BUDGET_UNAVAILABLE" => "翻訳の利用上限を確認できません。時間を置いて再開してください。",
            "UPSTREAM_TIMEOUT" => "Geminiからの応答が時間内に完了しませんでした。",
            "UPSTREAM_ERROR" => "翻訳APIでエラーが発生しました。管理者に連絡してください。",
            _ when response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => "認証できませんでした。招待トークンとサーバーURLを確認してください。",
            _ => $"翻訳サーバーでエラーが発生しました（HTTP {(int)response.StatusCode}）。"
        };
        return new HttpRequestException(description, null, response.StatusCode);
    }
}
