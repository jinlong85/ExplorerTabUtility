using System;
using System.Linq;
using System.Collections.Generic;

namespace ExplorerTabUtility.Models;

/// <summary>
/// The File Explorer windows and tabs of the last session (stored in %AppData%\ExplorerTabUtility\session.json).
/// </summary>
public sealed class SessionData
{
    public int Version { get; set; } = 1;

    /// <summary>When <see cref="Windows"/> was last observed (UTC).</summary>
    public DateTime SavedAtUtc { get; set; }

    /// <summary>
    /// True while the windows were still open at the last observation (so a reboot / logoff / Explorer crash ended the session).
    /// False when the user closed all windows: such a session is only restored on request (tray menu / shortcut).
    /// </summary>
    public bool IsOpen { get; set; }

    /// <summary>Windows, most recently used first.</summary>
    public List<SessionWindow> Windows { get; set; } = [];

    public int TabCount => Windows.Sum(w => w.Tabs.Count);
}

public sealed class SessionWindow
{
    /// <summary>Tabs in the order File Explorer registered them.</summary>
    public List<SessionTab> Tabs { get; set; } = [];
}

public sealed class SessionTab
{
    public string Location { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string[]? SelectedItems { get; set; }
}

/// <summary>Outcome of restoring a session (see ExplorerWatcher.RestoreSessionAsync).</summary>
public sealed class RestoreResult
{
    public bool IsAutomatic { get; set; }
    /// <summary>Tabs that were opened.</summary>
    public int OpenedTabs { get; set; }
    /// <summary>Tabs that were already open (or duplicates, with Reuse Tabs) and therefore not opened again.</summary>
    public int AlreadyOpen { get; set; }
    /// <summary>Folders that don't exist anymore (skipped).</summary>
    public List<string> Missing { get; } = [];
    /// <summary>Tabs that were not opened because of the tab limit.</summary>
    public int OverLimit { get; set; }
    /// <summary>Tabs that could not be opened (timeout / navigation failed).</summary>
    public int Failed { get; set; }
    /// <summary>There was no saved session at all.</summary>
    public bool NoSession { get; set; }
}
