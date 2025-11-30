using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using FluentAssertions;
using Jellyfin.Plugin.Polyglot.Configuration;
using Jellyfin.Plugin.Polyglot.Models;
using Xunit;

namespace Jellyfin.Plugin.Polyglot.Tests.Configuration;

/// <summary>
/// Tests to ensure PluginConfiguration and all its nested types can be properly
/// serialized and deserialized using XML serialization (which Jellyfin uses).
/// These tests catch issues like Dictionary types that don't serialize.
/// </summary>
public class ConfigurationSerializationTests
{
    /// <summary>
    /// Verifies that an empty configuration can be serialized and deserialized.
    /// </summary>
    [Fact]
    public void EmptyConfiguration_CanSerializeAndDeserialize()
    {
        // Arrange
        var config = new PluginConfiguration();

        // Act & Assert - should not throw
        var result = SerializeAndDeserialize(config);

        result.Should().NotBeNull();
        result.LanguageAlternatives.Should().NotBeNull();
        result.UserLanguages.Should().NotBeNull();
    }

    /// <summary>
    /// Verifies that a configuration with language alternatives can be serialized.
    /// </summary>
    [Fact]
    public void Configuration_WithLanguageAlternatives_CanSerializeAndDeserialize()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            LanguageAlternatives = new List<LanguageAlternative>
            {
                new LanguageAlternative
                {
                    Id = Guid.NewGuid(),
                    Name = "Portuguese",
                    LanguageCode = "pt-BR",
                    MetadataLanguage = "pt",
                    MetadataCountry = "BR",
                    DestinationBasePath = "/media/portuguese",
                    CreatedAt = DateTime.UtcNow,
                    MirroredLibraries = new List<LibraryMirror>
                    {
                        new LibraryMirror
                        {
                            Id = Guid.NewGuid(),
                            SourceLibraryId = Guid.NewGuid(),
                            SourceLibraryName = "Movies",
                            TargetLibraryId = Guid.NewGuid(),
                            TargetLibraryName = "Filmes",
                            TargetPath = "/media/portuguese/movies",
                            CollectionType = "movies",
                            Status = SyncStatus.Synced,
                            LastSyncedAt = DateTime.UtcNow,
                            LastSyncFileCount = 100
                        }
                    }
                }
            }
        };

        // Act
        var result = SerializeAndDeserialize(config);

        // Assert
        result.LanguageAlternatives.Should().HaveCount(1);
        var alt = result.LanguageAlternatives[0];
        alt.Name.Should().Be("Portuguese");
        alt.LanguageCode.Should().Be("pt-BR");
        alt.MirroredLibraries.Should().HaveCount(1);
        alt.MirroredLibraries[0].SourceLibraryName.Should().Be("Movies");
        alt.MirroredLibraries[0].Status.Should().Be(SyncStatus.Synced);
    }

    /// <summary>
    /// Verifies that a configuration with user language assignments can be serialized.
    /// This specifically tests the List of UserLanguageConfig (which replaced Dictionary).
    /// </summary>
    [Fact]
    public void Configuration_WithUserLanguages_CanSerializeAndDeserialize()
    {
        // Arrange
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        var altId = Guid.NewGuid();

        var config = new PluginConfiguration
        {
            UserLanguages = new List<UserLanguageConfig>
            {
                new UserLanguageConfig
                {
                    UserId = userId1,
                    SelectedAlternativeId = altId,
                    ManuallySet = true,
                    SetAt = DateTime.UtcNow,
                    SetBy = "admin"
                },
                new UserLanguageConfig
                {
                    UserId = userId2,
                    SelectedAlternativeId = null, // No language assigned
                    ManuallySet = false,
                    SetAt = DateTime.UtcNow,
                    SetBy = "auto"
                }
            }
        };

        // Act
        var result = SerializeAndDeserialize(config);

        // Assert
        result.UserLanguages.Should().HaveCount(2);
        
        var user1Config = result.UserLanguages.Find(u => u.UserId == userId1);
        user1Config.Should().NotBeNull();
        user1Config!.SelectedAlternativeId.Should().Be(altId);
        user1Config.ManuallySet.Should().BeTrue();
        user1Config.SetBy.Should().Be("admin");

        var user2Config = result.UserLanguages.Find(u => u.UserId == userId2);
        user2Config.Should().NotBeNull();
        user2Config!.SelectedAlternativeId.Should().BeNull();
    }

    /// <summary>
    /// Verifies that all boolean and primitive settings are preserved.
    /// </summary>
    [Fact]
    public void Configuration_WithSettings_CanSerializeAndDeserialize()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            UserReconciliationTime = "04:30"
        };

        // Act
        var result = SerializeAndDeserialize(config);

        // Assert
        result.UserReconciliationTime.Should().Be("04:30");
    }

    /// <summary>
    /// Verifies that new user default settings are preserved.
    /// </summary>
    [Fact]
    public void Configuration_WithNewUserDefaults_CanSerializeAndDeserialize()
    {
        // Arrange
        var defaultLanguageId = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            AutoManageNewUsers = true,
            DefaultLanguageAlternativeId = defaultLanguageId
        };

        // Act
        var result = SerializeAndDeserialize(config);

        // Assert
        result.AutoManageNewUsers.Should().BeTrue();
        result.DefaultLanguageAlternativeId.Should().Be(defaultLanguageId);
    }

    /// <summary>
    /// Verifies that null DefaultLanguageAlternativeId is preserved.
    /// </summary>
    [Fact]
    public void Configuration_WithNullDefaultLanguage_CanSerializeAndDeserialize()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            AutoManageNewUsers = true,
            DefaultLanguageAlternativeId = null
        };

        // Act
        var result = SerializeAndDeserialize(config);

        // Assert
        result.AutoManageNewUsers.Should().BeTrue();
        result.DefaultLanguageAlternativeId.Should().BeNull();
    }

    /// <summary>
    /// Verifies that sync behavior settings are preserved.
    /// </summary>
    [Fact]
    public void Configuration_WithSyncBehaviorSettings_CanSerializeAndDeserialize()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncMirrorsAfterLibraryScan = false
        };

        // Act
        var result = SerializeAndDeserialize(config);

        // Assert
        result.SyncMirrorsAfterLibraryScan.Should().BeFalse();
    }

    /// <summary>
    /// Verifies that SyncMirrorsAfterLibraryScan defaults to true.
    /// </summary>
    [Fact]
    public void Configuration_Default_SyncMirrorsAfterLibraryScanIsTrue()
    {
        // Arrange & Act
        var config = new PluginConfiguration();

        // Assert
        config.SyncMirrorsAfterLibraryScan.Should().BeTrue();
    }

    /// <summary>
    /// Verifies that a fully populated configuration can be serialized and deserialized.
    /// This is a comprehensive test that exercises all fields.
    /// </summary>
    [Fact]
    public void FullyPopulatedConfiguration_CanSerializeAndDeserialize()
    {
        // Arrange
        var portugueseAltId = Guid.NewGuid();
        var spanishAltId = Guid.NewGuid();
        var moviesLibId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var config = new PluginConfiguration
        {
            AutoManageNewUsers = true,
            DefaultLanguageAlternativeId = portugueseAltId,
            UserReconciliationTime = "03:00",
            LanguageAlternatives = new List<LanguageAlternative>
            {
                new LanguageAlternative
                {
                    Id = portugueseAltId,
                    Name = "Portuguese",
                    LanguageCode = "pt-BR",
                    MetadataLanguage = "pt",
                    MetadataCountry = "BR",
                    DestinationBasePath = "/media/portuguese",
                    CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ModifiedAt = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    MirroredLibraries = new List<LibraryMirror>
                    {
                        new LibraryMirror
                        {
                            Id = Guid.NewGuid(),
                            SourceLibraryId = moviesLibId,
                            SourceLibraryName = "Movies",
                            TargetLibraryId = Guid.NewGuid(),
                            TargetLibraryName = "Filmes",
                            TargetPath = "/media/portuguese/movies",
                            CollectionType = "movies",
                            Status = SyncStatus.Synced,
                            LastSyncedAt = DateTime.UtcNow,
                            LastSyncFileCount = 500
                        }
                    }
                },
                new LanguageAlternative
                {
                    Id = spanishAltId,
                    Name = "Spanish",
                    LanguageCode = "es-ES",
                    MetadataLanguage = "es",
                    MetadataCountry = "ES",
                    DestinationBasePath = "/media/spanish",
                    CreatedAt = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                    MirroredLibraries = new List<LibraryMirror>()
                }
            },
            UserLanguages = new List<UserLanguageConfig>
            {
                new UserLanguageConfig
                {
                    UserId = userId,
                    SelectedAlternativeId = portugueseAltId,
                    ManuallySet = true,
                    SetAt = DateTime.UtcNow,
                    SetBy = "admin"
                }
            }
        };

        // Act
        var result = SerializeAndDeserialize(config);

        // Assert
        result.Should().NotBeNull();
        result.LanguageAlternatives.Should().HaveCount(2);
        result.UserLanguages.Should().HaveCount(1);
        
        // Verify nested data integrity
        var ptAlt = result.LanguageAlternatives.Find(a => a.Id == portugueseAltId);
        ptAlt.Should().NotBeNull();
        ptAlt!.MirroredLibraries.Should().HaveCount(1);
        ptAlt.MirroredLibraries[0].Status.Should().Be(SyncStatus.Synced);
    }

    /// <summary>
    /// Verifies that all SyncStatus enum values can be serialized.
    /// </summary>
    [Theory]
    [InlineData(SyncStatus.Pending)]
    [InlineData(SyncStatus.Syncing)]
    [InlineData(SyncStatus.Synced)]
    [InlineData(SyncStatus.Error)]
    public void SyncStatus_AllValues_CanSerializeAndDeserialize(SyncStatus status)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            LanguageAlternatives = new List<LanguageAlternative>
            {
                new LanguageAlternative
                {
                    Id = Guid.NewGuid(),
                    Name = "Test",
                    MirroredLibraries = new List<LibraryMirror>
                    {
                        new LibraryMirror
                        {
                            Id = Guid.NewGuid(),
                            SourceLibraryId = Guid.NewGuid(),
                            Status = status
                        }
                    }
                }
            }
        };

        // Act
        var result = SerializeAndDeserialize(config);

        // Assert
        result.LanguageAlternatives[0].MirroredLibraries[0].Status.Should().Be(status);
    }

    /// <summary>
    /// Verifies that nullable fields serialize correctly when null.
    /// </summary>
    [Fact]
    public void NullableFields_WhenNull_SerializeCorrectly()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            LanguageAlternatives = new List<LanguageAlternative>
            {
                new LanguageAlternative
                {
                    Id = Guid.NewGuid(),
                    Name = "Test",
                    ModifiedAt = null, // Nullable DateTime
                    MirroredLibraries = new List<LibraryMirror>
                    {
                        new LibraryMirror
                        {
                            Id = Guid.NewGuid(),
                            SourceLibraryId = Guid.NewGuid(),
                            TargetLibraryId = null, // Nullable Guid
                            CollectionType = null, // Nullable string
                            LastSyncedAt = null, // Nullable DateTime
                            LastError = null // Nullable string
                        }
                    }
                }
            },
            UserLanguages = new List<UserLanguageConfig>
            {
                new UserLanguageConfig
                {
                    UserId = Guid.NewGuid(),
                    SelectedAlternativeId = null, // Nullable Guid
                    SetAt = null, // Nullable DateTime
                    SetBy = null // Nullable string
                }
            }
        };

        // Act
        var result = SerializeAndDeserialize(config);

        // Assert
        var alt = result.LanguageAlternatives[0];
        alt.ModifiedAt.Should().BeNull();
        
        var mirror = alt.MirroredLibraries[0];
        mirror.TargetLibraryId.Should().BeNull();
        mirror.CollectionType.Should().BeNull();
        mirror.LastSyncedAt.Should().BeNull();
        mirror.LastError.Should().BeNull();
        
        var userLang = result.UserLanguages[0];
        userLang.SelectedAlternativeId.Should().BeNull();
        userLang.SetAt.Should().BeNull();
        userLang.SetBy.Should().BeNull();
    }

    /// <summary>
    /// Verifies that special characters in strings are handled correctly.
    /// </summary>
    [Fact]
    public void SpecialCharacters_InStrings_SerializeCorrectly()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            LanguageAlternatives = new List<LanguageAlternative>
            {
                new LanguageAlternative
                {
                    Id = Guid.NewGuid(),
                    Name = "Português & Español <test>",
                    DestinationBasePath = "/media/special chars/test's folder",
                    MirroredLibraries = new List<LibraryMirror>
                    {
                        new LibraryMirror
                        {
                            Id = Guid.NewGuid(),
                            SourceLibraryId = Guid.NewGuid(),
                            SourceLibraryName = "Movies & TV \"Shows\"",
                            TargetPath = "/path/with spaces/and (parentheses)/file.mkv"
                        }
                    }
                }
            }
        };

        // Act
        var result = SerializeAndDeserialize(config);

        // Assert
        result.LanguageAlternatives[0].Name.Should().Be("Português & Español <test>");
        result.LanguageAlternatives[0].DestinationBasePath.Should().Be("/media/special chars/test's folder");
        result.LanguageAlternatives[0].MirroredLibraries[0].SourceLibraryName.Should().Be("Movies & TV \"Shows\"");
    }

    /// <summary>
    /// Verifies that ExcludedExtensions are normalized to lowercase.
    /// </summary>
    [Fact]
    public void ExcludedExtensions_AreNormalizedToLowercase()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            ExcludedExtensions = new HashSet<string> { ".NFO", ".JPG", ".Png" }
        };

        // Assert - all stored values should be lowercase
        config.ExcludedExtensions.Should().HaveCount(3);
        config.ExcludedExtensions.Should().OnlyContain(ext => ext == ext.ToLowerInvariant());
        config.ExcludedExtensions.Should().Contain(".nfo");
        config.ExcludedExtensions.Should().Contain(".jpg");
        config.ExcludedExtensions.Should().Contain(".png");
    }

    /// <summary>
    /// Verifies that ExcludedDirectories are normalized to lowercase.
    /// </summary>
    [Fact]
    public void ExcludedDirectories_AreNormalizedToLowercase()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            ExcludedDirectories = new HashSet<string> { "METADATA", "ExtraFanart", ".Trickplay" }
        };

        // Assert - all stored values should be lowercase
        config.ExcludedDirectories.Should().HaveCount(3);
        config.ExcludedDirectories.Should().OnlyContain(dir => dir == dir.ToLowerInvariant());
        config.ExcludedDirectories.Should().Contain("metadata");
        config.ExcludedDirectories.Should().Contain("extrafanart");
        config.ExcludedDirectories.Should().Contain(".trickplay");
    }

    /// <summary>
    /// Verifies that duplicate ExcludedExtensions are removed (case-insensitive).
    /// </summary>
    [Fact]
    public void ExcludedExtensions_RemovesDuplicates()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            ExcludedExtensions = new HashSet<string> { ".nfo", ".NFO", ".Nfo", ".jpg", ".JPG" }
        };

        // Assert
        config.ExcludedExtensions.Should().HaveCount(2);
        config.ExcludedExtensions.Should().Contain(".nfo");
        config.ExcludedExtensions.Should().Contain(".jpg");
    }

    /// <summary>
    /// Verifies that duplicate ExcludedDirectories are removed (case-insensitive).
    /// </summary>
    [Fact]
    public void ExcludedDirectories_RemovesDuplicates()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            ExcludedDirectories = new HashSet<string> { "metadata", "METADATA", "Metadata", "extrafanart" }
        };

        // Assert
        config.ExcludedDirectories.Should().HaveCount(2);
        config.ExcludedDirectories.Should().Contain("metadata");
        config.ExcludedDirectories.Should().Contain("extrafanart");
    }

    /// <summary>
    /// Verifies that ExcludedExtensions normalization survives serialization.
    /// XML deserialization adds to existing collections, so the result contains
    /// both defaults and serialized values (deduplicated).
    /// </summary>
    [Fact]
    public void ExcludedExtensions_NormalizationSurvivesSerialization()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            ExcludedExtensions = new HashSet<string> { ".NFO", ".JPG", ".custom" }
        };

        // Act
        var result = SerializeAndDeserialize(config);

        // Assert - values should still be lowercase and contain both defaults and custom
        result.ExcludedExtensions.Should().OnlyContain(ext => ext == ext.ToLowerInvariant());
        result.ExcludedExtensions.Should().Contain(".nfo");
        result.ExcludedExtensions.Should().Contain(".jpg");
        result.ExcludedExtensions.Should().Contain(".custom");
    }

    /// <summary>
    /// Verifies that ExcludedDirectories normalization survives serialization.
    /// XML deserialization adds to existing collections, so the result contains
    /// both defaults and serialized values (deduplicated).
    /// </summary>
    [Fact]
    public void ExcludedDirectories_NormalizationSurvivesSerialization()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            ExcludedDirectories = new HashSet<string> { "METADATA", "customdir" }
        };

        // Act
        var result = SerializeAndDeserialize(config);

        // Assert - values should still be lowercase and contain both defaults and custom
        result.ExcludedDirectories.Should().OnlyContain(dir => dir == dir.ToLowerInvariant());
        result.ExcludedDirectories.Should().Contain("metadata");
        result.ExcludedDirectories.Should().Contain("customdir");
    }

    /// <summary>
    /// Verifies that default ExcludedExtensions are set from FileClassifier defaults.
    /// </summary>
    [Fact]
    public void Configuration_Default_HasExcludedExtensionsFromFileClassifier()
    {
        // Arrange & Act
        var config = new PluginConfiguration();

        // Assert
        config.ExcludedExtensions.Should().NotBeEmpty();
        config.ExcludedExtensions.Should().Contain(".nfo");
        config.ExcludedExtensions.Should().Contain(".jpg");
    }

    /// <summary>
    /// Verifies that default ExcludedDirectories are set from FileClassifier defaults.
    /// </summary>
    [Fact]
    public void Configuration_Default_HasExcludedDirectoriesFromFileClassifier()
    {
        // Arrange & Act
        var config = new PluginConfiguration();

        // Assert
        config.ExcludedDirectories.Should().NotBeEmpty();
        config.ExcludedDirectories.Should().Contain("metadata");
        config.ExcludedDirectories.Should().Contain("extrafanart");
    }

    /// <summary>
    /// Helper method to serialize and deserialize a configuration using XmlSerializer.
    /// </summary>
    private static PluginConfiguration SerializeAndDeserialize(PluginConfiguration config)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        
        using var stream = new MemoryStream();
        serializer.Serialize(stream, config);
        
        stream.Position = 0;
        
        var result = serializer.Deserialize(stream) as PluginConfiguration;
        return result ?? throw new InvalidOperationException("Deserialization returned null");
    }
}

