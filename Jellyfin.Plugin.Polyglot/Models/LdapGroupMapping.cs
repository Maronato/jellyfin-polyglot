using System;

namespace Jellyfin.Plugin.Polyglot.Models;

/// <summary>
/// Represents a mapping from an LDAP group to a language alternative.
/// </summary>
public class LdapGroupMapping
{
    /// <summary>
    /// Gets or sets the unique identifier.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the full DN or CN of the LDAP group.
    /// </summary>
    public string LdapGroupDn { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the friendly name for UI display.
    /// </summary>
    public string LdapGroupName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the language alternative this group maps to.
    /// </summary>
    public Guid LanguageAlternativeId { get; set; }

    /// <summary>
    /// Gets or sets the priority. Higher priority wins if user is in multiple groups.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Creates a deep copy of this LDAP group mapping.
    /// </summary>
    /// <returns>A new LdapGroupMapping instance with copied values.</returns>
    public LdapGroupMapping DeepClone()
    {
        return new LdapGroupMapping
        {
            Id = Id,
            LdapGroupDn = LdapGroupDn,
            LdapGroupName = LdapGroupName,
            LanguageAlternativeId = LanguageAlternativeId,
            Priority = Priority
        };
    }
}

