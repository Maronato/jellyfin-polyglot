using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Polyglot.Models;

/// <summary>
/// Represents information about a Jellyfin library.
/// </summary>
public class LibraryInfo
{
    /// <summary>
    /// Gets or sets the library ID.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection type (movies, tvshows, etc.).
    /// </summary>
    public string? CollectionType { get; set; }

    /// <summary>
    /// Gets or sets the library paths.
    /// </summary>
    public List<string> Paths { get; set; } = new();

    /// <summary>
    /// Gets or sets the preferred metadata language.
    /// </summary>
    public string? PreferredMetadataLanguage { get; set; }

    /// <summary>
    /// Gets or sets the metadata country code.
    /// </summary>
    public string? MetadataCountryCode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this library is a mirror created by this plugin.
    /// </summary>
    public bool IsMirror { get; set; }

    /// <summary>
    /// Gets or sets the language alternative ID if this is a mirror.
    /// </summary>
    public Guid? LanguageAlternativeId { get; set; }
}

