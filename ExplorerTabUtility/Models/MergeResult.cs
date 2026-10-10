namespace ExplorerTabUtility.Models;

/// <summary>Outcome of merging File Explorer windows (see ExplorerWatcher.MergeWindowsAsync).</summary>
public sealed class MergeResult
{
    /// <summary>Windows whose tabs were all moved into the target window and that were closed.</summary>
    public int MergedWindows { get; set; }

    /// <summary>Windows that were left open because not all of their tabs could be moved.</summary>
    public int FailedWindows { get; set; }

    /// <summary>Tabs opened in the target window.</summary>
    public int MovedTabs { get; set; }

    /// <summary>Tabs of merged windows that were not reopened because their folder was already open in the target window.</summary>
    public int SkippedTabs { get; set; }
}
