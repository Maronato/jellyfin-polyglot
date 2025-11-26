using System;

namespace Jellyfin.Plugin.MultiLang.Models;

/// <summary>
/// Represents a user's language assignment configuration.
/// </summary>
public class UserLanguageConfig
{
    /// <summary>
    /// Gets or sets the user identifier.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the username for display purposes.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the assigned language alternative ID (null = default/no filter).
    /// </summary>
    public Guid? SelectedAlternativeId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this was manually set by admin.
    /// If true, LDAP won't override.
    /// </summary>
    public bool ManuallySet { get; set; }

    /// <summary>
    /// Gets or sets when the assignment was made.
    /// </summary>
    public DateTime? SetAt { get; set; }

    /// <summary>
    /// Gets or sets the source of assignment: "ldap", "admin", "user-sync".
    /// </summary>
    public string? SetBy { get; set; }
}

