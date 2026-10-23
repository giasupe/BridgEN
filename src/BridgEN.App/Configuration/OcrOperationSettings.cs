using BridgEN.App.Input;
using System.Text.Json.Serialization;

namespace BridgEN.App.Configuration;

public enum OcrOperationMode
{
    Automatic,
    Manual
}

internal sealed record OcrOperationSettings(
    OcrOperationMode Mode = OcrOperationMode.Automatic,
    ManualOcrHotkey? ManualHotkey = null)
{
    [JsonIgnore]
    public ManualOcrHotkey EffectiveHotkey => ManualHotkey ?? ManualOcrHotkey.Default;
}
