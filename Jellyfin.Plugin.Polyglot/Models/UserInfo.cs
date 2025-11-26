using System;

namespace Jellyfin.Plugin.Polyglot.Models;

/// <summary>
/// Represents information about a user with their language assignment.
/// </summary>
public class UserInfo
{
    /// <summary>
    /// Gets or sets the user ID.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the user is an administrator.
    /// </summary>
    public bool IsAdministrator { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin manages this user's library access.
    /// </summary>
    public bool IsPluginManaged { get; set; }

    /// <summary>
    /// Gets or sets the assigned language alternative ID.
    /// </summary>
    public Guid? AssignedAlternativeId { get; set; }

    /// <summary>
    /// Gets or sets the assigned language name.
    /// </summary>
    public string? AssignedAlternativeName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this was manually set.
    /// </summary>
    public bool ManuallySet { get; set; }

    /// <summary>
    /// Gets or sets who/what set this assignment.
    /// </summary>
    public string? SetBy { get; set; }

    /// <summary>
    /// Gets or sets when the assignment was set.
    /// </summary>
    public DateTime? SetAt { get; set; }
}

