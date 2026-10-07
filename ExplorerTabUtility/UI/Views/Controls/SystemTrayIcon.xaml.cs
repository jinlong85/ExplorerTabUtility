using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Collections.Generic;
using ExplorerTabUtility.Hooks;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Localization;
using ExplorerTabUtility.UI.Commands;

namespace ExplorerTabUtility.UI.Views.Controls;

// ReSharper disable once RedundantExtendsListEntry
public partial class SystemTrayIcon : UserControl, IDisposable
{
    private readonly ProfileManager _profileManager;
    private readonly HookManager _hookManager;
    private readonly Action _showWindowAction;
    private ICommand ProfileItemCommand { get; set; } = null!;
    private bool _savedReuseTabsState;

    public SystemTrayIcon(ProfileManager profileManager, HookManager hookManager, Action showWindowAction)
    {
        InitializeComponent();
        InitializeCommands();

        TrayIcon.Icon = Helper.GetIcon();
        TrayIcon.ToolTipText = Loc.Get("App_NotifyIconText");

        _profileManager = profileManager;
        _hookManager = hookManager;
        _showWindowAction = showWindowAction;

        _hookManager.OnShellInitialized += HookManager_OnShellInitialized;
        _hookManager.OnWindowHookToggled += HookManager_OnWindowHookToggled;
        _hookManager.OnReuseTabsToggled += HookManager_OnReuseTabsToggled;
        _hookManager.OnWindowsMerged += HookManager_OnWindowsMerged;
        _hookManager.OnSessionRestored += HookManager_OnSessionRestored;

        // Populate submenus for keyboard & mouse profiles
        UpdateMenuItems(autoCheckParent: false);

        // Keep "Add to startup" in sync with the registry (it can be changed from Preferences, Task Manager, ...)
        if (TrayIcon.ContextMenu != null)
            TrayIcon.ContextMenu.Opened += (_, _) => RefreshStartupMenuItem();
        RegistryManager.StartupChanged += _ => Dispatcher.BeginInvoke(new Action(RefreshStartupMenuItem));
    }

    private void InitializeCommands()
    {
        ProfileItemCommand = new RelayCommand(OnProfileItemClick, s => s != null && ((MenuItem)s).IsEnabled);

        KeyboardHookMenu.CommandParameter = KeyboardHookMenu;
        KeyboardHookMenu.Command = new RelayCommand(_ => ToggleKeyboardHookMenu(), s => s != null && ((MenuItem)s).HasItems);

        MouseHookMenu.CommandParameter = MouseHookMenu;
        MouseHookMenu.Command = new RelayCommand(_ => ToggleMouseHookMenu(), s => s != null && ((MenuItem)s).HasItems);

        WindowHook.Command = new RelayCommand(_ => ToggleWindowHook());
        ReuseTabs.Command = new RelayCommand(_ => ToggleReuseTabs());
        AddToStartup.Command = new RelayCommand(_ => ToggleStartup());
        MergeWindowsNow.Command = new RelayCommand(_ => _ = _hookManager.MergeWindowsNowAsync());
        RestoreSession.Command = new RelayCommand(_ => _ = _hookManager.RestoreSessionNowAsync());
        OpenSettings.Command = new RelayCommand(_ => _showWindowAction());
        CheckForUpdates.Command = new RelayCommand(_ => UpdateManager.CheckForUpdates());
        ExitApplication.Command = new RelayCommand(_ => Application.Current.Shutdown());
    }

    private void HookManager_OnShellInitialized()
    {
        // Workaround for hardcodet/wpf-notifyicon bug: repeatedly hide icon when explorer.exe restarts
        if (TrayIcon.Visibility == Visibility.Visible) return;
        Helper.DoUntilTimeEnd(HideTrayIcon, 7000, 1000);
        return;
        
        void HideTrayIcon()
        {
            TrayIcon.Dispatcher.BeginInvoke(() =>
            {
                if (TrayIcon.Visibility == Visibility.Visible) return;
                TrayIcon.Visibility = Visibility.Hidden;
                TrayIcon.Visibility = Visibility.Collapsed;
            });
        }
    }
    
    private void HookManager_OnWindowHookToggled()
    {
        if (WindowHook.IsChecked)
        {
            // Store the current ReuseTabs state before toggling WindowHook
            _savedReuseTabsState = SettingsManager.ReuseTabs;

            WindowHook.IsChecked = false;
            WindowHook.Command.Execute(WindowHook.CommandParameter);
            return;
        }
        
        if (_savedReuseTabsState)
        {
            // It will activate window hook too as well.
            ReuseTabs.IsChecked = true;
            ReuseTabs.Command.Execute(ReuseTabs.CommandParameter);
        }
        else
        {
            WindowHook.IsChecked = true;
            WindowHook.Command.Execute(WindowHook.CommandParameter);
        }
    }

    private void HookManager_OnWindowsMerged(MergeResult result)
    {
        if (result.FailedWindows > 0)
        {
            // Nothing is lost, but the user should know why some windows are still there.
            CustomMessageBox.Show(Loc.Format("Merge_Failed", result.FailedWindows), Loc.Get("App_Title"), icon: MessageBoxImage.Warning);
            return;
        }

        if (TrayIcon.Visibility != Visibility.Visible) return;

        var message = result.MergedWindows > 0
            ? Loc.Format("Merge_Done", result.MergedWindows, result.MovedTabs)
            : Loc.Get("Merge_Nothing");
        TrayIcon.ShowBalloonTip(Loc.Get("App_Title"), message, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
    }

    private void HookManager_OnSessionRestored(RestoreResult result)
    {
        if (TrayIcon.Visibility != Visibility.Visible) return;

        var parts = new List<string>();
        if (result.NoSession)
            parts.Add(Loc.Get("Restore_Nothing"));
        else if (result.OpenedTabs > 0)
            parts.Add(Loc.Format("Restore_Done", result.OpenedTabs));
        else if (result.AlreadyOpen > 0 && result.Missing.Count == 0 && result.Failed == 0)
            parts.Add(Loc.Get("Restore_AllOpen"));
        else if (result.Missing.Count == 0 && result.Failed == 0)
            parts.Add(Loc.Get("Restore_Nothing"));

        if (result.Missing.Count > 0)
            parts.Add(Loc.Format("Restore_Missing", result.Missing.Count, string.Join(", ", result.Missing.Take(3))));
        if (result.OverLimit > 0)
            parts.Add(Loc.Format("Restore_OverLimit", result.OverLimit, ExplorerWatcher.MaxRestoreTabs));
        if (result.Failed > 0)
            parts.Add(Loc.Format("Restore_Failed", result.Failed));

        TrayIcon.ShowBalloonTip(Loc.Get("App_Title"), string.Join(Environment.NewLine, parts), Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
    }

    private void HookManager_OnReuseTabsToggled()
    {
        ReuseTabs.IsChecked = !ReuseTabs.IsChecked;
        ReuseTabs.Command.Execute(ReuseTabs.CommandParameter);
    }

    public void UpdateMenuItems(bool autoCheckParent = true)
    {
        PopulateHookProfiles(KeyboardHookMenu, _profileManager.GetKeyboardProfiles(), autoCheckParent);
        PopulateHookProfiles(MouseHookMenu, _profileManager.GetMouseProfiles(), autoCheckParent);
    }

    public void SetTrayIconVisibility(bool visible) => TrayIcon.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    private void OnNotifyIconDoubleClick(object sender, RoutedEventArgs _) => _showWindowAction();

    private void ToggleKeyboardHookMenu()
    {
        KeyboardHookMenu.IsChecked = !KeyboardHookMenu.IsChecked;

        ToggleHookState(KeyboardHookMenu,
            v => SettingsManager.IsKeyboardHookActive = v,
            _hookManager.StartKeyboardHook,
            _hookManager.StopKeyboardHook
        );
    }

    private void ToggleMouseHookMenu()
    {
        MouseHookMenu.IsChecked = !MouseHookMenu.IsChecked;

        ToggleHookState(MouseHookMenu,
            v => SettingsManager.IsMouseHookActive = v,
            _hookManager.StartMouseHook,
            _hookManager.StopMouseHook
        );
    }

    private void ToggleWindowHook()
    {
        ToggleHookState(WindowHook,
            v => SettingsManager.IsWindowHookActive = v,
            _hookManager.StartWindowHook,
            _hookManager.StopWindowHook
        );

        if (!WindowHook.IsChecked && ReuseTabs.IsChecked)
        {
            ReuseTabs.IsChecked = false;
            ReuseTabs.Command.Execute(ReuseTabs.CommandParameter);
        }
    }

    private static void ToggleHookState(MenuItem parent, Action<bool> setSetting, Action startHook, Action stopHook)
    {
        setSetting(parent.IsChecked);
        (parent.IsChecked ? startHook : stopHook)();

        if (parent.Name?.EndsWith("Menu") != true) return;

        // Enable/disable dropdown menu based on current state
        foreach (MenuItem dropDownItem in parent.Items)
            dropDownItem.IsEnabled = parent.IsChecked;

        // If hook is enabled and no items are checked, tick the first one
        if (parent.IsChecked && !parent.Items.Cast<MenuItem>().Any(i => i.IsChecked))
        {
            var first = (MenuItem)parent.Items[0]!;
            first.IsChecked = !first.IsChecked;
            first.Command.Execute(first.CommandParameter);
        }

        // Force menu to close and reopen to refresh the UI state
        var isOpen = parent.IsSubmenuOpen;
        parent.IsSubmenuOpen = false;
        Application.Current.Dispatcher.InvokeAsync(() => { parent.IsSubmenuOpen = isOpen; },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void ToggleReuseTabs()
    {
        SettingsManager.ReuseTabs = ReuseTabs.IsChecked;
        _hookManager.SetReuseTabs(ReuseTabs.IsChecked);

        if (ReuseTabs.IsChecked && !WindowHook.IsChecked)
        {
            WindowHook.IsChecked = true;
            WindowHook.Command.Execute(WindowHook.CommandParameter);
        }
    }

    private void ToggleStartup()
    {
        try
        {
            RegistryManager.ToggleStartup();
        }
        catch (Exception ex)
        {
            CustomMessageBox.Show(Loc.Format("Msg_StartupChangeFailed", ex.Message), Loc.Get("App_Title"), icon: MessageBoxImage.Warning);
        }

        RefreshStartupMenuItem();
    }

    private void RefreshStartupMenuItem()
    {
        try
        {
            AddToStartup.IsChecked = RegistryManager.IsStartupEnabled;
        }
        catch
        {
            AddToStartup.IsChecked = false;
        }
    }

    private void PopulateHookProfiles(MenuItem parent, IEnumerable<HotKeyProfile> profiles, bool autoCheckParent = true)
    {
        parent.Items.Clear();

        foreach (var profile in profiles)
        {
            var profileItem = new MenuItem
            {
                Header = profile.Name,
                StaysOpenOnClick = true,
                IsCheckable = true,
                IsChecked = profile.IsEnabled,
                IsEnabled = parent.IsChecked,
                Command = ProfileItemCommand,
                Tag = profile
            };

            profileItem.CommandParameter = profileItem;
            parent.Items.Add(profileItem);
        }

        var anyChecked = parent.Items.Cast<MenuItem>().Any(item => item.IsChecked);

        // No subitems are checked, uncheck the parent.
        // At least one subitem is checked, check the parent (if autoCheckParent)
        var desiredParentChecked = anyChecked && (parent.IsChecked || autoCheckParent);
        if (desiredParentChecked != parent.IsChecked)
            parent.Command.Execute(parent.CommandParameter);
    }

    private void OnProfileItemClick(object? sender)
    {
        if (sender is not MenuItem { Tag: HotKeyProfile profile } item) return;

        _profileManager.SetProfileEnabledFromTray(profile, item.IsChecked);

        if (item.Parent is not MenuItem { IsChecked: true } parent) return;

        var anyChecked = parent.Items.OfType<MenuItem>().Any(m => m.IsChecked);
        if (anyChecked) return;

        // No subitems are checked, uncheck the parent
        parent.Command.Execute(parent.CommandParameter);
    }

    public void Dispose()
    {
        TrayIcon.Dispose();
        GC.SuppressFinalize(this);
    }
}