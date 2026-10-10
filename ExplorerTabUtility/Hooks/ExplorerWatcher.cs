using Shell32;
using SHDocVw;
using System;
using System.Linq;
using System.Windows;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Interop;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.WinAPI;
using ExplorerTabUtility.Localization;
using ExplorerTabUtility.UI.Views;

namespace ExplorerTabUtility.Hooks;

using WindowEntry = DualKeyEntry<InternetExplorer, nint?, WindowInfo>;

public partial class ExplorerWatcher : IHook
{
    private static bool _instanceRunning;
    private static Guid _shellBrowserGuid = typeof(IShellBrowser).GUID;

    private ShellWindows _shellWindows = null!;
    private ShellPathComparer _shellPathComparer = null!;
    private StaTaskScheduler _staTaskScheduler = null!;
    private nint _mainWindowHandle;
    private readonly ConcurrentDictionary<nint, byte> _processedHWnds = new();
    private readonly DualKeyDictionary<InternetExplorer, nint?, WindowInfo> _windowEntryDict = [];
    private readonly List<WindowRecord> _closedWindows = new();
    private readonly object _windowEntryDictLock = new(), _closedWindowsLock = new(), _processLock = new();
    private readonly SemaphoreSlim _toOpenWindowsLock = new(1);
    private readonly ProcessWatcher _processWatcher;
    private int _mainExplorerProcessId;
    private Timer? _explorerCheckTimer;

    private nint _eventObjectShowHookId;
    private WinEventDelegate? _eventObjectShowHookCallback;
    private DShellWindowsEvents_WindowRegisteredEventHandler? _windowRegisteredHandler;

    private string _defaultLocation = null!;

    // Folders that were just converted from a new window into a tab (location, Environment.TickCount).
    // Used to make sure one "open folder" request from another app only ends up as ONE tab.
    private const int DuplicateRequestWindowMs = 2_000;
    private const int RecentTabMaxAgeMs = 3_000;
    private readonly List<(string Location, int Tick)> _recentConversions = new();
    private readonly object _recentConversionsLock = new();
    private bool _reuseTabs = true;
    private static long _lastOwnSelectionTicks;
    // IShellView/ShellFolderView.SelectItem flags (SVSI_*)
    private const int SvsiSelect = 0x1, SvsiDeselectOthers = 0x4, SvsiEnsureVisible = 0x8, SvsiFocused = 0x10;
    private bool _isForcingTabs;
    public bool IsHookActive => _isForcingTabs;
    public event Action? OnShellInitialized;

    public ExplorerWatcher()
    {
        if (_instanceRunning)
            throw new InvalidOperationException("Only one instance of ExplorerWatcher is allowed at a time.");
        _instanceRunning = true;

        _processWatcher = new ProcessWatcher("explorer");
        _processWatcher.ProcessTerminated += OnExplorerProcessTerminated;
        InitializeSession();
        StartExplorerProcessCheck();
    }

    public void StartHook()
    {
        if (_isForcingTabs) return;
        _isForcingTabs = true;
    }

    public void StopHook()
    {
        if (!_isForcingTabs) return;
        _isForcingTabs = false;
    }
    public void SetReuseTabs(bool reuseTabs) => _reuseTabs = reuseTabs;

    public void ClearClosedWindows()
    {
        lock (_closedWindowsLock)
            _closedWindows.Clear();
    }

    public IReadOnlyCollection<WindowRecord> GetWindows()
    {
        var result = new List<WindowRecord>();
        
        // Add open windows
        lock (_windowEntryDictLock)
            result.AddRange(
                _windowEntryDict.Keys.Select(ie => new WindowRecord(GetLocation(ie), new IntPtr(ie.HWND), GetSelectedItems(ie), ie.LocationName)));
        
        // Add closed windows in reverse order (last closed on top)
        lock (_closedWindowsLock)
            result.AddRange(_closedWindows.AsEnumerable().Reverse());
        
        return result.GroupBy(w => w.Location).Select(g => g.First()).ToList();
    }

    public async Task SwitchTo(string location, nint windowHandle = 0, string[]? selectedItems = null, bool asTab = true, bool duplicate = false)
    {
        var windowToOpen = new WindowRecord(location, windowHandle, selectedItems);
        if (!asTab)
        {
            await OpenNewWindowWithSelection(windowToOpen);
            return;
        }

        await OpenTabNavigateWithSelection(windowToOpen, windowHandle, duplicate, true);
    }
    
    public nint SearchForTab(string targetPath)
    {
        nint targetPidl = 0;
        try
        {
            targetPidl = _shellPathComparer.GetPidlFromPath(targetPath);
            if (targetPidl == 0) return 0;

            foreach (var (window, windowInfo, tabHandle) in _windowEntryDict)
            {
                // Make sure it is not the newly created window
                if (!Helper.IsTimeUp(windowInfo.CreatedAt, 2_000) || !tabHandle.HasValue || tabHandle.Value == 0)
                    continue;

                var comparePath = windowInfo.Location ?? GetLocation(window);

                if (_shellPathComparer.IsEquivalent(targetPath, comparePath, targetPidl))
                    return tabHandle.Value;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (targetPidl != 0)
                Marshal.FreeCoTaskMem(targetPidl);
        }
    }
    public async Task SelectTabByHandle(nint windowHandle, nint tabHandle)
    {
        // Don't walk through the tab positions while tabs are still being created/closed (see SelectTabByIndex).
        if (!await WaitForTabsSettledAsync(windowHandle, 1_500)) return;

        var tabs = Helper.GetAllExplorerTabs(windowHandle).ToArray();
        if (tabs.Length == 0) return;

        var activeTab = tabs[0];
        for (var i = 0; i < tabs.Length; i++)
        {
            if (activeTab == tabHandle) break;

            SelectTabByIndex(windowHandle, i);

            // ReSharper disable once AccessToModifiedClosure
            activeTab = await Helper.DoUntilConditionAsync(
                () => WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null),
                h => h != activeTab);
        }
    }
    public void SelectTabByIndex(nint windowHandle, int index)
    {
        // Windows 11 Explorer crashes (and restarts the taskbar/desktop) when it gets this command for a tab position its
        // tab strip doesn't have: uncaught E_INVALIDARG in FileExplorerTabsController::GetTabIdAtPosition (two crash dumps
        // of the tab groups build, WM_COMMAND 0xA221 with lParam 2 and 3). While tabs are being created or closed the number
        // of tab windows can be ahead of the tab strip, so only send it for a position that the tab windows AND the
        // registered tabs agree on.
        var safeCount = Math.Min(Helper.GetAllExplorerTabs(windowHandle).Count(), CountRegisteredTabs(windowHandle));
        if (index < 0 || index >= safeCount) return;

        // Send 0xA221 magic command (CTRL + 1...n)
        WinApi.SendMessage(windowHandle, WinApi.WM_COMMAND, 0xA221, index + 1);
    }
    public async Task RequestToOpenNewTab(nint windowHandle, bool bringToFront = false, bool lockToOpenWindows = true)
    {
        if (bringToFront && windowHandle == 0)
            windowHandle = GetMainWindowHWnd(0);

        if (windowHandle == 0)
        {
            await OpenNewWindowWithSelection(new WindowRecord(string.Empty), lockToOpenWindows);
            return;
        }

        var tabHandle = WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
        if (tabHandle == 0) return;

        // Send 0xA21B magic command (CTRL + T)
        WinApi.PostMessage(tabHandle, WinApi.WM_COMMAND, 0xA21B, 0);

        if (bringToFront)
            WinApi.RestoreWindowToForeground(windowHandle);
    }
    public async Task Open(string? location, bool asTab, nint windowHandle, int delay = 0)
    {
        if (delay > 0)
            await Task.Delay(delay);

        var normalizedPath = Helper.NormalizeLocation(location ?? string.Empty);
        
        if (normalizedPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
            System.IO.File.Exists(normalizedPath))
        {
            try
            {
                Helper.BypassWinForegroundRestrictions();
                Process.Start(new ProcessStartInfo(normalizedPath) { UseShellExecute = true });
                return;
            }
            catch
            {
                //
            }
        }
        
        if (!asTab)
        {
            await OpenNewWindowWithSelection(new WindowRecord(normalizedPath));
            return;
        }

        if (string.IsNullOrWhiteSpace(normalizedPath) && !_reuseTabs)
        {
            await RequestToOpenNewTab(windowHandle, bringToFront: true);
            return;
        }

        if (_windowEntryDict.Count > 0)
        {
            OpenNewTab(windowHandle, normalizedPath);
            return;
        }

        await OpenNewWindowWithSelection(new WindowRecord(normalizedPath));
    }
    public void OpenNewTab(nint windowHandle, string location)
    {
        _ = OpenTabNavigateWithSelection(new WindowRecord(location, windowHandle), windowHandle);
    }
    public async Task DuplicateActiveTab(nint windowHandle, bool asTab)
    {
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        if (window == null) return;

        var location = _windowEntryDict[window].Value.Location ?? GetLocation(window);
        var selectedItems = GetSelectedItems(window);
        var windowRecord = new WindowRecord(location, windowHandle, selectedItems);

        if (!asTab)
        {
            await OpenNewWindowWithSelection(windowRecord);
            return;
        }

        await OpenTabNavigateWithSelection(windowRecord, windowHandle, isDuplicate: true);
    }
    public async Task ReopenClosedTab(bool asTab, nint windowHandle = 0)
    {
        WindowRecord? closedWindow;
        lock (_closedWindowsLock)
        {
            closedWindow = _closedWindows.LastOrDefault(w => w.Location != _defaultLocation);
            if (closedWindow == null) return;
            _closedWindows.Remove(closedWindow);
        }

        if (!asTab)
        {
            closedWindow.CreatedAt = Environment.TickCount;
            await OpenNewWindowWithSelection(closedWindow);
            return;
        }

        await OpenTabNavigateWithSelection(closedWindow, windowHandle);
    }
    public async Task DetachCurrentTab(nint windowHandle)
    {
        if (Helper.GetAllExplorerTabs(windowHandle).Take(2).Count() < 2)
            return;

        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        if (window == null) return;

        var location = _windowEntryDict[window].Value.Location ?? GetLocation(window);
        var selectedItems = GetSelectedItems(window);
        var windowRecord = new WindowRecord(location, windowHandle, selectedItems);

        // Send 0xA021 magic command (CTRL + W)
        WinApi.SendMessage(activeTabHandle, WinApi.WM_COMMAND, 0xA021, 1);

        await OpenNewWindowWithSelection(windowRecord);
    }
    public void SetTargetWindow(nint windowHandle)
    {
        if (Helper.IsFileExplorerWindow(windowHandle))
            _mainWindowHandle = windowHandle;
    }
    public void NavigateBackForward(nint windowHandle, bool isForward)
    {
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        try
        {
            if (isForward) window?.GoForward();
            else window?.GoBack();
        }
        catch
        {
            // Will throw if there is no further history
        }
    }

    // ---- Automatic merging of Explorer windows ----
    // Moves the tabs of every other normal File Explorer window into one window and closes the emptied windows.
    //
    // Why it is done like this (see the crash analysis in the PR): Windows 11 Explorer crashes - taking the taskbar and
    // desktop with it - when it receives the "select tab N" command (WM_COMMAND 0xA221) for a tab position its tab strip
    // doesn't know (uncaught E_INVALIDARG in FileExplorerTabsController::GetTabIdAtPosition). While tabs are being created
    // in quick succession the tab windows and the tab strip can disagree. So merging
    //  - never sends 0xA221 (tabs are only added with Ctrl+T, which always works on the window's own tab),
    //  - adds ONE tab at a time (under the same lock as every other "open tab" operation) and only continues after the
    //    new tab exists, is registered, has navigated to its folder and the window's tabs are consistent again,
    //  - never brings windows to the front or simulates input when it runs automatically,
    //  - closes a source window only after ALL of its tabs were confirmed open in the target window.
    private const int AutoMergeIntervalMs = 1_000;
    private const int AutoMergeSettleMs = 1_500;   // the set of windows must be unchanged this long
    private const int AutoMergeIdleMs = 1_500;     // no keyboard/mouse input for this long
    private const int MaxSelectedItemsToRestore = 100;

    // Control Panel style windows are CabinetWClass windows too, but they are not file folders: never merge them.
    private static readonly string[] UnsupportedLocationMarkers =
    [
        "{26EE0668-A00A-44D7-9371-BEB064C98683}", // Control Panel (category view and its pages)
        "{21EC2020-3AEA-1069-A2DD-08002B30309D}", // All Control Panel Items
        "{ED7BA470-8E54-465E-825C-99712043E01C}", // "God mode" (All Tasks)
    ];

    // Windows the user opened as a separate window on purpose while the app was running (Ctrl+Shift, a detached tab,
    // "open as window" actions of this app, or while the window hook was off). Automatic merging leaves them alone.
    private readonly ConcurrentDictionary<nint, byte> _keepAsWindow = new();
    // Windows whose merge failed (window -> its tabs at that time). Not retried automatically until their tabs change.
    private readonly ConcurrentDictionary<nint, string> _mergeFailedWindows = new();
    private readonly SemaphoreSlim _mergeLock = new(1);
    // Shell windows this app has already looked at. Fallback for the "seenBefore" property, which not every entry of
    // ShellWindows implements (PutProperty/GetProperty can fail with E_NOTIMPL).
    private readonly ConditionalWeakTable<object, object> _seenShellWindows = new();
    private Timer? _autoMergeTimer;
    private volatile bool _autoMergeEnabled;
    private volatile bool _isMerging;
    private int _autoMergeTickRunning;
    private string? _autoMergeSignature, _lastUnsuccessfulAutoMergeSignature;
    private long _autoMergeSignatureSince, _lastWindowRegisteredAt, _lastMergeFinishedAt;

    private sealed class MergeTab(string location, string[]? selectedItems)
    {
        public string Location { get; } = location;
        public string[]? SelectedItems { get; } = selectedItems;
    }
    private sealed class MergeWindow(nint handle)
    {
        public nint Handle { get; } = handle;
        public List<MergeTab> Tabs { get; } = [];
        public int TabWindowCount { get; set; }
        public bool HasUnsupportedTab { get; set; }
        /// <summary>Every tab window has a registered tab object (no tab is still being created or closed).</summary>
        public bool IsConsistent => TabWindowCount > 0 && TabWindowCount == Tabs.Count;
        public bool CanBeMerged => IsConsistent && !HasUnsupportedTab;
        public string Signature => string.Join("|", Tabs.Select(t => t.Location));
    }

    public void SetAutoMergeWindows(bool enabled)
    {
        _autoMergeEnabled = enabled;
        _autoMergeSignature = null;
        _lastUnsuccessfulAutoMergeSignature = null;

        if (enabled)
            _autoMergeTimer ??= new Timer(AutoMergeTick, null, AutoMergeIntervalMs, AutoMergeIntervalMs);
        else
        {
            _autoMergeTimer?.Dispose();
            _autoMergeTimer = null;
        }
    }

    /// <summary>
    /// Merges the normal File Explorer windows into one window.
    /// <paramref name="manual"/> (tray menu / shortcut): all windows, including the ones kept on purpose; the result window is brought to the front.
    /// Automatic: windows the user opened as separate windows on purpose and windows that failed before are skipped, nothing is activated.
    /// </summary>
    public async Task<MergeResult> MergeWindowsAsync(bool manual, nint preferredTarget = 0)
    {
        var result = new MergeResult();
        if (_shellWindows == null || _mainExplorerProcessId == 0) return result;

        if (!await _mergeLock.WaitAsync(manual ? 15_000 : 0)) return result;
        _isMerging = true;
        var startTick = Environment.TickCount;
        try
        {
            // A manual merge may start right after a click in the tray / a shortcut: give tabs that are still being created a moment.
            var windows = GetMergeWindows();
            if (manual && windows.Any(w => !w.IsConsistent))
            {
                await Task.Delay(1_000);
                windows = GetMergeWindows();
            }

            var candidates = windows
                .Where(w => manual || (!_keepAsWindow.ContainsKey(w.Handle) && !IsKnownMergeFailure(w)))
                .ToList();
            if (candidates.Count < 2) return result;

            // Target: the foreground Explorer window, else the most recently used one (windows are in Z-order).
            if (preferredTarget == 0) preferredTarget = WinApi.GetForegroundWindow();
            var target = candidates.FirstOrDefault(w => w.Handle == preferredTarget && w.CanBeMerged)
                         ?? candidates.FirstOrDefault(w => w.CanBeMerged);
            if (target == null) return result;

            var sources = candidates.Where(w => w != target && w.CanBeMerged).ToList();
            if (sources.Count == 0) return result;

            var targetLocations = target.Tabs.Select(t => t.Location).ToList();
            foreach (var source in sources)
            {
                if (!Helper.IsFileExplorerWindow(target.Handle)) break;

                // Automatic merge: stop (between windows) as soon as the user does something.
                if (!manual && WinApi.GetUserIdleTimeMs() < (uint)(Environment.TickCount - startTick)) break;

                var allOpened = true;
                foreach (var tab in source.Tabs)
                {
                    // Reuse tabs: a folder that is already open in the target window is not opened a second time.
                    if (_reuseTabs && targetLocations.Any(l => IsSameLocation(l, tab.Location)))
                        continue;

                    if (!await OpenTabForMergeAsync(target.Handle, tab.Location, tab.SelectedItems))
                    {
                        allOpened = false;
                        break;
                    }

                    targetLocations.Add(tab.Location);
                    result.MovedTabs++;
                }

                // Only close the window when everything it showed is open in the target window, and it didn't change meanwhile.
                if (!allOpened || GetCurrentSignature(source.Handle) != source.Signature || !await CloseWindowForMergeAsync(source.Handle))
                {
                    _mergeFailedWindows[source.Handle] = source.Signature;
                    result.FailedWindows++;
                    continue;
                }

                RemoveClosedRecords(source.Handle, startTick);
                _keepAsWindow.TryRemove(source.Handle, out _);
                result.MergedWindows++;
            }

            if (result.MergedWindows > 0)
                _mainWindowHandle = target.Handle;

            if (manual && Helper.IsFileExplorerWindow(target.Handle))
                WinApi.RestoreWindowToForeground(target.Handle);

            return result;
        }
        catch
        {
            return result;
        }
        finally
        {
            _lastMergeFinishedAt = Stopwatch.GetTimestamp();
            _isMerging = false;
            _mergeLock.Release();
        }
    }

    private async void AutoMergeTick(object? _)
    {
        if (Interlocked.Exchange(ref _autoMergeTickRunning, 1) == 1) return;
        try
        {
            // Only while the window hook is on (turning it off means "I want separate windows").
            if (!_autoMergeEnabled || !_isForcingTabs || _shellWindows == null || _mainExplorerProcessId == 0)
            {
                _autoMergeSignature = null;
                return;
            }

            PruneClosedWindows();

            // Don't interfere with the window hook (a hidden window is being converted into a tab) or another tab operation.
            if (_isMerging || _isRestoring || _toOpenWindowsLock.CurrentCount == 0 || Helper.HiddenWindows.Keys.Any(Helper.IsFileExplorerWindow))
            {
                _autoMergeSignature = null;
                return;
            }

            var windows = Helper.GetAllExplorerWindows()
                .Where(h => !_keepAsWindow.ContainsKey(h) && IsNormalExplorerWindow(h))
                .ToList();
            if (windows.Count < 2)
            {
                _autoMergeSignature = null;
                return;
            }

            // Cheap fingerprint of the situation (windows and their number of tabs). Wait until it stays the same for a moment.
            var signature = string.Join(";", windows
                .OrderBy(h => (long)h)
                .Select(h => $"{(long)h:X}:{Helper.GetAllExplorerTabs(h).Count()}"));
            if (signature == _lastUnsuccessfulAutoMergeSignature) return; // Nothing changed since the last attempt that merged nothing.
            if (signature != _autoMergeSignature)
            {
                _autoMergeSignature = signature;
                _autoMergeSignatureSince = Stopwatch.GetTimestamp();
                return;
            }

            if (!Helper.IsTimeUp(_autoMergeSignatureSince, AutoMergeSettleMs) ||
                !Helper.IsTimeUp(_lastWindowRegisteredAt, 2_000) ||
                !Helper.IsTimeUp(_lastMergeFinishedAt, 3_000))
                return;

            // Don't interrupt the user: wait until there was no input for a moment and no mouse button is held (dragging a tab/window).
            if (WinApi.GetUserIdleTimeMs() < AutoMergeIdleMs || IsMouseButtonDown()) return;

            var result = await MergeWindowsAsync(manual: false);
            _lastUnsuccessfulAutoMergeSignature = result.MergedWindows == 0 ? signature : null;
            _autoMergeSignature = null;
        }
        catch
        {
            // Never let the timer die because of a closed window / restarted Explorer.
        }
        finally
        {
            Interlocked.Exchange(ref _autoMergeTickRunning, 0);
        }
    }

    private static bool IsMouseButtonDown()
    {
        return (WinApi.GetAsyncKeyState(0x01) & 0x8000) != 0 || // VK_LBUTTON
               (WinApi.GetAsyncKeyState(0x02) & 0x8000) != 0 || // VK_RBUTTON
               (WinApi.GetAsyncKeyState(0x04) & 0x8000) != 0;   // VK_MBUTTON
    }

    /// <summary>
    /// A visible File Explorer window on the current virtual desktop that has tabs and is not being converted into a tab right now.
    /// (Open/save dialogs and other apps use other window classes.)
    /// </summary>
    private static bool IsNormalExplorerWindow(nint hWnd)
    {
        return WinApi.IsWindowVisible(hWnd) &&
               !WinApi.IsWindowCloaked(hWnd) &&
               !Helper.HiddenWindows.ContainsKey(hWnd) &&
               WinApi.FindWindowEx(hWnd, 0, "ShellTabWindowClass", null) != 0;
    }

    /// <summary>The normal Explorer windows (most recently used first) with their tabs in the order Explorer registered them.</summary>
    private List<MergeWindow> GetMergeWindows()
    {
        var windows = Helper.GetAllExplorerWindows()
            .Where(IsNormalExplorerWindow)
            .Select(h => new MergeWindow(h))
            .ToList();
        if (windows.Count < 2) return windows;

        var byHandle = windows.ToDictionary(w => w.Handle);
        foreach (var (tab, frame) in GetRegisteredTabs())
        {
            if (!byHandle.TryGetValue(frame, out var window)) continue;

            string location;
            try { location = GetLocation(tab); }
            catch { location = string.Empty; }

            if (!IsMergeableLocation(location))
                window.HasUnsupportedTab = true;

            window.Tabs.Add(new MergeTab(location, GetSelectedItemsForMerge(tab)));
        }

        foreach (var window in windows)
            window.TabWindowCount = Helper.GetAllExplorerTabs(window.Handle).Count();

        return windows;
    }

    /// <summary>All registered Explorer tabs with the handle of the window that hosts them, in registration order.</summary>
    private List<(InternetExplorer Tab, nint Window)> GetRegisteredTabs()
    {
        var result = new List<(InternetExplorer, nint)>();
        try
        {
            var count = _shellWindows.Count;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    if (_shellWindows.Item(i) is not InternetExplorer tab) continue;
                    var frame = new IntPtr(tab.HWND);
                    if (frame == 0) continue; // an entry without a window
                    result.Add((tab, frame));
                }
                catch
                {
                    // The tab was closed meanwhile.
                }
            }
        }
        catch
        {
            // Explorer restarted
        }

        return result;
    }

    private int CountRegisteredTabs(nint window) => GetRegisteredTabs().Count(t => t.Window == window);

    private string? GetCurrentSignature(nint window)
    {
        if (!Helper.IsFileExplorerWindow(window)) return null;

        var locations = new List<string>();
        foreach (var (tab, frame) in GetRegisteredTabs())
        {
            if (frame != window) continue;
            try { locations.Add(GetLocation(tab)); }
            catch { return null; }
        }

        return string.Join("|", locations);
    }

    private bool IsKnownMergeFailure(MergeWindow window)
    {
        return _mergeFailedWindows.TryGetValue(window.Handle, out var signature) && signature == window.Signature;
    }

    private bool IsMergeableLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location)) return false;
        if (UnsupportedLocationMarkers.Any(m => location.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) return false;

        // This PC, Recycle Bin, Home, Network, libraries, shell:::{CLSID} ... are fine as long as the shell can resolve them again.
        nint pidl = 0;
        try
        {
            pidl = _shellPathComparer.GetPidlFromPath(location);
            return pidl != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pidl != 0) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static string[]? GetSelectedItemsForMerge(InternetExplorer tab)
    {
        try
        {
            if (tab.Document is not ShellFolderView view) return null;
            var count = view.SelectedItems().Count;
            if (count == 0 || count > MaxSelectedItemsToRestore) return null;
            return GetSelectedItems(tab);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Waits until every tab window of <paramref name="window"/> has a registered tab object and the number of tabs
    /// stayed the same for a short moment (no tab is being created or closed). Returns false on timeout.
    /// </summary>
    private async Task<bool> WaitForTabsSettledAsync(nint window, int timeoutMs)
    {
        var startTicks = Stopwatch.GetTimestamp();
        var previous = -1;
        while (true)
        {
            if (!Helper.IsFileExplorerWindow(window)) return false;

            var tabWindows = Helper.GetAllExplorerTabs(window).Count();
            var consistent = tabWindows > 0 && tabWindows == CountRegisteredTabs(window);
            if (consistent && tabWindows == previous) return true;

            previous = consistent ? tabWindows : -1;
            if (Helper.IsTimeUp(startTicks, timeoutMs)) return false;
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Opens ONE new tab with <paramref name="location"/> in <paramref name="window"/> and waits until it really shows that location.
    /// Never creates a window and never activates anything. Returns false if the tab could not be confirmed.
    /// </summary>
    private async Task<bool> OpenTabForMergeAsync(nint window, string location, string[]? selectedItems)
    {
        await _toOpenWindowsLock.WaitAsync();
        try
        {
            if (!await WaitForTabsSettledAsync(window, 3_000)) return false;

            var currentTabs = Helper.GetAllExplorerTabs(window).ToArray();
            if (currentTabs.Length == 0) return false;

            // Send 0xA21B magic command (CTRL + T), exactly like RequestToOpenNewTab.
            WinApi.PostMessage(currentTabs[0], WinApi.WM_COMMAND, 0xA21B, 0);

            var newTabHandle = await Helper.ListenForNewExplorerTabAsync(window, currentTabs, 3_000);
            if (newTabHandle == 0) return false;

            var tab = await Helper.DoUntilNotDefaultAsync(() => GetWindowByTabHandle(newTabHandle), 3_000, 50);
            if (tab == null) return false;

            await Navigate(tab, location);

            // Confirmed only when the new tab reports the requested location.
            var opened = await Helper.DoUntilConditionAsync(() => IsTabAt(tab, location), ok => ok, 6_000, 100);
            if (!opened) return false;

            if (selectedItems?.Length > 0)
            {
                try
                {
                    await Helper.DoUntilConditionAsync(() => tab.ReadyState, s => s == tagREADYSTATE.READYSTATE_COMPLETE, 2_000, 50);
                    SelectItems(tab, selectedItems);
                }
                catch
                {
                    // Restoring the selection is a nice-to-have.
                }
            }

            // Let the window finish registering the new tab before the next one is requested.
            await WaitForTabsSettledAsync(window, 2_000);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _toOpenWindowsLock.Release();
        }
    }

    private bool IsTabAt(InternetExplorer tab, string location)
    {
        try
        {
            return IsSameLocation(GetLocation(tab), location);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CloseWindowForMergeAsync(nint window)
    {
        if (!Helper.IsFileExplorerWindow(window)) return true;

        WinApi.PostMessage(window, WinApi.WM_CLOSE, 0, 0);
        return await Helper.DoUntilConditionAsync(() => !WinApi.IsWindow(window) || !Helper.IsFileExplorerWindow(window), gone => gone, 3_000, 50);
    }

    /// <summary>The tabs of a merged window were moved, not closed: keep them out of the "reopen closed" history.</summary>
    private void RemoveClosedRecords(nint window, int sinceTick)
    {
        RemoveNow();
        // OnQuit of the closed tabs can arrive a bit later.
        _ = Task.Delay(1_500).ContinueWith(_ => RemoveNow(), TaskScheduler.Default);
        return;

        void RemoveNow()
        {
            lock (_closedWindowsLock)
                _closedWindows.RemoveAll(r => r.Handle == window && r.CreatedAt - sinceTick >= 0);
        }
    }

    private void PruneClosedWindows()
    {
        // Windows converted into tabs are closed while still listed as hidden; forget them (window handles get reused).
        foreach (var hWnd in Helper.HiddenWindows.Keys)
            if (!WinApi.IsWindow(hWnd)) Helper.HiddenWindows.TryRemove(hWnd, out _);

        foreach (var hWnd in _keepAsWindow.Keys)
            if (!Helper.IsFileExplorerWindow(hWnd)) _keepAsWindow.TryRemove(hWnd, out _);

        foreach (var hWnd in _mergeFailedWindows.Keys)
            if (!Helper.IsFileExplorerWindow(hWnd)) _mergeFailedWindows.TryRemove(hWnd, out _);
    }

    /// <summary>Remember that the user opened <paramref name="hWnd"/> as a separate window on purpose.</summary>
    private void KeepAsWindow(nint hWnd)
    {
        // Only an ADDITIONAL window is "separate on purpose"; the only window is simply the window new tabs go to.
        if (hWnd != 0 && Helper.GetAllExplorerWindows().Any(h => h != hWnd))
            _keepAsWindow[hWnd] = 0;
    }

    private void PreventWindowHiding(nint hWnd)
    {
        if (_processedHWnds.TryAdd(hWnd, 0))
        {
            // Schedule removal after a short delay
            _ = Task.Delay(7_000).ContinueWith(t => _processedHWnds.TryRemove(hWnd, out _), TaskScheduler.Default);
        }
    }
    private void OnWindowShown(nint hWinEventHook, uint eventType, nint hWnd, int idObject, int idChild, uint dwEventThread, uint dWmsEventTime)
    {
        if (!_isForcingTabs || idObject != 0 || idChild != 0) return;
        
        // Check if the hWnd was processed by OnShellWindowRegistered
        if (_processedHWnds.TryRemove(hWnd, out _)) return;
        
        if (!WinApi.IsWindowHasClassName(hWnd, "CabinetWClass")) return;

        if (_windowEntryDict.Count < 2 || Helper.IsCtrlShiftDown()) return;
        Helper.HideWindow(hWnd, SettingsManager.HaveThemeIssue);
    }
    /// <summary>
    /// The entry <paramref name="index"/> of ShellWindows if it is a real Explorer tab (it has a window), else null.
    /// ShellWindows can contain broken "ghost" entries (empty name/location, HWND 0) whose members fail with E_NOTIMPL.
    /// </summary>
    private InternetExplorer? GetValidShellWindow(int index)
    {
        try
        {
            if (_shellWindows.Item(index) is not InternetExplorer window) return null;
            return window.HWND != 0 ? window : null;
        }
        catch
        {
            return null; // closed meanwhile, or a broken entry
        }
    }

    private bool IsSeenBefore(InternetExplorer window)
    {
        if (_seenShellWindows.TryGetValue(window, out _)) return true;
        try { return window.GetProperty("seenBefore") is not null; }
        catch { return false; } // not implemented by this entry: rely on the app's own list
    }

    private void MarkSeenBefore(InternetExplorer window)
    {
        try { _seenShellWindows.Add(window, true); }
        catch (ArgumentException) { /* already marked */ }

        // The property survives our COM wrapper being released (the app's own list doesn't), so set it when possible.
        try { window.PutProperty("seenBefore", true); }
        catch { /* E_NOTIMPL on some entries */ }
    }

    private InternetExplorer? GetRecentlyCreatedWindow(out WindowInfo? windowInfo)
    {
        // When a new window is registered, it's typically the last in the collection
        var count = _shellWindows.Count;
        for (var i = count - 1; i >= 0; i--)
        {
            // One broken entry (e.g. a "ghost" entry without a window) must never stop the others from being handled.
            if (GetValidShellWindow(i) is not { } window) continue;

            lock (_windowEntryDictLock)
            {
                if (_windowEntryDict.Keys.Contains(window)) continue;
                if (IsSeenBefore(window)) continue;
                MarkSeenBefore(window);

                windowInfo = new WindowInfo();
                _windowEntryDict.Add(window, windowInfo);

                if (_windowEntryDict.Count == 1)
                {
                    _mainWindowHandle = new IntPtr(window.HWND);

                    // "Restore last session's tabs" replaces the old prompt while it is enabled.
                    if (SettingsManager.RestorePreviousWindows && !_autoRestoreEnabled && !_isRestoring && _closedWindows.Any(w => w.Restore))
                        _ = RestorePreviousWindows();
                }
                
                return window;
            }
        }

        windowInfo = null;
        return null;
    }
    private async void OnShellWindowRegistered(int __)
    {
        var showAgain = true;
        nint hWnd = 0;
        _lastWindowRegisteredAt = Stopwatch.GetTimestamp();
        try
        {
            var shouldOpenAsWindow = Helper.IsCtrlShiftDown();

            WindowInfo windowInfo = null!;
            var window = await Helper.DoUntilNotDefaultAsync(() => GetRecentlyCreatedWindow(out windowInfo!), 2_500, 70);
            if (window == null) return;

            _ = GetTabHandle(window);

            hWnd = new IntPtr(window.HWND);
            
            if (shouldOpenAsWindow)
            {
                // Opened as a new window on purpose (Ctrl+Shift): automatic merging must leave it alone.
                if (Helper.GetAllExplorerTabs(hWnd).Take(2).Count() == 1) KeepAsWindow(hWnd);
                PreventWindowHiding(hWnd);
                HookWindowEvents(window, windowInfo);
                return;
            }
            
            var location = GetLocation(window);

            //Control Panel
            if (location.StartsWith("shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}"))
            {
                PreventWindowHiding(hWnd);
                RemoveWindowAndUnhookEvents(window, windowInfo);
                return;
            }

            // Check if this is a single tab window and there are other windows
            var isNewWindow = Helper.GetAllExplorerTabs(hWnd).Take(2).Count() == 1;
            var shouldReopenAsTab = (_isForcingTabs || _reuseTabs) &&
                                    _windowEntryDict.Count > 1 &&
                                    hWnd != _mainWindowHandle &&
                                    isNewWindow;

            // A new window while the window hook is off is a window the user wants: automatic merging leaves it alone.
            if (isNewWindow && !_isForcingTabs && !_reuseTabs && _windowEntryDict.Count > 1)
                KeepAsWindow(hWnd);

            if (shouldReopenAsTab)
                Helper.HideWindow(hWnd, SettingsManager.HaveThemeIssue);
            else
                PreventWindowHiding(hWnd);

            // Check if it is a detached tab
            var isRecentlyClosed = TryGetRecentlyClosedWindow(location, out var closedWindow);
            if (isRecentlyClosed)
                SelectItems(window, closedWindow!.SelectedItems);

            shouldReopenAsTab = shouldReopenAsTab && !isRecentlyClosed;

            // A detached tab or a location this app opened "as window": a separate window on purpose.
            if (isRecentlyClosed && isNewWindow)
                KeepAsWindow(hWnd);

            if (shouldReopenAsTab)
            {
                showAgain = false;
                // Read the item(s) the other app asked to show BEFORE this window is closed (see CaptureSelectedItemsAsync).
                var windowRecord = new WindowRecord(location, hWnd, await CaptureSelectedItemsAsync(window));

                // Duplicate protection (opening a folder from another app could end up as two identical tabs):
                // 1) The same folder was already converted moments ago (duplicate request / duplicate registration).
                // 2) The folder was already opened as a new tab moments ago (e.g. by Explorer itself, Windows 11
                //    "Open desktop folders and external folder links in new tab").
                // In both cases just close this window and switch to the tab that already exists.
                var isRepeatedRequest = !TryRegisterConversion(location);
                var existingTab = FindRecentTabWithLocation(location, exclude: window);
                if (isRepeatedRequest || existingTab != 0)
                {
                    window.Quit();
                    RemoveWindowAndUnhookEvents(window, windowInfo);
                    if (existingTab != 0)
                        await ActivateTabAsync(existingTab, windowRecord.SelectedItems);
                    return;
                }

                // Close the new window FIRST and only open the replacement tab once it is really gone.
                // If the window's tab survived because Explorer already moved it into an existing window
                // (Windows 11 can do that by itself), keep that tab instead of opening a second identical one.
                var hostWindow = await CloseWindowAsync(window, hWnd);
                if (hostWindow != 0)
                {
                    showAgain = true; // Restore the (now empty) original window if it still exists.
                    HookWindowEvents(window, windowInfo);

                    // Make sure the moved tab is the visible one, its window is in front and the requested item is selected.
                    var movedTab = await GetTabHandle(window);
                    if (movedTab != 0)
                        await ActivateTabAsync(movedTab, windowRecord.SelectedItems);
                    else
                        SelectItems(window, windowRecord.SelectedItems);
                    return;
                }

                RemoveWindowAndUnhookEvents(window, windowInfo);
                _ = OpenTabNavigateWithSelection(windowRecord, _mainWindowHandle, closeIfDuplicated: true);
                return;
            }

            // OnQuit might fire after ShellWindowRegistered in case of reattached tab (and there were selected files)
            if (!isRecentlyClosed)
            {
                isRecentlyClosed = await Helper.DoUntilNotDefaultAsync(() => TryGetRecentlyClosedWindow(location, out closedWindow), 700, 50);
                if (isRecentlyClosed)
                    SelectItems(window, closedWindow!.SelectedItems);
                if (isRecentlyClosed && isNewWindow)
                    KeepAsWindow(hWnd);
            }

            HookWindowEvents(window, windowInfo);
        }
        catch {/**/}
        finally
        {
            if (showAgain)
            {
                await Helper.DoUntilNotDefaultAsync(() => Helper.ShowWindow(hWnd, removeCache: false), 1_500, 200);

                if (!SettingsManager.HaveThemeIssue)
                    Helper.UpdateWindowLayered(hWnd, remove: true);

                // OnWindowShown might fire after ShellWindowRegistered and hide it again, keep the cache, wait a bit, then remove it.
                _ = Task.Delay(3000).ContinueWith(t => Helper.HiddenWindows.TryRemove(hWnd, out _), TaskScheduler.Default);
            }
        }
    }
    private void HookWindowEvents(InternetExplorer window, WindowInfo windowInfo)
    {
        // Create strongly-typed handlers so we can remove them later
        windowInfo.OnQuitHandler = () =>
        {
            var location = windowInfo.Location ?? GetLocation(window);
            var locationName = windowInfo.Name ?? window.LocationName;
            var windowRecord = new WindowRecord(location, new IntPtr(window.HWND), name: locationName);
            lock (_closedWindowsLock) _closedWindows.Add(windowRecord);

            // Home, This PC, etc
            if (location == _defaultLocation)
            {
                RemoveWindowAndUnhookEvents(window, windowInfo);
                return;
            }

            windowRecord.SelectedItems = GetSelectedItems(window);
            RemoveWindowAndUnhookEvents(window, windowInfo);
        };

        if (SettingsManager.RestorePreviousWindows)
            windowInfo.OnNavigateHandler = (object _, ref object _) =>
            {
                windowInfo.Location = GetLocation(window);
                windowInfo.Name = window.LocationName;
            };

        try
        {
            // Subscribe
            window.OnQuit += windowInfo.OnQuitHandler;

            // Every navigation creates a new view object, so (re)subscribe to its selection changes each time.
            windowInfo.ViewNavigateHandler = (object _, ref object _) =>
            {
                windowInfo.LastNavigatedAt = Stopwatch.GetTimestamp();
                windowInfo.LastSelection = null;
                HookViewEvents(window, windowInfo);
            };
            window.NavigateComplete2 += windowInfo.ViewNavigateHandler;
            HookViewEvents(window, windowInfo);
            if (SettingsManager.RestorePreviousWindows)
            {
                windowInfo.Location = GetLocation(window);
                windowInfo.Name = window.LocationName;
                window.NavigateComplete2 += windowInfo.OnNavigateHandler;
            }

            // Make sure the window is still alive (User might have closed it immediately after opening it)
            _ = window.HWND;
        }
        catch
        {
            UnhookViewEvents(windowInfo);
            lock (_windowEntryDictLock)
                _windowEntryDict.Remove(window);
        }
    }
    private void RemoveWindowAndUnhookEvents(InternetExplorer window, WindowInfo windowInfo, bool useLock = true)
    {
        // Unsubscribe
        UnhookWindowViewEvents(window, windowInfo);
        if (windowInfo.OnQuitHandler != null) window.OnQuit -= windowInfo.OnQuitHandler;
        if (windowInfo.OnNavigateHandler != null) window.NavigateComplete2 -= windowInfo.OnNavigateHandler;

        // Remove from dictionary
        if (useLock)
        {
            lock (_windowEntryDictLock)
                _windowEntryDict.Remove(window);
        }
        else
            _windowEntryDict.Remove(window);

        // Finally, release the COM reference for this InternetExplorer instance
        Marshal.ReleaseComObject(window);
    }

    private async Task RestorePreviousWindows()
    {
        var result = await RunInStaThread(() => CustomMessageBox.Show(
            Loc.Get("Msg_RestorePreviousWindows"),
            Loc.Get("App_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question));

        foreach (var record in _closedWindows.Where(record => record.Restore))
        {
            record.Restore = false;
            
            if (result != MessageBoxResult.Yes) continue;
            
            _ = OpenTabNavigateWithSelection(record);
        }
    }
    private async Task OpenNewWindowWithSelection(WindowRecord windowToOpen, bool duplicate = true, bool lockToOpenWindows = true)
    {
        if (lockToOpenWindows)
            await _toOpenWindowsLock.WaitAsync();

        try
        {
            lock (_closedWindowsLock)
                _closedWindows.Add(windowToOpen);

            var hasSelection = windowToOpen.SelectedItems?.Length > 0;

            nint[]? currentWindows = null;
            if (hasSelection)
                currentWindows = Helper.GetAllExplorerWindows().ToArray();

            Helper.BypassWinForegroundRestrictions();

            var location = string.IsNullOrWhiteSpace(windowToOpen.Location) ? _defaultLocation : windowToOpen.Location;
            await RunInStaThread(() =>
            {
                Shell? shell = null;
                try
                {
                    shell = new Shell();
                    shell.ShellExecute(location, "", "", duplicate ? "opennewwindow" : "open");
                }
                finally
                {
                    if (shell != null)
                        Marshal.ReleaseComObject(shell);
                }
            });

            if (!hasSelection) return;

            var newWindowHandle = await Helper.ListenForNewExplorerWindowAsync(currentWindows ?? []);
            if (newWindowHandle == 0) return;

            var window = _windowEntryDict.Keys.FirstOrDefault(w => w.HWND == newWindowHandle);
            if (window == null) return;

            SelectItems(window, windowToOpen.SelectedItems);
        }
        finally
        {
            if (lockToOpenWindows)
                _toOpenWindowsLock.Release();
        }
    }
    private async Task OpenTabNavigateWithSelection(WindowRecord windowToOpen, nint windowHandle = 0, bool isDuplicate = false, bool forceTabReuse = false,
        bool closeIfDuplicated = false)
    {
        InternetExplorer? createdWindow = null;
        nint createdTabHandle = 0, createdInWindow = 0;

        await _toOpenWindowsLock.WaitAsync();
        try
        {
            if ((_reuseTabs || forceTabReuse) && !isDuplicate && _windowEntryDict.Count > 0)
            {
                var existingTab = SearchForTab(windowToOpen.Location);
                if (existingTab != 0)
                {
                    // Switch to the existing tab, bring its window to the front and select the requested item(s)
                    // (previously the item the other app asked to show was not selected when a tab was reused).
                    await ActivateTabAsync(existingTab, windowToOpen.SelectedItems);
                    return;
                }
            }

            // Get the main window
            var mainWindowHWnd = Helper.IsFileExplorerWindow(windowHandle)
                ? windowHandle
                : GetMainWindowHWnd(windowToOpen.Handle);

            if (mainWindowHWnd == 0)
            {
                await OpenNewWindowWithSelection(windowToOpen, lockToOpenWindows: false);
                return;
            }

            // Store the current tabs
            var currentTabs = Helper.GetAllExplorerTabs(mainWindowHWnd).ToArray();

            // Request to open a new tab
            await RequestToOpenNewTab(mainWindowHWnd, lockToOpenWindows: false);

            // Wait for the new tab
            var newTabHandle = await Helper.ListenForNewExplorerTabAsync(mainWindowHWnd, currentTabs, 2_000);
            if (newTabHandle == 0) return;

            // Get the window object
            var window = await Helper.DoUntilNotDefaultAsync(() => GetWindowByTabHandle(newTabHandle), 2_000, 50);
            if (window == null) return;

            var tcs = new TaskCompletionSource<bool>();
            DWebBrowserEvents2_NavigateComplete2EventHandler navigateHandler = null!;
            navigateHandler = (object _, ref object _) =>
            {
                window.NavigateComplete2 -= navigateHandler;
                tcs.TrySetResult(true);
                SelectItems(window, windowToOpen.SelectedItems);
            };

            window.NavigateComplete2 += navigateHandler;
            try
            {
                await Navigate(window, windowToOpen.Location);
            }
            catch
            {
                window.NavigateComplete2 -= navigateHandler;
                tcs.TrySetResult(false);
            }

            WinApi.RestoreWindowToForeground(mainWindowHWnd);

            var timeoutTask = Task.Delay(5000);
            await Task.WhenAny(tcs.Task, timeoutTask);

            createdWindow = window;
            createdTabHandle = newTabHandle;
            createdInWindow = mainWindowHWnd;
        }
        finally
        {
            _toOpenWindowsLock.Release();
        }

        // A converted window's folder may also show up as a separate new tab created by Explorer itself
        // a moment later. If that happens, close OUR tab (never one we didn't create) and keep Explorer's.
        if (closeIfDuplicated && createdWindow != null)
            await CloseOwnTabIfDuplicatedAsync(windowToOpen, createdWindow, createdTabHandle, createdInWindow);
    }

    /// <summary>
    /// Remembers that <paramref name="location"/> is being converted from a new window into a tab.
    /// Returns false if the same location was already converted within the last <see cref="DuplicateRequestWindowMs"/>.
    /// </summary>
    private bool TryRegisterConversion(string location)
    {
        lock (_recentConversionsLock)
        {
            var now = Environment.TickCount;
            _recentConversions.RemoveAll(c => now - c.Tick > DuplicateRequestWindowMs);

            if (_recentConversions.Any(c => IsSameLocation(c.Location, location)))
                return false;

            _recentConversions.Add((location, now));
            return true;
        }
    }

    private bool IsSameLocation(string location1, string location2)
    {
        if (string.Equals(location1, location2, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return _shellPathComparer.IsEquivalent(location1, location2);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Finds a tab (other than <paramref name="exclude"/>) that was opened within the last <see cref="RecentTabMaxAgeMs"/>
    /// and shows <paramref name="location"/>. Returns its tab handle, or 0.
    /// </summary>
    private nint FindRecentTabWithLocation(string location, InternetExplorer? exclude)
    {
        if (string.IsNullOrWhiteSpace(location) || location == _defaultLocation) return 0;

        List<(InternetExplorer Window, WindowInfo Info, nint? TabHandle)> candidates;
        lock (_windowEntryDictLock)
        {
            candidates = ((IEnumerable<WindowEntry>)_windowEntryDict)
                .Where(e => !ReferenceEquals(e.PrimaryKey, exclude) && !Helper.IsTimeUp(e.Value.CreatedAt, RecentTabMaxAgeMs))
                .Select(e => (e.PrimaryKey, e.Value, e.OptionalKey))
                .ToList();
        }

        foreach (var (window, info, tabHandle) in candidates)
        {
            if (tabHandle is not { } tab || tab == 0) continue;
            try
            {
                if (IsSameLocation(location, info.Location ?? GetLocation(window)))
                    return tab;
            }
            catch
            {
                // The tab might have been closed meanwhile.
            }
        }

        return 0;
    }

    /// <summary>
    /// Closes the window of <paramref name="window"/> and waits until it is gone.
    /// Returns 0 when it closed, or the handle of the Explorer window that now hosts it
    /// (Explorer moved the tab into another window before it could be closed).
    /// </summary>
    private static async Task<nint> CloseWindowAsync(InternetExplorer window, nint originalHWnd, int timeoutMs = 1_000)
    {
        try
        {
            var host = new IntPtr(window.HWND);
            if (host != originalHWnd && Helper.IsFileExplorerWindow(host))
                return host; // Already moved, don't close it.

            window.Quit();
        }
        catch
        {
            return 0; // Already gone.
        }

        var startTicks = Stopwatch.GetTimestamp();
        while (!Helper.IsTimeUp(startTicks, timeoutMs))
        {
            await Task.Delay(30);
            try
            {
                var host = new IntPtr(window.HWND);
                if (host != originalHWnd && Helper.IsFileExplorerWindow(host))
                    return host;
            }
            catch
            {
                return 0; // COM object disconnected => the window was closed.
            }
        }

        return 0; // Still there after the timeout: keep the previous behavior (treat it as closed).
    }

    private async Task ActivateTabAsync(nint tabHandle, string[]? selectedItems)
    {
        var hostWindow = WinApi.GetParent(tabHandle);
        if (!Helper.IsFileExplorerWindow(hostWindow)) return;

        await SelectTabByHandle(hostWindow, tabHandle);
        WinApi.RestoreWindowToForeground(hostWindow);

        var window = GetWindowByTabHandle(tabHandle);
        if (window != null)
        {
            try { SelectItems(window, selectedItems); }
            catch { /* ignored */ }
        }
    }

    private async Task CloseOwnTabIfDuplicatedAsync(WindowRecord windowToOpen, InternetExplorer ownWindow, nint ownTabHandle, nint hostWindow)
    {
        // Give Explorer a short moment to register its own tab (if it creates one at all).
        var startTicks = Stopwatch.GetTimestamp();
        while (!Helper.IsTimeUp(startTicks, 1_500))
        {
            var otherTab = FindRecentTabWithLocation(windowToOpen.Location, exclude: ownWindow);
            if (otherTab != 0 && otherTab != ownTabHandle)
            {
                // Only close our tab if it is still the active one, so we never close something the user switched to.
                if (GetActiveTabHandle(hostWindow) == ownTabHandle)
                {
                    // Send 0xA021 magic command (CTRL + W)
                    WinApi.SendMessage(ownTabHandle, WinApi.WM_COMMAND, 0xA021, 1);
                    await ActivateTabAsync(otherTab, windowToOpen.SelectedItems);
                }
                return;
            }

            await Task.Delay(100);
        }
    }
    /// <summary>
    /// Reads the selection of a window that is about to be converted into a tab.
    /// When another app asks Explorer to show a file ("explorer /select,&lt;file&gt;", SHOpenFolderAndSelectItems),
    /// Explorer selects it only after the folder finished loading, which is usually AFTER the window was registered.
    /// So wait (bounded) for the folder to load and give the selection a short moment to appear.
    /// </summary>
    private static async Task<string[]?> CaptureSelectedItemsAsync(InternetExplorer window)
    {
        try
        {
            await Helper.DoUntilConditionAsync(() => window.ReadyState, s => s == tagREADYSTATE.READYSTATE_COMPLETE, 1_000, 30);
        }
        catch
        {
            // Window already gone; read whatever we can below.
        }

        var startTicks = Stopwatch.GetTimestamp();
        while (true)
        {
            string[]? selection = null;
            try { selection = GetSelectedItems(window); }
            catch { /* view not ready yet */ }

            if (selection != null || Helper.IsTimeUp(startTicks, 150)) return selection;
            await Task.Delay(30);
        }
    }

    // ---- Explorer showed an item in an existing tab that is not the active one ----
    // When another app asks Explorer to show a folder/file ("Open file location", "Show in folder", "explorer /select,<file>",
    // SHOpenFolderAndSelectItems) and that folder is ALREADY open in some tab, Explorer does not open a new window.
    // It selects the item(s) in that existing tab and brings its window to the front, but it never switches to that tab,
    // so the user keeps looking at the previously active tab (see w4po/ExplorerTabUtility#126 and #128).
    // Because no new window is registered, the "window to tab" logic never sees this. Instead we watch selection changes:
    // the user cannot change the selection of a tab that is not visible, so a selection change in an inactive tab,
    // followed by its window coming to the foreground, means "show this tab".
    private void HookViewEvents(InternetExplorer window, WindowInfo windowInfo)
    {
        UnhookViewEvents(windowInfo);
        try
        {
            if (window.Document is not ShellFolderView view) return;

            windowInfo.SelectionChangedHandler ??= () => OnViewSelectionChanged(window, windowInfo);
            ((DShellFolderViewEvents_Event)view).SelectionChanged += windowInfo.SelectionChangedHandler;
            windowInfo.View = view;
        }
        catch
        {
            // The view is not ready yet (we hook again on NavigateComplete2) or the window was closed.
        }
    }
    private static void UnhookViewEvents(WindowInfo windowInfo)
    {
        var view = windowInfo.View;
        windowInfo.View = null;
        if (view == null || windowInfo.SelectionChangedHandler == null) return;

        try { ((DShellFolderViewEvents_Event)view).SelectionChanged -= windowInfo.SelectionChangedHandler; }
        catch { /* The old view is already gone */ }
    }
    private static void UnhookWindowViewEvents(InternetExplorer window, WindowInfo windowInfo)
    {
        UnhookViewEvents(windowInfo);
        if (windowInfo.ViewNavigateHandler == null) return;

        try { window.NavigateComplete2 -= windowInfo.ViewNavigateHandler; }
        catch { /* The window is already gone */ }
    }
    private void OnViewSelectionChanged(InternetExplorer window, WindowInfo windowInfo)
    {
        try
        {
            if (!_isForcingTabs && !_reuseTabs) return;

            // Tabs created by a merge load their folders in the background: that is not "Explorer showed an item".
            if (_isMerging || _isRestoring || !Helper.IsTimeUp(_lastMergeFinishedAt, 2_000)) return;

            // Ignore our own selection changes and the initial selection of a new tab / a folder that was just navigated to.
            if (!Helper.IsTimeUp(_lastOwnSelectionTicks, 700) ||
                !Helper.IsTimeUp(windowInfo.CreatedAt, 2_000) ||
                !Helper.IsTimeUp(windowInfo.LastNavigatedAt, 1_000))
                return;

            if (!_windowEntryDict.TryGetValue(window, out WindowEntry entry) || entry.OptionalKey is not { } tabHandle || tabHandle == 0)
                return;

            var hostWindow = WinApi.GetParent(tabHandle);
            if (!Helper.IsFileExplorerWindow(hostWindow)) return;

            // The visible tab: a normal selection change by the user. Nothing to do (and don't read big selections here).
            if (GetActiveTabHandle(hostWindow) == tabHandle)
            {
                windowInfo.LastSelection = null;
                return;
            }

            if (Interlocked.Exchange(ref windowInfo.RevealCheckPending, 1) == 1) return;
            _ = SwitchToRevealedTabAsync(window, windowInfo, tabHandle, hostWindow);
        }
        catch
        {
            // Never let an event handler throw back into Explorer.
        }
    }
    private async Task SwitchToRevealedTabAsync(InternetExplorer window, WindowInfo windowInfo, nint tabHandle, nint hostWindow)
    {
        try
        {
            // Explorer changes the selection in several steps (deselect others, select, focus); let it settle.
            await Task.Delay(150);
            if (GetActiveTabHandle(hostWindow) == tabHandle) return;

            var selection = GetSelectedItems(window);
            var previous = windowInfo.LastSelection;
            windowInfo.LastSelection = selection;
            if (selection == null) return;

            // Items only disappeared from the selection (deleted / moved away): not a request to show something.
            if (previous != null && selection.Length < previous.Length &&
                !selection.Except(previous, StringComparer.OrdinalIgnoreCase).Any())
                return;

            // Explorer brings the window to the front right after selecting the item(s). If that doesn't happen,
            // it was not a "show this" request (e.g. a background tab refreshed its content), so leave it alone.
            var isForeground = await Helper.DoUntilConditionAsync(() => WinApi.GetForegroundWindow() == hostWindow, b => b, 1_500, 50);
            if (!isForeground || GetActiveTabHandle(hostWindow) == tabHandle) return;

            await ActivateTabAsync(tabHandle, selection);
        }
        catch
        {
            // The tab might have been closed meanwhile.
        }
        finally
        {
            Interlocked.Exchange(ref windowInfo.RevealCheckPending, 0);
        }
    }

    private bool TryGetRecentlyClosedWindow(string location, out WindowRecord? closedWindow, int maxAge = 2_000)
    {
        nint targetPidl = 0;
        try
        {
            targetPidl = _shellPathComparer.GetPidlFromPath(location);
            lock (_closedWindowsLock)
            {
                for (var i = _closedWindows.Count - 1; i >= 0; i--)
                {
                    var record = _closedWindows[i];
                    if (Environment.TickCount - record.CreatedAt > maxAge) break;
                    if (!_shellPathComparer.IsEquivalent(location, record.Location, targetPidl)) continue;
                    _closedWindows.RemoveAt(i);
                    closedWindow = record;
                    return true;
                }
            }
            closedWindow = null;
            return false;
        }
        finally
        {
            if (targetPidl != 0)
                Marshal.FreeCoTaskMem(targetPidl);
        }
    }
    private nint GetMainWindowHWnd(nint otherThan)
    {
        if (Helper.IsFileExplorerWindow(_mainWindowHandle))
            return _mainWindowHandle;

        var allWindows = WinApi.FindAllWindowsEx("CabinetWClass");

        // Get another handle other than the newly created one. (In case if it is still alive.)
        _mainWindowHandle = allWindows
            .Where(h => h != otherThan)
            .Reverse() // To get the last one in the z-index (the oldest)
            .OrderByDescending(h => WinApi.FindAllWindowsEx("ShellTabWindowClass", h).Count()) // The one with the most tabs first
            .FirstOrDefault();

        if (_mainWindowHandle != 0) return _mainWindowHandle;

        return Helper.IsFileExplorerWindow(otherThan) ? otherThan : 0;
    }
    private Task<nint> GetTabHandle(InternetExplorer window)
    {
        if (_windowEntryDict.TryGetValue(window, out WindowEntry entry) && entry.OptionalKey is { } handle and > 0)
            return Task.FromResult(handle);

        // Schedule the operation on STA
        return RunInStaThread(() =>
        {
            // ReSharper disable once SuspiciousTypeConversion.Global
            if (window is not Interop.IServiceProvider sp) return 0;

            sp.QueryService(ref _shellBrowserGuid, ref _shellBrowserGuid, out var shellBrowser);
            if (shellBrowser == null) return 0;

            try
            {
                shellBrowser.GetWindow(out var hWnd);

                if (hWnd != 0)
                    _windowEntryDict.UpdateOptionalKey(window, hWnd);

                return hWnd;
            }
            finally
            {
                Marshal.ReleaseComObject(shellBrowser);
            }
        });
    }
    private static nint GetActiveTabHandle(nint windowHandle)
    {
        // Active tab always at the top of the z-index
        return WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
    }
    private InternetExplorer? GetWindowByTabHandle(nint tabHandle)
    {
        if (tabHandle == 0) return null;
        return _windowEntryDict.TryGetValue(tabHandle, out InternetExplorer? foundWindow) ? foundWindow : null;
    }
    private static string[]? GetSelectedItems(InternetExplorer window)
    {
        var selectedItems = (window.Document as ShellFolderView)!.SelectedItems();
        var count = selectedItems.Count;
        if (count == 0) return null;

        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = GetItemName(selectedItems.Item(i));
        }

        return result;
    }
    /// <summary>
    /// Name that <c>Folder.ParseName</c> can resolve again. <see cref="FolderItem.Name"/> is the display name
    /// (e.g. "report" instead of "report.pdf" when file extensions are hidden), so prefer the real file name.
    /// </summary>
    private static string GetItemName(FolderItem item)
    {
        try
        {
            var path = item.Path;
            if (!string.IsNullOrEmpty(path) && !path.StartsWith("::") && System.IO.Path.IsPathRooted(path))
            {
                var fileName = System.IO.Path.GetFileName(path.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(fileName)) return fileName;
            }
        }
        catch
        {
            // Fall back to the display name.
        }

        return item.Name;
    }
    private static void SelectItems(InternetExplorer window, string[]? names)
    {
        if (names == null || names.Length == 0) return;

        if (window.Document is not ShellFolderView document) return;

        // Our own selection changes must not be mistaken for "Explorer showed an item in this tab".
        _lastOwnSelectionTicks = Stopwatch.GetTimestamp();

        var isFirst = true;
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            object item = document.Folder.ParseName(name);
            if (item == null) continue;

            // Like "explorer /select": the first item replaces the selection, gets the focus and is scrolled into view.
            document.SelectItem(ref item, isFirst ? SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused : SvsiSelect);
            isFirst = false;
        }
    }
    private static string GetLocation(InternetExplorer window)
    {
        var path = window.LocationURL;
        if (!string.IsNullOrWhiteSpace(path)) return Helper.NormalizeLocation(path);

        // Recycle Bin, This PC, etc
        path = ((window.Document as ShellFolderView)!.Folder as Folder2)!.Self.Path;
        return Helper.NormalizeLocation(path);
    }
    private async Task Navigate(InternetExplorer window, string path)
    {
        if (!path.Contains("#") && !path.Contains("%23"))
        {
            window.Navigate2(path);
            return;
        }

        var folder = await RunInStaThread(() =>
        {
            Shell? shell = null;
            Folder? folder;
            try
            {
                shell = new Shell();
                folder = shell.NameSpace(path);
            }
            finally
            {
                if (shell != null)
                    Marshal.ReleaseComObject(shell);
            }
            return folder;
        });

        try
        {
            window.Navigate2(folder);
        }
        finally
        {
            if (folder != null)
                Marshal.ReleaseComObject(folder);
        }
    }
    private Task RunInStaThread(Action action, TaskCreationOptions tco = default, CancellationToken ct = default)
    {
        return Task.Factory.StartNew(action, ct, tco, _staTaskScheduler);
    }
    private Task<T?> RunInStaThread<T>(Func<T?> action, TaskCreationOptions tco = default, CancellationToken ct = default)
    {
        return Task.Factory.StartNew(action, ct, tco, _staTaskScheduler);
    }
    
    private void StartExplorerProcessCheck() => _explorerCheckTimer = new Timer(CheckForMainExplorer, null, 0, 1000);
    private void CheckForMainExplorer(object? state)
    {
        // Timer callback: an exception here would end the process, so nothing may escape.
        try
        {
            var process = Helper.GetMainExplorerProcess();
            if (process == null) return;

            _explorerCheckTimer?.Dispose();
            _explorerCheckTimer = null;

            lock (_processLock)
            {
                if (_mainExplorerProcessId != 0) return;

                _mainExplorerProcessId = process.Id;
                try
                {
                    InitializeShellObjects();
                }
                catch
                {
                    // Explorer is not ready (or restarting): start over in a moment instead of crashing.
                    _mainExplorerProcessId = 0;
                    try { DisposeShellObjects(); } catch { /* partly initialized */ }
                    _explorerCheckTimer = new Timer(CheckForMainExplorer, null, 3_000, 1_000);
                    return;
                }

                OnShellInitialized?.Invoke();
                OnShellReadyForSession();
            }
        }
        catch
        {
            // Keep running; the window hook simply isn't ready yet.
        }
    }
    private void OnExplorerProcessTerminated(object? s, ProcessEventArgs e)
    {
        // Main explorer.exe process (_shellWindows must be restarted)
        lock (_processLock)
        {
            if (e.ProcessId == _mainExplorerProcessId)
            {
                OnExplorerTerminatedForSession();
                _mainExplorerProcessId = 0;
                DisposeShellObjects();
                StartExplorerProcessCheck();
                return;
            }
        }
        
        // Other explorer.exe processes
        lock (_windowEntryDictLock)
        {
            if (_windowEntryDict.Count == 0) return;
            var crashCount = 0;
            for (var i = _windowEntryDict.Count - 1; i >= 0; i--)
            {
                var (window, info) = _windowEntryDict.ElementAt<WindowEntry>(i);
                try
                {
                    _ = window.HWND;
                }
                catch
                {
                    if (info.OnNavigateHandler != null)
                    {
                        crashCount++;
                        lock (_closedWindowsLock)
                            _closedWindows.Add(new WindowRecord(info.Location!, name: info.Name!));
                    }
                    
                    RemoveWindowAndUnhookEvents(window, info, useLock: false);
                }
            }
            if (!SettingsManager.RestorePreviousWindows || _windowEntryDict.Count > 0) return;
            lock (_closedWindowsLock)
            {
                for (var i = 1; i <= crashCount; i++)
                    _closedWindows[_closedWindows.Count - i].Restore = true;
            }
        }
    }

    private void InitializeShellObjects()
    {
        _shellPathComparer = new ShellPathComparer();
        _staTaskScheduler = new StaTaskScheduler();
        _shellWindows = new ShellWindows();

        _defaultLocation = Helper.GetDefaultExplorerLocation(_shellPathComparer);
        
        if (SettingsManager.ClosedWindows != null)
            lock (_closedWindowsLock) _closedWindows.AddRange(SettingsManager.ClosedWindows);

        // Hook the global "WindowRegistered" event
        _windowRegisteredHandler = OnShellWindowRegistered;
        _shellWindows.WindowRegistered += _windowRegisteredHandler;

        // Hook the global "OBJECT_SHOW" event
        _eventObjectShowHookCallback = OnWindowShown;
        _eventObjectShowHookId = WinApi.SetWinEventHook(WinApi.EVENT_OBJECT_SHOW, WinApi.EVENT_OBJECT_SHOW, 0, _eventObjectShowHookCallback, 0, 0, 0);

        // Hook the event handlers for already-open windows
        var hasOpen = false;
        var count = _shellWindows.Count;
        for (var i = 0; i < count; i++)
        {
            // One broken entry (e.g. a "ghost" entry without a window) must never stop the app from starting.
            if (GetValidShellWindow(i) is not { } window)
                continue;

            var windowInfo = new WindowInfo();
            try
            {
                _windowEntryDict.Add(window, windowInfo);
                MarkSeenBefore(window);

                _ = GetTabHandle(window);
                HookWindowEvents(window, windowInfo);
                hasOpen = true;
            }
            catch
            {
                try { _windowEntryDict.Remove(window); } catch { /* not added */ }
            }
        }

        if (!hasOpen) return;
        lock (_closedWindowsLock)
            foreach (var window in _closedWindows) window.Restore = false;
    }
    private void DisposeShellObjects()
    {
        PersistWindows();

        // Unhook global event
        if (_windowRegisteredHandler != null)
        {
            _shellWindows.WindowRegistered -= _windowRegisteredHandler;
            _windowRegisteredHandler = null;
        }
        if (_eventObjectShowHookCallback != null)
        {
            WinApi.UnhookWinEvent(_eventObjectShowHookId);
            _eventObjectShowHookCallback = null;
        }

        // Unsubscribe from each InternetExplorer instance's events
        foreach (var (window, windowInfo) in _windowEntryDict)
        {
            // Unsubscribe
            UnhookWindowViewEvents(window, windowInfo);
            if (windowInfo.OnQuitHandler != null) window.OnQuit -= windowInfo.OnQuitHandler;
            if (windowInfo.OnNavigateHandler != null) window.NavigateComplete2 -= windowInfo.OnNavigateHandler;

            // Release the COM object
            Marshal.ReleaseComObject(window);
        }
        _windowEntryDict.Clear();

        // Release the ShellWindows COM object
        Marshal.ReleaseComObject(_shellWindows);

        _shellPathComparer.Dispose();
        _staTaskScheduler.Dispose();
    }

    private void PersistWindows()
    {
        var store = new List<WindowRecord>();
        lock (_closedWindowsLock)
        {
            if (SettingsManager.SaveClosedHistory) store.AddRange(_closedWindows);
            _closedWindows.Clear();
        }

        // Save currently open windows (explorer crash / system restart, logoff / AppExit)
        if (SettingsManager.RestorePreviousWindows)
            lock (_windowEntryDictLock)
            {
                store.AddRange(_windowEntryDict.Values
                    .Where(w => w.OnNavigateHandler != null)
                    .Select(w => new WindowRecord(w.Location!, name: w.Name!, restore: true)));
            }
        
        // DistinctBy location
        var distinctItems = store
            .GroupBy(w => w.Location)
            .Select(g => g.Last())
            .ToArray();
        
        // TakeLast 100
        SettingsManager.ClosedWindows = distinctItems.Skip(Math.Max(0, distinctItems.Length - 100)).ToArray();
    }

    public void Dispose()
    {
        SetAutoMergeWindows(false);
        ShutdownSession();
        DisposeShellObjects();
        _instanceRunning = false;
        _processWatcher.Dispose();
        GC.SuppressFinalize(this);
    }
}