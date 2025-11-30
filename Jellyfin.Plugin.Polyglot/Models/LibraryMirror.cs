using System;

namespace Jellyfin.Plugin.Polyglot.Models;

/// <summary>
/// Represents a source-to-target library mirror mapping.
/// </summary>
public class LibraryMirror
{
    /// <summary>
    /// Gets or sets the unique identifier for this mirror.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the source library GUID.
    /// </summary>
    public Guid SourceLibraryId { get; set; }

    /// <summary>
    /// Gets or sets the source library name for display purposes.
    /// </summary>
    public string SourceLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the created mirror library GUID (null until created).
    /// </summary>
    public Guid? TargetLibraryId { get; set; }

    /// <summary>
    /// Gets or sets the mirror library name.
    /// </summary>
    public string TargetLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the full path to the mirrored content.
    /// </summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection type (movies, tvshows, etc.).
    /// </summary>
    public string? CollectionType { get; set; }

    /// <summary>
    /// Gets or sets the current sync status.
    /// </summary>
    public SyncStatus Status { get; set; } = SyncStatus.Pending;

    /// <summary>
    /// Gets or sets the last successful sync time.
    /// </summary>
    public DateTime? LastSyncedAt { get; set; }

    /// <summary>
    /// Gets or sets the last error message if status is Error.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets the count of files synced in the last sync operation.
    /// </summary>
    public int LastSyncFileCount { get; set; }

    /// <summary>
    /// Creates a deep copy of this mirror.
    /// </summary>
    /// <returns>A new LibraryMirror instance with copied values.</returns>
    public LibraryMirror DeepClone()
    {
        return new LibraryMirror
        {
            Id = Id,
            SourceLibraryId = SourceLibraryId,
            SourceLibraryName = SourceLibraryName,
            TargetLibraryId = TargetLibraryId,
            TargetLibraryName = TargetLibraryName,
            TargetPath = TargetPath,
            CollectionType = CollectionType,
            Status = Status,
            LastSyncedAt = LastSyncedAt,
            LastError = LastError,
            LastSyncFileCount = LastSyncFileCount
        };
    }
}

