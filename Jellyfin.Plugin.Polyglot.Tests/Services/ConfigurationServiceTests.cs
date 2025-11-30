using FluentAssertions;
using Jellyfin.Plugin.Polyglot.Models;
using Jellyfin.Plugin.Polyglot.Services;
using Jellyfin.Plugin.Polyglot.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Polyglot.Tests.Services;

/// <summary>
/// Tests for ConfigurationService that verify thread-safe configuration operations.
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

    #region GetAlternative - Deep Copy Behavior

    [Fact]
    public void GetAlternative_ReturnsDeepCopy_ModificationsDoNotAffectConfig()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");

        // Act - Get the alternative and modify it
        var retrieved = _service.GetAlternative(alternative.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Name = "Modified Name";
        retrieved.MirroredLibraries.Add(new LibraryMirror { Id = Guid.NewGuid() });

        // Assert - Original config should be unchanged
        var original = _context.Configuration.LanguageAlternatives.First(a => a.Id == alternative.Id);
        original.Name.Should().Be("Portuguese", "modifications to returned copy should not affect config");
        original.MirroredLibraries.Should().BeEmpty("adding to returned copy should not affect config");
    }

    [Fact]
    public void GetAlternative_NonExistent_ReturnsNull()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = _service.GetAlternative(nonExistentId);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetAlternatives - Deep Copy Behavior

    [Fact]
    public void GetAlternatives_ReturnsDeepCopies()
    {
        // Arrange
        _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.AddLanguageAlternative("Spanish", "es-ES");

        // Act
        var alternatives = _service.GetAlternatives();

        // Assert
        alternatives.Should().HaveCount(2);

        // Modify returned list
        alternatives.ToList().ForEach(a => a.Name = "Modified");

        // Config should be unchanged
        _context.Configuration.LanguageAlternatives
            .Any(a => a.Name == "Modified")
            .Should().BeFalse("modifications to returned copies should not affect config");
    }

    #endregion

    #region GetMirror - Deep Copy Behavior

    [Fact]
    public void GetMirror_ReturnsDeepCopy_ModificationsDoNotAffectConfig()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        var mirror = _context.AddMirror(alternative, sourceLibraryId, "Movies");

        // Act - Get the mirror and modify it
        var retrieved = _service.GetMirror(mirror.Id);
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

    #endregion

    #region AddAlternative - Atomic Duplicate Check

    [Fact]
    public void AddAlternative_DuplicateName_ReturnsFalse()
    {
        // Arrange
        _context.AddLanguageAlternative("Portuguese", "pt-BR");

        var duplicate = new LanguageAlternative
        {
            Id = Guid.NewGuid(),
            Name = "Portuguese", // Same name (case-insensitive)
            LanguageCode = "pt-PT"
        };

        // Act
        var result = _service.AddAlternative(duplicate);

        // Assert
        result.Should().BeFalse("should reject duplicate name");
        _context.Configuration.LanguageAlternatives.Should().HaveCount(1);
    }

    [Fact]
    public void AddAlternative_DuplicateNameCaseInsensitive_ReturnsFalse()
    {
        // Arrange
        _context.AddLanguageAlternative("Portuguese", "pt-BR");

        var duplicate = new LanguageAlternative
        {
            Id = Guid.NewGuid(),
            Name = "PORTUGUESE", // Different case
            LanguageCode = "pt-PT"
        };

        // Act
        var result = _service.AddAlternative(duplicate);

        // Assert
        result.Should().BeFalse("should reject duplicate name case-insensitively");
    }

    [Fact]
    public void AddAlternative_UniqueName_ReturnsTrue()
    {
        // Arrange
        var alternative = new LanguageAlternative
        {
            Id = Guid.NewGuid(),
            Name = "Portuguese",
            LanguageCode = "pt-BR"
        };

        // Act
        var result = _service.AddAlternative(alternative);

        // Assert
        result.Should().BeTrue();
        _context.Configuration.LanguageAlternatives.Should().Contain(a => a.Id == alternative.Id);
    }

    #endregion

    #region RemoveAlternative - Dangling Reference Cleanup

    [Fact]
    public void RemoveAlternative_ClearsDefaultLanguageAlternativeId_WhenDeletingDefault()
    {
        // Arrange - Set up an alternative as the default
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.Configuration.DefaultLanguageAlternativeId = alternative.Id;

        // Act
        var result = _service.RemoveAlternative(alternative.Id);

        // Assert
        result.Should().BeTrue();
        _context.Configuration.DefaultLanguageAlternativeId.Should().BeNull(
            "DefaultLanguageAlternativeId should be cleared when the default alternative is deleted");
    }

    [Fact]
    public void RemoveAlternative_PreservesDefaultLanguageAlternativeId_WhenDeletingOther()
    {
        // Arrange - Set up two alternatives, one as default
        var defaultAlt = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var otherAlt = _context.AddLanguageAlternative("Spanish", "es-ES");
        _context.Configuration.DefaultLanguageAlternativeId = defaultAlt.Id;

        // Act - Delete the non-default one
        var result = _service.RemoveAlternative(otherAlt.Id);

        // Assert
        result.Should().BeTrue();
        _context.Configuration.DefaultLanguageAlternativeId.Should().Be(defaultAlt.Id,
            "DefaultLanguageAlternativeId should be preserved when deleting a non-default alternative");
    }

    [Fact]
    public void RemoveAlternative_RemovesLdapMappings_ThatReferenceDeletedAlternative()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var otherAlt = _context.AddLanguageAlternative("Spanish", "es-ES");

        // Add LDAP mappings for both alternatives
        _context.Configuration.LdapGroupMappings.Add(new LdapGroupMapping
        {
            Id = Guid.NewGuid(),
            LdapGroupDn = "CN=Portuguese,DC=test",
            LanguageAlternativeId = alternative.Id
        });
        _context.Configuration.LdapGroupMappings.Add(new LdapGroupMapping
        {
            Id = Guid.NewGuid(),
            LdapGroupDn = "CN=Spanish,DC=test",
            LanguageAlternativeId = otherAlt.Id
        });

        // Act
        var result = _service.RemoveAlternative(alternative.Id);

        // Assert
        result.Should().BeTrue();
        _context.Configuration.LdapGroupMappings.Should().HaveCount(1,
            "LDAP mapping for deleted alternative should be removed");
        _context.Configuration.LdapGroupMappings.First().LanguageAlternativeId.Should().Be(otherAlt.Id,
            "LDAP mapping for other alternative should remain");
    }

    #endregion

    #region TryRemoveAlternativeAtomic - Dangling Reference Cleanup

    [Fact]
    public void TryRemoveAlternativeAtomic_ClearsDefaultLanguageAlternativeId()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.Configuration.DefaultLanguageAlternativeId = alternative.Id;

        // Act
        var result = _service.TryRemoveAlternativeAtomic(alternative.Id, new HashSet<Guid>());

        // Assert
        result.Success.Should().BeTrue();
        _context.Configuration.DefaultLanguageAlternativeId.Should().BeNull(
            "DefaultLanguageAlternativeId should be cleared when using atomic remove");
    }

    [Fact]
    public void TryRemoveAlternativeAtomic_RejectsWhenNewMirrorsAdded()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        var mirror = _context.AddMirror(alternative, sourceLibraryId, "Movies");

        // Expected mirrors is empty (simulating deletion started before mirror was added)
        var expectedMirrorIds = new HashSet<Guid>();

        // Act
        var result = _service.TryRemoveAlternativeAtomic(alternative.Id, expectedMirrorIds);

        // Assert
        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(RemoveAlternativeFailureReason.NewMirrorsAdded);
        result.UnexpectedMirrorIds.Should().Contain(mirror.Id);
        _context.Configuration.LanguageAlternatives.Should().Contain(a => a.Id == alternative.Id,
            "alternative should not be removed when new mirrors detected");
    }

    [Fact]
    public void TryRemoveAlternativeAtomic_SucceedsWhenMirrorsMatch()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        var mirror = _context.AddMirror(alternative, sourceLibraryId, "Movies");

        var expectedMirrorIds = new HashSet<Guid> { mirror.Id };

        // Act
        var result = _service.TryRemoveAlternativeAtomic(alternative.Id, expectedMirrorIds);

        // Assert
        result.Success.Should().BeTrue();
        _context.Configuration.LanguageAlternatives.Should().NotContain(a => a.Id == alternative.Id);
    }

    #endregion

    #region AddMirror - Atomic Duplicate Check

    [Fact]
    public void AddMirror_DuplicateSourceLibrary_ReturnsFalse()
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
        var result = _service.AddMirror(alternative.Id, duplicateMirror);

        // Assert
        result.Should().BeFalse("should reject duplicate source library");
        var updatedAlt = _context.Configuration.LanguageAlternatives.First(a => a.Id == alternative.Id);
        updatedAlt.MirroredLibraries.Should().HaveCount(1);
    }

    [Fact]
    public void AddMirror_NonExistentAlternative_ReturnsFalse()
    {
        // Arrange
        var nonExistentAltId = Guid.NewGuid();
        var mirror = new LibraryMirror
        {
            Id = Guid.NewGuid(),
            SourceLibraryId = Guid.NewGuid()
        };

        // Act
        var result = _service.AddMirror(nonExistentAltId, mirror);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region UpdateMirror - Atomic Update

    [Fact]
    public void UpdateMirror_ExistingMirror_AppliesChanges()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        var mirror = _context.AddMirror(alternative, sourceLibraryId, "Movies");

        // Act
        var result = _service.UpdateMirror(mirror.Id, m =>
        {
            m.Status = SyncStatus.Error;
            m.LastSyncFileCount = 100;
        });

        // Assert
        result.Should().BeTrue();
        var updated = _context.Configuration.LanguageAlternatives
            .SelectMany(a => a.MirroredLibraries)
            .First(m => m.Id == mirror.Id);
        updated.Status.Should().Be(SyncStatus.Error);
        updated.LastSyncFileCount.Should().Be(100);
    }

    [Fact]
    public void UpdateMirror_NonExistentMirror_ReturnsFalse()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = _service.UpdateMirror(nonExistentId, m => m.Status = SyncStatus.Error);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region User Language Operations

    [Fact]
    public void UpdateOrCreateUserLanguage_NewUser_CreatesEntry()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var altId = _context.AddLanguageAlternative().Id;

        // Act
        var isNew = _service.UpdateOrCreateUserLanguage(userId, u =>
        {
            u.SelectedAlternativeId = altId;
            u.IsPluginManaged = true;
        });

        // Assert
        isNew.Should().BeTrue("should indicate new entry was created");
        _context.Configuration.UserLanguages.Should().Contain(u => u.UserId == userId);
    }

    [Fact]
    public void UpdateOrCreateUserLanguage_ExistingUser_UpdatesEntry()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var alt1 = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var alt2 = _context.AddLanguageAlternative("Spanish", "es-ES");
        _context.AddUserLanguage(userId, alt1.Id);

        // Act
        var isNew = _service.UpdateOrCreateUserLanguage(userId, u =>
        {
            u.SelectedAlternativeId = alt2.Id;
        });

        // Assert
        isNew.Should().BeFalse("should indicate existing entry was updated");
        var userConfig = _context.Configuration.UserLanguages.First(u => u.UserId == userId);
        userConfig.SelectedAlternativeId.Should().Be(alt2.Id);
    }

    [Fact]
    public void GetUserLanguage_ReturnsDeepCopy()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var altId = _context.AddLanguageAlternative().Id;
        _context.AddUserLanguage(userId, altId, manuallySet: true, setBy: "admin");

        // Act
        var retrieved = _service.GetUserLanguage(userId);
        retrieved.Should().NotBeNull();
        retrieved!.ManuallySet = false; // Modify the copy

        // Assert - Original should be unchanged
        var original = _context.Configuration.UserLanguages.First(u => u.UserId == userId);
        original.ManuallySet.Should().BeTrue();
    }

    #endregion

    #region LDAP Group Mapping Operations

    [Fact]
    public void AddLdapGroupMapping_DuplicateGroupDn_ReturnsFalse()
    {
        // Arrange
        var altId = _context.AddLanguageAlternative().Id;
        _context.Configuration.LdapGroupMappings.Add(new LdapGroupMapping
        {
            Id = Guid.NewGuid(),
            LdapGroupDn = "CN=Test,DC=example",
            LanguageAlternativeId = altId
        });

        var duplicate = new LdapGroupMapping
        {
            Id = Guid.NewGuid(),
            LdapGroupDn = "CN=Test,DC=example", // Same DN
            LanguageAlternativeId = altId
        };

        // Act
        var result = _service.AddLdapGroupMapping(duplicate);

        // Assert
        result.Should().BeFalse("should reject duplicate group DN");
    }

    [Fact]
    public void AddLdapGroupMapping_DuplicateGroupDnCaseInsensitive_ReturnsFalse()
    {
        // Arrange
        var altId = _context.AddLanguageAlternative().Id;
        _context.Configuration.LdapGroupMappings.Add(new LdapGroupMapping
        {
            Id = Guid.NewGuid(),
            LdapGroupDn = "CN=Test,DC=example",
            LanguageAlternativeId = altId
        });

        var duplicate = new LdapGroupMapping
        {
            Id = Guid.NewGuid(),
            LdapGroupDn = "cn=test,dc=EXAMPLE", // Different case
            LanguageAlternativeId = altId
        };

        // Act
        var result = _service.AddLdapGroupMapping(duplicate);

        // Assert
        result.Should().BeFalse("should reject duplicate group DN case-insensitively");
    }

    #endregion

    #region UpdateSettings

    [Fact]
    public void UpdateSettings_ActionOverload_AlwaysSaves()
    {
        // Arrange
        _context.Configuration.AutoManageNewUsers = false;

        // Act
        _service.UpdateSettings(config =>
        {
            config.AutoManageNewUsers = true;
        });

        // Assert
        _context.Configuration.AutoManageNewUsers.Should().BeTrue();
    }

    [Fact]
    public void UpdateSettings_FuncOverload_SavesOnlyWhenReturnsTrue()
    {
        // Arrange
        _context.Configuration.AutoManageNewUsers = false;

        // Act - Return false to skip saving (validation failure scenario)
        var result = _service.UpdateSettings(config =>
        {
            config.AutoManageNewUsers = true; // Would be set
            return false; // But don't save
        });

        // Assert
        result.Should().BeFalse();
        // Note: In the actual ConfigurationService, the change IS applied to the in-memory config
        // but SaveConfiguration is not called. Since we're using PluginTestContext,
        // the change persists in memory, but in production it wouldn't persist to disk.
    }

    #endregion

    #region ClearAllConfiguration

    [Fact]
    public void ClearAllConfiguration_RemovesAllData()
    {
        // Arrange
        var alt = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        _context.AddMirror(alt, sourceLibraryId, "Movies");
        _context.AddUserLanguage(Guid.NewGuid(), alt.Id);
        _context.Configuration.LdapGroupMappings.Add(new LdapGroupMapping
        {
            Id = Guid.NewGuid(),
            LdapGroupDn = "CN=Test,DC=example",
            LanguageAlternativeId = alt.Id
        });

        // Verify setup
        _context.Configuration.LanguageAlternatives.Should().NotBeEmpty();
        _context.Configuration.UserLanguages.Should().NotBeEmpty();
        _context.Configuration.LdapGroupMappings.Should().NotBeEmpty();

        // Act
        _service.ClearAllConfiguration();

        // Assert
        _context.Configuration.LanguageAlternatives.Should().BeEmpty();
        _context.Configuration.UserLanguages.Should().BeEmpty();
        _context.Configuration.LdapGroupMappings.Should().BeEmpty();
    }

    #endregion

    #region GetMirrorWithAlternative

    [Fact]
    public void GetMirrorWithAlternative_ExistingMirror_ReturnsMirrorAndAlternativeId()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        var mirror = _context.AddMirror(alternative, sourceLibraryId, "Movies");

        // Act
        var result = _service.GetMirrorWithAlternative(mirror.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Value.Mirror.Id.Should().Be(mirror.Id);
        result.Value.AlternativeId.Should().Be(alternative.Id);
    }

    [Fact]
    public void GetMirrorWithAlternative_NonExistentMirror_ReturnsNull()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = _service.GetMirrorWithAlternative(nonExistentId);

        // Assert
        result.Should().BeNull();
    }

    #endregion
}

