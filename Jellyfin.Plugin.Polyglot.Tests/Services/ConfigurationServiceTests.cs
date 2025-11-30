using FluentAssertions;
using Jellyfin.Plugin.Polyglot.Models;
using Jellyfin.Plugin.Polyglot.Services;
using Jellyfin.Plugin.Polyglot.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Polyglot.Tests.Services;

/// <summary>
/// Tests for ConfigurationService that verify thread-safe configuration operations
/// using the generic Read/Update pattern.
/// Uses PluginTestContext to set up Plugin.Instance properly.
/// </summary>
public class ConfigurationServiceTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly ConfigurationService _service;

    public ConfigurationServiceTests()
    {
        _context = new PluginTestContext();
        var logger = new Mock<ILogger<ConfigurationService>>();
        _service = new ConfigurationService(logger.Object);
    }

    public void Dispose() => _context.Dispose();

    #region Read - Immutable Snapshot Behavior

    [Fact]
    public void Read_ReturnsImmutableSnapshot_ModificationsDoNotAffectConfig()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");

        // Act - Read the alternative and modify it
        var retrieved = _service.Read(c => c.LanguageAlternatives.FirstOrDefault(a => a.Id == alternative.Id));
        retrieved.Should().NotBeNull();
        retrieved!.Name = "Modified Name";
        retrieved.MirroredLibraries.Add(new LibraryMirror { Id = Guid.NewGuid() });

        // Assert - Original config should be unchanged
        var original = _context.Configuration.LanguageAlternatives.First(a => a.Id == alternative.Id);
        original.Name.Should().Be("Portuguese", "modifications to returned copy should not affect config");
        original.MirroredLibraries.Should().BeEmpty("adding to returned copy should not affect config");
    }

    [Fact]
    public void Read_NonExistentEntity_ReturnsNull()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = _service.Read(c => c.LanguageAlternatives.FirstOrDefault(a => a.Id == nonExistentId));

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Read_ReturnsDeepCopiesOfCollections()
    {
        // Arrange
        _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.AddLanguageAlternative("Spanish", "es-ES");

        // Act
        var alternatives = _service.Read(c => c.LanguageAlternatives.ToList());

        // Assert
        alternatives.Should().HaveCount(2);

        // Modify returned list
        alternatives.ForEach(a => a.Name = "Modified");

        // Config should be unchanged
        _context.Configuration.LanguageAlternatives
            .Any(a => a.Name == "Modified")
            .Should().BeFalse("modifications to returned copies should not affect config");
    }

    [Fact]
    public void Read_Mirror_ReturnsDeepCopy_ModificationsDoNotAffectConfig()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        var mirror = _context.AddMirror(alternative, sourceLibraryId, "Movies");

        // Act - Read the mirror and modify it
        var retrieved = _service.Read(c => c.LanguageAlternatives
            .SelectMany(a => a.MirroredLibraries)
            .FirstOrDefault(m => m.Id == mirror.Id));
        retrieved.Should().NotBeNull();
        retrieved!.TargetLibraryName = "Modified Name";
        retrieved.Status = SyncStatus.Error;

        // Assert - Original config should be unchanged
        var original = _context.Configuration.LanguageAlternatives
            .SelectMany(a => a.MirroredLibraries)
            .First(m => m.Id == mirror.Id);
        original.TargetLibraryName.Should().NotBe("Modified Name");
        original.Status.Should().Be(SyncStatus.Synced); // AddMirror sets Synced status
    }

    [Fact]
    public void Read_ScalarValue_ReturnsCorrectValue()
    {
        // Arrange
        _context.Configuration.AutoManageNewUsers = true;
        _context.Configuration.UserReconciliationTime = "03:00";

        // Act
        var (autoManage, reconcTime) = _service.Read(c => (c.AutoManageNewUsers, c.UserReconciliationTime));

        // Assert
        autoManage.Should().BeTrue();
        reconcTime.Should().Be("03:00");
    }

    #endregion

    #region Update(Action) - Always Saves

    [Fact]
    public void Update_ActionOverload_AlwaysSaves()
    {
        // Arrange
        _context.Configuration.AutoManageNewUsers = false;

        // Act
        _service.Update(c => c.AutoManageNewUsers = true);

        // Assert
        _context.Configuration.AutoManageNewUsers.Should().BeTrue();
    }

    [Fact]
    public void Update_AddAlternative_PersistsChange()
    {
        // Arrange
        var alternative = new LanguageAlternative
        {
            Id = Guid.NewGuid(),
            Name = "Portuguese",
            LanguageCode = "pt-BR"
        };

        // Act
        _service.Update(c => c.LanguageAlternatives.Add(alternative));

        // Assert
        _context.Configuration.LanguageAlternatives.Should().Contain(a => a.Id == alternative.Id);
    }

    [Fact]
    public void Update_RemoveAlternative_PersistsChange()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");

        // Act
        _service.Update(c =>
        {
            var toRemove = c.LanguageAlternatives.FirstOrDefault(a => a.Id == alternative.Id);
            if (toRemove != null) c.LanguageAlternatives.Remove(toRemove);
        });

        // Assert
        _context.Configuration.LanguageAlternatives.Should().NotContain(a => a.Id == alternative.Id);
    }

    #endregion

    #region Update(Func<bool>) - Conditional Save

    [Fact]
    public void Update_FuncReturnsTrue_SavesChanges()
    {
        // Arrange
        _context.Configuration.AutoManageNewUsers = false;

        // Act
        var result = _service.Update(c =>
        {
            c.AutoManageNewUsers = true;
            return true; // Save
        });

        // Assert
        result.Should().BeTrue();
        _context.Configuration.AutoManageNewUsers.Should().BeTrue();
    }

    [Fact]
    public void Update_FuncReturnsFalse_DiscardsChanges()
    {
        // Arrange
        _context.Configuration.AutoManageNewUsers = false;

        // Act - Return false to abort (validation failure scenario)
        var result = _service.Update(c =>
        {
            c.AutoManageNewUsers = true; // Would be set in snapshot
            return false; // But abort - don't save
        });

        // Assert
        result.Should().BeFalse();
        // Original config should be unchanged because we worked on a snapshot
        _context.Configuration.AutoManageNewUsers.Should().BeFalse(
            "snapshot should be discarded when returning false");
    }

    [Fact]
    public void Update_DuplicateAlternativeName_CanRejectWithFalse()
    {
        // Arrange
        _context.AddLanguageAlternative("Portuguese", "pt-BR");

        var duplicate = new LanguageAlternative
        {
            Id = Guid.NewGuid(),
            Name = "Portuguese", // Same name
            LanguageCode = "pt-PT"
        };

        // Act
        var result = _service.Update(c =>
        {
            if (c.LanguageAlternatives.Any(a => string.Equals(a.Name, duplicate.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return false; // Reject duplicate
            }
            c.LanguageAlternatives.Add(duplicate);
            return true;
        });

        // Assert
        result.Should().BeFalse("should reject duplicate name");
        _context.Configuration.LanguageAlternatives.Should().HaveCount(1);
    }

    [Fact]
    public void Update_DuplicateSourceLibrary_CanRejectWithFalse()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        _context.AddMirror(alternative, sourceLibraryId, "Movies");

        var duplicateMirror = new LibraryMirror
        {
            Id = Guid.NewGuid(),
            SourceLibraryId = sourceLibraryId, // Same source
            TargetLibraryName = "Filmes 2"
        };

        // Act
        var result = _service.Update(c =>
        {
            var alt = c.LanguageAlternatives.FirstOrDefault(a => a.Id == alternative.Id);
            if (alt == null) return false;
            if (alt.MirroredLibraries.Any(m => m.SourceLibraryId == duplicateMirror.SourceLibraryId))
            {
                return false; // Reject duplicate source
            }
            alt.MirroredLibraries.Add(duplicateMirror);
            return true;
        });

        // Assert
        result.Should().BeFalse("should reject duplicate source library");
        var updatedAlt = _context.Configuration.LanguageAlternatives.First(a => a.Id == alternative.Id);
        updatedAlt.MirroredLibraries.Should().HaveCount(1);
    }

    #endregion

    #region Update - Dangling Reference Cleanup

    [Fact]
    public void Update_RemoveAlternative_ClearsDefaultLanguageAlternativeId_WhenDeletingDefault()
    {
        // Arrange - Set up an alternative as the default
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.Configuration.DefaultLanguageAlternativeId = alternative.Id;

        // Act
        _service.Update(c =>
        {
            var toRemove = c.LanguageAlternatives.FirstOrDefault(a => a.Id == alternative.Id);
            if (toRemove != null)
            {
                c.LanguageAlternatives.Remove(toRemove);
                // Clean up dangling reference
                if (c.DefaultLanguageAlternativeId == alternative.Id)
                {
                    c.DefaultLanguageAlternativeId = null;
                }
            }
        });

        // Assert
        _context.Configuration.DefaultLanguageAlternativeId.Should().BeNull(
            "DefaultLanguageAlternativeId should be cleared when the default alternative is deleted");
    }

    [Fact]
    public void Update_RemoveAlternative_PreservesDefaultLanguageAlternativeId_WhenDeletingOther()
    {
        // Arrange - Set up two alternatives, one as default
        var defaultAlt = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var otherAlt = _context.AddLanguageAlternative("Spanish", "es-ES");
        _context.Configuration.DefaultLanguageAlternativeId = defaultAlt.Id;

        // Act - Delete the non-default one
        _service.Update(c =>
        {
            var toRemove = c.LanguageAlternatives.FirstOrDefault(a => a.Id == otherAlt.Id);
            if (toRemove != null) c.LanguageAlternatives.Remove(toRemove);
        });

        // Assert
        _context.Configuration.DefaultLanguageAlternativeId.Should().Be(defaultAlt.Id,
            "DefaultLanguageAlternativeId should be preserved when deleting a non-default alternative");
    }

    #endregion

    #region Update - Mirror Operations

    [Fact]
    public void Update_Mirror_AppliesChanges()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        var mirror = _context.AddMirror(alternative, sourceLibraryId, "Movies");

        // Act
        _service.Update(c =>
        {
            var m = c.LanguageAlternatives
                .SelectMany(a => a.MirroredLibraries)
                .FirstOrDefault(x => x.Id == mirror.Id);
            if (m != null)
            {
                m.Status = SyncStatus.Error;
                m.LastSyncFileCount = 100;
            }
        });

        // Assert
        var updated = _context.Configuration.LanguageAlternatives
            .SelectMany(a => a.MirroredLibraries)
            .First(m => m.Id == mirror.Id);
        updated.Status.Should().Be(SyncStatus.Error);
        updated.LastSyncFileCount.Should().Be(100);
    }

    #endregion

    #region User Language Operations

    [Fact]
    public void Update_CreateUserLanguage_NewUser_CreatesEntry()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var altId = _context.AddLanguageAlternative().Id;

        // Act
        _service.Update(c =>
        {
            var existing = c.UserLanguages.FirstOrDefault(u => u.UserId == userId);
            if (existing == null)
            {
                c.UserLanguages.Add(new UserLanguageConfig
                {
                    UserId = userId,
                    SelectedAlternativeId = altId,
                    IsPluginManaged = true
                });
            }
        });

        // Assert
        _context.Configuration.UserLanguages.Should().Contain(u => u.UserId == userId);
    }

    [Fact]
    public void Update_UpdateUserLanguage_ExistingUser_UpdatesEntry()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var alt1 = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var alt2 = _context.AddLanguageAlternative("Spanish", "es-ES");
        _context.AddUserLanguage(userId, alt1.Id);

        // Act
        _service.Update(c =>
        {
            var userConfig = c.UserLanguages.FirstOrDefault(u => u.UserId == userId);
            if (userConfig != null)
            {
                userConfig.SelectedAlternativeId = alt2.Id;
            }
        });

        // Assert
        var updated = _context.Configuration.UserLanguages.First(u => u.UserId == userId);
        updated.SelectedAlternativeId.Should().Be(alt2.Id);
    }

    [Fact]
    public void Read_UserLanguage_ReturnsDeepCopy()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var altId = _context.AddLanguageAlternative().Id;
        _context.AddUserLanguage(userId, altId, manuallySet: true, setBy: "admin");

        // Act
        var retrieved = _service.Read(c => c.UserLanguages.FirstOrDefault(u => u.UserId == userId));
        retrieved.Should().NotBeNull();
        retrieved!.ManuallySet = false; // Modify the copy

        // Assert - Original should be unchanged
        var original = _context.Configuration.UserLanguages.First(u => u.UserId == userId);
        original.ManuallySet.Should().BeTrue();
    }

    #endregion

    #region ClearAllConfiguration

    [Fact]
    public void Update_ClearAllConfiguration_RemovesAllData()
    {
        // Arrange
        var alt = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        _context.AddMirror(alt, sourceLibraryId, "Movies");
        _context.AddUserLanguage(Guid.NewGuid(), alt.Id);

        // Verify setup
        _context.Configuration.LanguageAlternatives.Should().NotBeEmpty();
        _context.Configuration.UserLanguages.Should().NotBeEmpty();

        // Act
        _service.Update(c =>
        {
            c.LanguageAlternatives.Clear();
            c.UserLanguages.Clear();
        });

        // Assert
        _context.Configuration.LanguageAlternatives.Should().BeEmpty();
        _context.Configuration.UserLanguages.Should().BeEmpty();
    }

    #endregion

    #region Read - Mirror with Alternative

    [Fact]
    public void Read_MirrorWithAlternative_ExistingMirror_ReturnsMirrorAndAlternativeId()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        var mirror = _context.AddMirror(alternative, sourceLibraryId, "Movies");

        // Act
        var result = _service.Read(c =>
        {
            foreach (var alt in c.LanguageAlternatives)
            {
                var m = alt.MirroredLibraries.FirstOrDefault(x => x.Id == mirror.Id);
                if (m != null)
                {
                    return (m, alt.Id);
                }
            }
            return ((LibraryMirror?)null, Guid.Empty);
        });

        // Assert
        result.Item1.Should().NotBeNull();
        result.Item1!.Id.Should().Be(mirror.Id);
        result.Item2.Should().Be(alternative.Id);
    }

    [Fact]
    public void Read_MirrorWithAlternative_NonExistentMirror_ReturnsNull()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = _service.Read(c =>
        {
            foreach (var alt in c.LanguageAlternatives)
            {
                var m = alt.MirroredLibraries.FirstOrDefault(x => x.Id == nonExistentId);
                if (m != null)
                {
                    return (m, alt.Id);
                }
            }
            return ((LibraryMirror?)null, Guid.Empty);
        });

        // Assert
        result.Item1.Should().BeNull();
    }

    #endregion

    #region Snapshot Isolation Tests

    [Fact]
    public void Update_ExternalReferenceAfterSave_DoesNotAffectConfig()
    {
        // Arrange - This tests that the double-clone mechanism works
        var newAlt = new LanguageAlternative
        {
            Id = Guid.NewGuid(),
            Name = "Portuguese",
            LanguageCode = "pt-BR"
        };

        // Act
        _service.Update(c => c.LanguageAlternatives.Add(newAlt));

        // Now modify the object we passed in
        newAlt.Name = "Modified After Save";

        // Assert - Config should have the original value
        var saved = _context.Configuration.LanguageAlternatives.First(a => a.Id == newAlt.Id);
        saved.Name.Should().Be("Portuguese",
            "modifications to objects after save should not affect config (double-clone)");
    }

    #endregion
}
