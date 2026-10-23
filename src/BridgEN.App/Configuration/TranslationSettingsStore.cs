using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BridgEN.App.Configuration;

/// <summary>Stores public settings and a user-bound DPAPI-encrypted invitation token.</summary>
internal sealed class TranslationSettingsStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BridgEN");
    private static readonly string SettingsPath = Path.Combine(DirectoryPath, "connection.json");

    public TranslationConnectionSettings? Load()
    {
        if (!File.Exists(SettingsPath)) return null;
        var saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(SettingsPath))
            ?? throw new InvalidDataException("接続設定を読み取れませんでした。");
        var token = string.IsNullOrEmpty(saved.EncryptedInviteToken) ? string.Empty
            : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(saved.EncryptedInviteToken),
                null, DataProtectionScope.CurrentUser));
        return new TranslationConnectionSettings(saved.UseRelay, saved.RelayUrl ?? string.Empty, token);
    }

    public void Save(TranslationConnectionSettings settings)
    {
        var encrypted = string.IsNullOrEmpty(settings.InviteToken) ? string.Empty
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.InviteToken),
                null, DataProtectionScope.CurrentUser));
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
            new SavedSettings(settings.UseRelay, settings.RelayUrl, encrypted),
            new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    public static string DefaultRelayUrl()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "bridgen.defaults.json");
        if (!File.Exists(path)) return string.Empty;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("relayUrl", out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    private sealed record SavedSettings(bool UseRelay, string RelayUrl, string EncryptedInviteToken);
}
