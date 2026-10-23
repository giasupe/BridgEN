using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BridgEN.App.Configuration;

/// <summary>Persists operation preferences separately from connection credentials.</summary>
internal sealed class OcrOperationSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BridgEN", "ocr-settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public OcrOperationSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new OcrOperationSettings();
        var settings = JsonSerializer.Deserialize<OcrOperationSettings>(File.ReadAllText(SettingsPath), JsonOptions)
            ?? throw new InvalidDataException("操作設定を読み取れませんでした。");
        Validate(settings);
        return settings;
    }

    public void Save(OcrOperationSettings settings)
    {
        Validate(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    private static void Validate(OcrOperationSettings settings)
    {
        if (!Enum.IsDefined(settings.Mode) || !settings.EffectiveHotkey.IsValid)
            throw new InvalidDataException("操作モードまたは実行キーが正しくありません。");
    }
}
