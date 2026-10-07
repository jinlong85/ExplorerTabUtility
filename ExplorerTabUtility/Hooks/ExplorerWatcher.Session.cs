using SHDocVw;
using Shell32;
using System;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.WinAPI;

namespace ExplorerTabUtility.Hooks;

// ---- Restore the last session ----
// The open Explorer windows and tabs are observed every few seconds and saved to %AppData%\ExplorerTabUtility\session.json
// (atomically, throttled). They are restored automatically only when the windows were lost WITHOUT the user closing them:
//  1. App start after a reboot / logoff (or an Explorer restart while the app was not running): the session was still open
//     when it was last saved, Explorer was started after that, the session is not older than 14 days, and no Explorer
//     window is open a few seconds after the shell is ready.
//  2. Explorer.exe crashed / was restarted while the app runs: the tabs that were open before are restored once the new
//     Explorer is ready (not again if Explorer goes down again within 2 minutes of an automatic restore: no crash loops).
// When the user closes the last window, the session is marked as closed: it is NOT reopened automatically (not even after
// a reboot), but the tray item / shortcut "Restore last session's tabs" brings it back.
// Restoring opens everything in ONE window (an existing one, else one new window) with the same serialized, confirmed
// tab-opening as merging (OpenTabForMergeAsync; never "select tab N"), skips missing folders and tabs that are already
// open (Reuse Tabs), opens at most MaxRestoreTabs tabs and never runs at the same time as a merge.
public partial class ExplorerWatcher
{
    private const int SessionPollMs = 3_000;
    private const int SessionSaveThrottleMs = 5_000;        // location changes
    private const int SessionSelectionSaveMs = 15_000;      // selection-only changes
    private const int SessionHeartbeatMs = 10 * 60_000;     // keep SavedAtUtc fresh while nothing changes
    private const int AllClosedGraceMs = 4_000;             // windows must be gone this long before "the user closed them"
    private const int StartupRestoreDelayMs = 6_000;
    private const int CrashRestoreDelayMs = 4_000;
    private const int CrashLoopGuardMs = 120_000;
    private const int MaxSavedTabs = 100;
    private const int MaxSavedSelectedItems = 20;
    public const int MaxRestoreTabs = 30;
    private static readonly TimeSpan MaxSessionAge = TimeSpan.FromDays(14);

    private readonly object _sessionLock = new();
    private SessionData _session = new();
    private SessionData? _startupSession;          // as saved by the previous run
    private List<SessionWindow>? _lastSession;     // the tabs that were open the last time all windows went away
    private List<SessionWindow>? _crashSession;    // the tabs that were open when Explorer crashed
    private Timer? _sessionTimer;
    private int _sessionTickRunning;
    private string? _pendingSnapshotSignature, _savedSelectionSignature;
    private long _lastSessionSaveAt, _emptySince, _explorerTerminatedAt, _lastAutoRestoreAt;
    private bool _sessionDirty, _sessionLocationsDirty, _shellInitializedOnce;
    private volatile bool _autoRestoreEnabled = true;
    private volatile bool _isRestoring;

    /// <summary>Raised (on a background thread) after a restore. Automatic restores only raise it when something was opened or skipped.</summary>
    public event Action<RestoreResult>? SessionRestored;

    public void SetAutoRestoreSession(bool enabled) => _autoRestoreEnabled = enabled;

    private void InitializeSession()
    {
        _autoRestoreEnabled = SettingsManager.AutoRestoreSession;
        _startupSession = SessionManager.Load();
        if (_startupSession != null)
        {
            _session = new SessionData
            {
                SavedAtUtc = _startupSession.SavedAtUtc,
                IsOpen = _startupSession.IsOpen,
                Windows = _startupSession.Windows
            };
            if (_startupSession.Windows.Count > 0)
                _lastSession = _startupSession.Windows;
        }

        _sessionTimer = new Timer(SessionTick, null, SessionPollMs, SessionPollMs);
    }

    /// <summary>Called after the shell objects were (re)created: the first time = app start, later = Explorer restarted.</summary>
    private void OnShellReadyForSession()
    {
        var isRestart = _shellInitializedOnce;
        _shellInitializedOnce = true;
        if (isRestart)
            _ = Task.Run(RestoreAfterExplorerRestartAsync);
        else
            _ = Task.Run(RestoreAtStartupAsync);
    }

    /// <summary>Called when the main explorer.exe process terminated (crash / restart / logoff), before the shell objects are released.</summary>
    private void OnExplorerTerminatedForSession()
    {
        _explorerTerminatedAt = Stopwatch.GetTimestamp();
        lock (_sessionLock)
        {
            if (!_session.IsOpen || _session.Windows.Count == 0) return;
            _crashSession = _session.Windows;
            _lastSession = _session.Windows;
        }
    }

    private async Task RestoreAtStartupAsync()
    {
        try
        {
            await Task.Delay(StartupRestoreDelayMs);

            var session = _startupSession;
            if (!_autoRestoreEnabled || session is not { IsOpen: true } || session.TabCount == 0) return;
            if (DateTime.UtcNow - session.SavedAtUtc > MaxSessionAge) return;

            // Explorer must have been (re)started after the session was saved: reboot, logoff/logon, or Explorer restart.
            // Otherwise the app was simply restarted and the user may have closed those windows meanwhile.
            DateTime explorerStartUtc;
            try
            {
                var process = Helper.GetMainExplorerProcess();
                if (process == null) return;
                explorerStartUtc = process.StartTime.ToUniversalTime();
            }
            catch
            {
                return;
            }
            if (session.SavedAtUtc >= explorerStartUtc) return;

            // The user (or Windows' own "restore previous folder windows") already opened windows: leave it to them / auto-merge.
            if (Helper.GetAllExplorerWindows().Any(HasTabs)) return;

            await RestoreSessionAsync(session.Windows, manual: false);
        }
        catch
        {
            // Restoring is best effort.
        }
    }

    private async Task RestoreAfterExplorerRestartAsync()
    {
        try
        {
            var session = Interlocked.Exchange(ref _crashSession, null);
            if (session == null || !_autoRestoreEnabled) return;

            // If Explorer went down again right after an automatic restore, don't try again (no crash loops).
            if (_lastAutoRestoreAt != 0 && !Helper.IsTimeUp(_lastAutoRestoreAt, CrashLoopGuardMs)) return;

            await Task.Delay(CrashRestoreDelayMs);
            if (!_autoRestoreEnabled) return;

            await RestoreSessionAsync(session, manual: false);
        }
        catch
        {
            // Restoring is best effort.
        }
    }

    /// <summary>Tray menu / shortcut: reopen the tabs that were open the last time all windows went away (or at the end of the previous run).</summary>
    public Task<RestoreResult> RestoreLastSessionAsync()
    {
        List<SessionWindow>? source;
        lock (_sessionLock)
            source = _lastSession is { Count: > 0 } ? _lastSession : !_session.IsOpen && _session.Windows.Count > 0 ? _session.Windows : null;

        if (source == null)
        {
            var result = new RestoreResult { NoSession = true };
            SessionRestored?.Invoke(result);
            return Task.FromResult(result);
        }

        return RestoreSessionAsync(source, manual: true);
    }

    private async Task<RestoreResult> RestoreSessionAsync(List<SessionWindow> windows, bool manual)
    {
        var result = new RestoreResult { IsAutomatic = !manual };
        if (_shellWindows == null || _mainExplorerProcessId == 0) return result;

        // Never at the same time as a merge (or another restore).
        if (!await _mergeLock.WaitAsync(manual ? 15_000 : 10_000)) return result;
        _isRestoring = true;
        if (!manual) _lastAutoRestoreAt = Stopwatch.GetTimestamp();
        try
        {
            var openLocations = new List<string>();
            foreach (var (tab, _) in GetRegisteredTabs())
            {
                try { openLocations.Add(GetLocation(tab)); }
                catch { /* closed meanwhile */ }
            }

            var toOpen = new List<SessionTab>();
            foreach (var tab in windows.SelectMany(w => w.Tabs))
            {
                var location = tab.Location;
                if (string.IsNullOrWhiteSpace(location) || IsUnsupportedLocation(location)) continue;

                // Folders that are open already are never opened again; with "Reuse Tabs" duplicates within the session are skipped, too.
                if (openLocations.Any(l => IsSameLocation(l, location)) || (_reuseTabs && toOpen.Any(t => IsSameLocation(t.Location, location))))
                {
                    result.AlreadyOpen++;
                    continue;
                }

                if (!SessionLocationExists(location))
                {
                    result.Missing.Add(GetDisplayLocation(tab));
                    continue;
                }

                toOpen.Add(tab);
            }

            if (toOpen.Count > MaxRestoreTabs)
            {
                result.OverLimit = toOpen.Count - MaxRestoreTabs;
                toOpen = toOpen.Take(MaxRestoreTabs).ToList();
            }

            if (toOpen.Count == 0) return result;

            // ONE window: the foreground Explorer window, else the most recently used one, else a new window for the first tab.
            var foreground = WinApi.GetForegroundWindow();
            var target = Helper.IsFileExplorerWindow(foreground) && IsNormalExplorerWindow(foreground)
                ? foreground
                : Helper.GetAllExplorerWindows().FirstOrDefault(IsNormalExplorerWindow);

            var startIndex = 0;
            if (target == 0)
            {
                var before = Helper.GetAllExplorerWindows().ToArray();
                await OpenNewWindowWithSelection(new WindowRecord(toOpen[0].Location, 0, toOpen[0].SelectedItems));
                target = await Helper.ListenForNewExplorerWindowAsync(before, 8_000);
                if (target == 0)
                {
                    result.Failed = toOpen.Count;
                    return result;
                }

                result.OpenedTabs++;
                startIndex = 1;

                // New tabs are requested through the window's tab: wait until it exists and is registered.
                await WaitForTabsSettledAsync(target, 5_000);
            }

            for (var i = startIndex; i < toOpen.Count; i++)
            {
                if (!Helper.IsFileExplorerWindow(target))
                {
                    result.Failed += toOpen.Count - i;
                    break;
                }

                if (await OpenTabForMergeAsync(target, toOpen[i].Location, toOpen[i].SelectedItems))
                    result.OpenedTabs++;
                else
                    result.Failed++;
            }

            if (Helper.IsFileExplorerWindow(target))
            {
                _mainWindowHandle = target;
                if (manual) WinApi.RestoreWindowToForeground(target);
            }

            return result;
        }
        catch
        {
            return result;
        }
        finally
        {
            _lastMergeFinishedAt = Stopwatch.GetTimestamp(); // pauses auto-merge and the "background tab" heuristic for a moment
            _isRestoring = false;
            _mergeLock.Release();

            if (manual || result.OpenedTabs > 0 || result.Missing.Count > 0 || result.Failed > 0)
                SessionRestored?.Invoke(result);
        }
    }

    // ---- Observing the session ----

    private void SessionTick(object? _)
    {
        if (Interlocked.Exchange(ref _sessionTickRunning, 1) == 1) return;
        try
        {
            if (_shellWindows == null || _mainExplorerProcessId == 0) return;

            // Intermediate states of our own operations are not a session.
            if (_isRestoring || _isMerging || _toOpenWindowsLock.CurrentCount == 0)
            {
                _pendingSnapshotSignature = null;
                return;
            }

            var snapshot = TakeSessionSnapshot();
            if (snapshot == null)
            {
                _pendingSnapshotSignature = null; // tabs are being created/closed right now
                return;
            }

            if (snapshot.Count == 0)
            {
                _pendingSnapshotSignature = null;
                HandleNoWindowsOpen();
                return;
            }

            _emptySince = 0;

            // The same windows/tabs must be seen twice in a row (a window that is being closed or a crash in progress is not saved).
            var signature = GetLocationSignature(snapshot);
            if (signature != _pendingSnapshotSignature)
            {
                _pendingSnapshotSignature = signature;
                return;
            }

            lock (_sessionLock)
            {
                var locationsChanged = !_session.IsOpen || GetLocationSignature(_session.Windows) != signature;
                var selectionSignature = GetSelectionSignature(snapshot);
                if (locationsChanged || selectionSignature != _savedSelectionSignature)
                {
                    _session.Windows = snapshot;
                    _session.IsOpen = true;
                    _savedSelectionSignature = selectionSignature;
                    _sessionDirty = true;
                    _sessionLocationsDirty |= locationsChanged;
                }

                var isDue = _sessionLocationsDirty ? Helper.IsTimeUp(_lastSessionSaveAt, SessionSaveThrottleMs)
                    : _sessionDirty ? Helper.IsTimeUp(_lastSessionSaveAt, SessionSelectionSaveMs)
                    : Helper.IsTimeUp(_lastSessionSaveAt, SessionHeartbeatMs);
                if (isDue) WriteSession();
            }
        }
        catch
        {
            // Explorer restarted meanwhile; try again next time.
        }
        finally
        {
            Interlocked.Exchange(ref _sessionTickRunning, 0);
        }
    }

    private void HandleNoWindowsOpen()
    {
        lock (_sessionLock)
        {
            if (!_session.IsOpen || _session.Windows.Count == 0) return;

            if (_emptySince == 0)
            {
                _emptySince = Stopwatch.GetTimestamp();
                return;
            }
            if (!Helper.IsTimeUp(_emptySince, AllClosedGraceMs)) return;

            // Windows disappearing because Explorer crashed / restarted or Windows is shutting down is NOT "the user closed them".
            if ((_explorerTerminatedAt != 0 && !Helper.IsTimeUp(_explorerTerminatedAt, 15_000)) || IsSystemShuttingDown() || !IsMainExplorerAlive()) return;

            // The user closed the last window: remember the tabs for the tray item, but never reopen them automatically.
            _lastSession = _session.Windows;
            _session.IsOpen = false;
            WriteSession();
        }
    }

    /// <summary>Must be called with <see cref="_sessionLock"/> held.</summary>
    private void WriteSession()
    {
        _session.SavedAtUtc = DateTime.UtcNow;
        SessionManager.Save(_session);
        _lastSessionSaveAt = Stopwatch.GetTimestamp();
        _sessionDirty = false;
        _sessionLocationsDirty = false;
    }

    private void ShutdownSession()
    {
        _sessionTimer?.Dispose();
        _sessionTimer = null;
        lock (_sessionLock)
            if (_sessionDirty) WriteSession();
    }

    /// <summary>
    /// The open Explorer windows (most recently used first) with their tabs, or null while tabs are being created/closed.
    /// Windows that are being converted into a tab and Control Panel tabs are left out.
    /// </summary>
    private List<SessionWindow>? TakeSessionSnapshot()
    {
        var frames = Helper.GetAllExplorerWindows()
            .Where(h => WinApi.IsWindowVisible(h) && !Helper.HiddenWindows.ContainsKey(h) && HasTabs(h))
            .ToList();
        if (frames.Count == 0) return [];

        var byFrame = frames.ToDictionary(h => h, _ => new SessionWindow());
        var registeredCount = frames.ToDictionary(h => h, _ => 0);
        var savedTabs = 0;
        foreach (var (tab, frame) in GetRegisteredTabs())
        {
            if (!byFrame.TryGetValue(frame, out var window)) continue;
            registeredCount[frame]++;

            string location;
            try { location = GetLocation(tab); }
            catch { return null; }

            if (string.IsNullOrWhiteSpace(location) || IsUnsupportedLocation(location) || savedTabs >= MaxSavedTabs) continue;

            string? name = null;
            try { name = tab.LocationName; }
            catch { /* optional */ }

            window.Tabs.Add(new SessionTab { Location = location, Name = name, SelectedItems = GetSelectedItemsForSession(tab) });
            savedTabs++;
        }

        // Every tab window must have its registered tab object, otherwise the window is still changing.
        if (frames.Any(h => Helper.GetAllExplorerTabs(h).Count() != registeredCount[h])) return null;

        return frames.Select(h => byFrame[h]).Where(w => w.Tabs.Count > 0).ToList();
    }

    private static bool HasTabs(nint window) => WinApi.FindWindowEx(window, 0, "ShellTabWindowClass", null) != 0;

    private static bool IsUnsupportedLocation(string location) =>
        UnsupportedLocationMarkers.Any(m => location.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);

    private static string GetLocationSignature(List<SessionWindow> windows) =>
        string.Join("\n", windows.Select(w => string.Join("|", w.Tabs.Select(t => t.Location))));

    private static string GetSelectionSignature(List<SessionWindow> windows) =>
        string.Join("\n", windows.SelectMany(w => w.Tabs).Select(t => t.SelectedItems == null ? string.Empty : string.Join("|", t.SelectedItems)));

    private static string[]? GetSelectedItemsForSession(InternetExplorer tab)
    {
        try
        {
            if (tab.Document is not ShellFolderView view) return null;
            var count = view.SelectedItems().Count;
            if (count == 0 || count > MaxSavedSelectedItems) return null;
            return GetSelectedItems(tab);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"file:\\\C:\My%20Folder" => "C:\My Folder" (shell locations are returned unchanged).</summary>
    private static string ToFileSystemPath(string location)
    {
        if (!location.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return location;

        var path = location.Substring(5).Replace('/', '\\');
        if (path.StartsWith(@"\\\")) path = path.Substring(3);        // file:///C:/...  => C:\...
        else if (!path.StartsWith(@"\\")) path = path.TrimStart('\\'); // file:C:/...     => C:\...
        try { path = Uri.UnescapeDataString(path); }
        catch { /* keep as is */ }
        return path;
    }

    private static string GetDisplayLocation(SessionTab tab)
    {
        var path = ToFileSystemPath(tab.Location);
        return path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(tab.Name) ? tab.Name! : path;
    }

    private bool SessionLocationExists(string location)
    {
        try
        {
            var path = ToFileSystemPath(location);

            // Network shares may not be reachable yet right after logon, and checking them can block: let navigation decide.
            if (path.StartsWith(@"\\")) return true;

            if (!path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && System.IO.Path.IsPathRooted(path))
                return System.IO.Directory.Exists(path);

            nint pidl = 0;
            try
            {
                pidl = _shellPathComparer.GetPidlFromPath(location);
                return pidl != 0;
            }
            finally
            {
                if (pidl != 0) Marshal.FreeCoTaskMem(pidl);
            }
        }
        catch
        {
            return false;
        }
    }

    private bool IsMainExplorerAlive()
    {
        var pid = _mainExplorerProcessId;
        if (pid == 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSystemShuttingDown() => WinApi.GetSystemMetrics(WinApi.SM_SHUTTINGDOWN) != 0;
}
