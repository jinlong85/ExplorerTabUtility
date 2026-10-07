using Shell32;
using SHDocVw;
using System.Diagnostics;

namespace ExplorerTabUtility.Models;

public class WindowInfo
{
    public long CreatedAt { get; } = Stopwatch.GetTimestamp();
    public string? Location { get; set; }
    public string? Name { get; set; }
    public DWebBrowserEvents2_OnQuitEventHandler? OnQuitHandler { get; set; }
    public DWebBrowserEvents2_NavigateComplete2EventHandler? OnNavigateHandler { get; set; }

    // Used to notice when Explorer shows an item in this tab while the tab is not the active one
    // (see ExplorerWatcher.OnViewSelectionChanged).
    public long LastNavigatedAt { get; set; } = Stopwatch.GetTimestamp();
    public string[]? LastSelection { get; set; }
    public ShellFolderView? View { get; set; }
    public DShellFolderViewEvents_SelectionChangedEventHandler? SelectionChangedHandler { get; set; }
    public DWebBrowserEvents2_NavigateComplete2EventHandler? ViewNavigateHandler { get; set; }
    public int RevealCheckPending;
}