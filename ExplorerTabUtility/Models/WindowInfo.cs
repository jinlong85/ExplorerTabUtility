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

    // Closing a tab that duplicates another tab of the same window (see ExplorerWatcher.ScheduleDuplicateTabCheck).
    /// <summary>The location of the tab after its last navigation.</summary>
    public string? LastViewLocation { get; set; }
    /// <summary>Created by the app's "Duplicate tab" action: a duplicate on purpose, never closed as a duplicate.</summary>
    public bool SkipDuplicateCheck { get; set; }
    public int DuplicateCheckVersion;
}