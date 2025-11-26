namespace Jellyfin.Plugin.Polyglot.Models;

/// <summary>
/// Represents the synchronization status of a library mirror.
/// </summary>
public enum SyncStatus
{
    /// <summary>
    /// Mirror is pending initial sync.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Mirror is currently being synchronized.
    /// </summary>
    Syncing = 1,

    /// <summary>
    /// Mirror is synchronized and up to date.
    /// </summary>
    Synced = 2,

    /// <summary>
    /// Mirror sync encountered an error.
    /// </summary>
    Error = 3
}

