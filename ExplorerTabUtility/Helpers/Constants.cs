namespace ExplorerTabUtility.Helpers;

internal static class Constants
{
    internal const string AppName = "ExplorerTabUtility";
    internal const string MutexId = $"__{AppName}Hook__Mutex";
    internal const string SettingsFileName = "settings.json";
    internal const string HotKeyProfilesFileName = "HotKeyProfiles.json";
    internal const string SessionFileName = "session.json";
    // This (Simplified Chinese) build is maintained in the jinlong85 fork, so updates and project links point there.
    // The original project and its author (w4po) are credited in the About page.
    internal const string ProjectUrl = "https://github.com/jinlong85/ExplorerTabUtility";
    internal const string UpstreamProjectUrl = "https://github.com/w4po/ExplorerTabUtility";
    internal const string UpdateUrl = "https://api.github.com/repos/jinlong85/ExplorerTabUtility/releases/latest";
    internal const string DefaultHotKeyProfiles = "[{\"Name\":\"Home\",\"HotKeys\":[91,69],\"Scope\":0,\"Action\":0,\"Path\":\"\",\"IsHandled\":true,\"IsEnabled\":true,\"Delay\":0},{\"Name\":\"Duplicate\",\"HotKeys\":[17,68],\"Scope\":1,\"Action\":1,\"Path\":null,\"IsHandled\":true,\"IsEnabled\":true,\"Delay\":0},{\"Name\":\"ReopenClosed\",\"HotKeys\":[16,17,84],\"Scope\":1,\"Action\":2,\"Path\":null,\"IsHandled\":true,\"IsEnabled\":true,\"Delay\":0}]";
}