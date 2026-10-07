using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Collections.Generic;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Localization;

namespace ExplorerTabUtility.UI.Views;

/// <summary>
/// "Tab groups" page: create, rename and delete groups, edit/reorder their folders and open them.
/// Every change is saved to settings.json right away (like the Preferences page).
/// </summary>
// ReSharper disable once RedundantExtendsListEntry
public partial class TabGroupsView : UserControl
{
    private HookManager? _hookManager;
    private bool _isUpdating;

    private TabGroup? SelectedGroup => LstGroups.SelectedItem as TabGroup;

    public TabGroupsView()
    {
        InitializeComponent();

        BtnNewGroup.Click += BtnNewGroup_Click;
        BtnCaptureGroup.Click += BtnCaptureGroup_Click;
        BtnDeleteGroup.Click += BtnDeleteGroup_Click;
        BtnOpenGroup.Click += BtnOpenGroup_Click;
        BtnAddPath.Click += (_, _) => AddPaths([TxtNewPath.Text], clearInput: true);
        BtnBrowse.Click += BtnBrowse_Click;
        BtnMoveUp.Click += (_, _) => MoveSelectedPath(-1);
        BtnMoveDown.Click += (_, _) => MoveSelectedPath(1);
        BtnRemovePath.Click += (_, _) => RemoveSelectedPath();

        LstGroups.SelectionChanged += (_, _) => { if (!_isUpdating) UpdateEditor(); };
        LstPaths.SelectionChanged += (_, _) => UpdateButtons();
        LstPaths.KeyDown += LstPaths_KeyDown;
        LstPaths.DragOver += LstPaths_DragOver;
        LstPaths.Drop += LstPaths_Drop;

        TxtGroupName.TextChanged += TxtGroupName_TextChanged;
        TxtGroupName.LostFocus += (_, _) => CommitGroupName();
        TxtGroupName.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitGroupName(); };
        TxtNewPath.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddPaths([TxtNewPath.Text], clearInput: true); };

        // Groups can also change from the tray menu ("Save current window as group").
        TabGroupManager.GroupsChanged += () => RefreshGroups();

        RefreshGroups();
    }

    public void Initialize(HookManager hookManager) => _hookManager = hookManager;

    private void RefreshGroups(TabGroup? groupToSelect = null)
    {
        var selectedId = (groupToSelect ?? SelectedGroup)?.Id;
        var groups = TabGroupManager.Groups.ToList();

        _isUpdating = true;
        try
        {
            LstGroups.ItemsSource = groups;
            LstGroups.SelectedItem = groups.FirstOrDefault(g => g.Id == selectedId) ?? groups.FirstOrDefault();
        }
        finally
        {
            _isUpdating = false;
        }

        TxtNoGroups.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateEditor();
    }

    private void UpdateEditor()
    {
        var group = SelectedGroup;
        EditorPanel.Visibility = group == null ? Visibility.Collapsed : Visibility.Visible;
        TxtSelectHint.Visibility = group == null && TabGroupManager.Groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnDeleteGroup.IsEnabled = group != null;
        if (group == null) return;

        // Don't overwrite the name while the user is typing it.
        if (!TxtGroupName.IsKeyboardFocusWithin && TxtGroupName.Text != group.Name)
        {
            _isUpdating = true;
            TxtGroupName.Text = group.Name;
            _isUpdating = false;
        }

        RefreshPaths(group);
    }

    private void RefreshPaths(TabGroup group, int selectIndex = -1)
    {
        LstPaths.ItemsSource = group.Paths.ToList();
        if (selectIndex >= 0 && selectIndex < group.Paths.Count)
        {
            LstPaths.SelectedIndex = selectIndex;
            LstPaths.ScrollIntoView(LstPaths.SelectedItem);
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var group = SelectedGroup;
        var index = LstPaths.SelectedIndex;
        BtnOpenGroup.IsEnabled = group?.Paths.Count > 0;
        BtnMoveUp.IsEnabled = group != null && index > 0;
        BtnMoveDown.IsEnabled = group != null && index >= 0 && index < group.Paths.Count - 1;
        BtnRemovePath.IsEnabled = group != null && index >= 0;
    }

    // Groups
    private void BtnNewGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = TabGroupManager.Add(Loc.Get("TabGroups_DefaultName"));
        RefreshGroups(group);
        TxtGroupName.Focus();
        TxtGroupName.SelectAll();
    }

    private void BtnCaptureGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = _hookManager?.SaveWindowAsTabGroup();
        if (group == null) return;

        RefreshGroups(group);
        TxtGroupName.Focus();
        TxtGroupName.SelectAll();
    }

    private void BtnDeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = SelectedGroup;
        if (group == null) return;

        var result = CustomMessageBox.Show(Window.GetWindow(this), Loc.Format("TabGroups_DeleteConfirm", group.Name), Loc.Get("App_Title"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        TabGroupManager.Remove(group);
    }

    private async void BtnOpenGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = SelectedGroup;
        if (group == null || _hookManager == null) return;

        try
        {
            await _hookManager.OpenTabGroupAsync(group);
        }
        catch
        {
            // Explorer might have been restarted meanwhile.
        }
    }

    private void TxtGroupName_TextChanged(object sender, TextChangedEventArgs e)
    {
        var group = SelectedGroup;
        if (_isUpdating || group == null) return;

        group.Name = TxtGroupName.Text.Trim();
        TabGroupManager.Save(notify: false);
        LstGroups.Items.Refresh();
    }

    private void CommitGroupName()
    {
        var group = SelectedGroup;
        if (group == null) return;

        if (string.IsNullOrWhiteSpace(group.Name))
        {
            group.Name = TabGroupManager.GetUniqueName(Loc.Get("TabGroups_DefaultName"));
            TabGroupManager.Save(notify: false);
            _isUpdating = true;
            TxtGroupName.Text = group.Name;
            _isUpdating = false;
        }

        // Let the shortcut editors and the tray menu pick up the new name.
        TabGroupManager.NotifyChanged();
    }

    // Folders
    private void AddPaths(IEnumerable<string> paths, bool clearInput = false)
    {
        var group = SelectedGroup;
        if (group == null) return;

        var lastIndex = -1;
        foreach (var rawPath in paths)
        {
            var path = rawPath.Trim().Trim('"').Trim();
            if (path.Length == 0) continue;

            var existingIndex = group.Paths.FindIndex(p => string.Equals(p.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                lastIndex = existingIndex;
                continue;
            }

            group.Paths.Add(path);
            lastIndex = group.Paths.Count - 1;
        }

        if (clearInput) TxtNewPath.Text = string.Empty;
        if (lastIndex < 0) return;

        TabGroupManager.Save();
        RefreshPaths(group, lastIndex);
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var folders = PickFolders();
        if (folders.Count > 0) AddPaths(folders);
    }

    private List<string> PickFolders()
    {
        var owner = Window.GetWindow(this);
#if NET8_0_OR_GREATER
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.Get("TabGroups_BrowseTitle"),
            Multiselect = true
        };
        return dialog.ShowDialog(owner) == true ? dialog.FolderNames.ToList() : [];
#else
        // .NET Framework has no WPF folder dialog; use the shell's folder picker (also allows This PC, Libraries, ...).
        Shell32.Shell? shell = null;
        try
        {
            shell = new Shell32.Shell();
            var hWnd = owner == null ? 0 : (int)new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            const int bifEditBox = 0x10, bifNewDialogStyle = 0x40;
            var folder = shell.BrowseForFolder(hWnd, Loc.Get("TabGroups_BrowseTitle"), bifEditBox | bifNewDialogStyle, 0);
            if (folder is not Shell32.Folder2 folder2) return [];

            var path = folder2.Self.Path;
            return string.IsNullOrWhiteSpace(path) ? [] : [path];
        }
        catch
        {
            return [];
        }
        finally
        {
            if (shell != null)
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
#endif
    }

    private void MoveSelectedPath(int offset)
    {
        var group = SelectedGroup;
        var index = LstPaths.SelectedIndex;
        if (group == null || index < 0) return;

        var newIndex = index + offset;
        if (newIndex < 0 || newIndex >= group.Paths.Count) return;

        (group.Paths[index], group.Paths[newIndex]) = (group.Paths[newIndex], group.Paths[index]);
        TabGroupManager.Save();
        RefreshPaths(group, newIndex);
    }

    private void RemoveSelectedPath()
    {
        var group = SelectedGroup;
        var index = LstPaths.SelectedIndex;
        if (group == null || index < 0) return;

        group.Paths.RemoveAt(index);
        TabGroupManager.Save();
        RefreshPaths(group, Math.Min(index, group.Paths.Count - 1));
    }

    private void LstPaths_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            RemoveSelectedPath();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Up or Key.Down)
        {
            MoveSelectedPath(e.SystemKey == Key.Up ? -1 : 1);
            e.Handled = true;
        }
    }

    // Folders can be dragged here from File Explorer.
    private static void LstPaths_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void LstPaths_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] items) return;
        AddPaths(items.Where(Directory.Exists));
    }
}
