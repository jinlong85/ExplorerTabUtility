using System;
using System.Linq;
using System.Collections.Generic;
using ExplorerTabUtility.Models;

namespace ExplorerTabUtility.Managers;

/// <summary>
/// Keeps the tab groups (stored in settings.json, "TabGroups") and notifies the UI when they change.
/// All changes are expected to happen on the UI thread.
/// </summary>
public static class TabGroupManager
{
    /// <summary>Raised after groups were added, removed, renamed or edited.</summary>
    public static event Action? GroupsChanged;

    public static IReadOnlyList<TabGroup> Groups => SettingsManager.TabGroups;

    /// <summary>Finds a group by its id (as stored in a shortcut) or, as a fallback, by its name.</summary>
    public static TabGroup? Find(string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;

        var groups = SettingsManager.TabGroups;
        if (Guid.TryParse(idOrName, out var id))
        {
            var byId = groups.FirstOrDefault(g => g.Id == id);
            if (byId != null) return byId;
        }

        return groups.FirstOrDefault(g => string.Equals(g.Name, idOrName!.Trim(), StringComparison.CurrentCultureIgnoreCase));
    }

    public static TabGroup Add(string name, IEnumerable<string>? paths = null)
    {
        var group = new TabGroup
        {
            Name = GetUniqueName(name),
            Paths = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? []
        };

        SettingsManager.TabGroups.Add(group);
        Save();
        return group;
    }

    public static void Remove(TabGroup group)
    {
        if (SettingsManager.TabGroups.Remove(group))
            Save();
    }

    /// <summary>Persists the groups. Pass <paramref name="notify"/> = false while the user is still typing.</summary>
    public static void Save(bool notify = true)
    {
        SettingsManager.SaveSettings();
        if (notify) NotifyChanged();
    }

    public static void NotifyChanged() => GroupsChanged?.Invoke();

    /// <summary>"Name", or "Name (2)", "Name (3)"... if a group with that name already exists.</summary>
    public static string GetUniqueName(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Group" : name.Trim();
        var existing = new HashSet<string>(SettingsManager.TabGroups.Select(g => g.Name), StringComparer.CurrentCultureIgnoreCase);
        if (!existing.Contains(name)) return name;

        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!existing.Contains(candidate)) return candidate;
        }
    }
}
