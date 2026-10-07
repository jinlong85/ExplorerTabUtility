using System;
using System.Collections.Generic;

namespace ExplorerTabUtility.Models;

/// <summary>
/// A named, ordered set of folders that can be opened as tabs in one File Explorer window.
/// Paths can be normal folder paths (C:\Work, \\server\share, %USERPROFILE%\Downloads)
/// or shell locations (shell:Downloads, shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D} = This PC).
/// </summary>
public class TabGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<string> Paths { get; set; } = [];

    // Used by the themed ComboBox / ListBox templates, which display item.ToString().
    public override string ToString() => Name;
}
