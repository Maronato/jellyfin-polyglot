using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Polyglot.Models;

/// <summary>
/// Represents a language configuration with its destination path and mirrored libraries.
/// </summary>
public class LanguageAlternative
{
    /// <summary>
    /// Gets or sets the unique identifier.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the display name (e.g., "Portuguese").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the locale code (e.g., "pt-BR").
    /// </summary>
    public string LanguageCode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the metadata language code (e.g., "pt").
    /// </summary>
    public string MetadataLanguage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the metadata country code (e.g., "BR").
    /// </summary>
    public string MetadataCountry { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base path for mirror directories (e.g., "/media/portuguese").
    /// </summary>
    public string DestinationBasePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of libraries mirrored for this language.
    /// </summary>
    public List<LibraryMirror> MirroredLibraries { get; set; } = new();

    /// <summary>
    /// Gets or sets when this alternative was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets when this alternative was last modified.
    /// </summary>
    public DateTime? ModifiedAt { get; set; }

    /// <summary>
    /// Creates a deep copy of this alternative, including all mirrored libraries.
    /// </summary>
    /// <returns>A new LanguageAlternative instance with copied values.</returns>
    public LanguageAlternative DeepClone()
    {
        return new LanguageAlternative
        {
            Id = Id,
            Name = Name,
            LanguageCode = LanguageCode,
            MetadataLanguage = MetadataLanguage,
            MetadataCountry = MetadataCountry,
            DestinationBasePath = DestinationBasePath,
            MirroredLibraries = MirroredLibraries.Select(m => m.DeepClone()).ToList(),
            CreatedAt = CreatedAt,
            ModifiedAt = ModifiedAt
        };
    }
}

