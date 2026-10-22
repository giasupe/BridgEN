namespace speciRPG.App.Configuration;

internal sealed record TranslationConnectionSettings(
    bool UseRelay = true,
    string RelayUrl = "",
    string InviteToken = "");
