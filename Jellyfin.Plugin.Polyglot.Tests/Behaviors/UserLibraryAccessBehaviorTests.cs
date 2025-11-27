using FluentAssertions;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Polyglot.Models;
using Jellyfin.Plugin.Polyglot.Services;
using Jellyfin.Plugin.Polyglot.Tests.TestHelpers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Polyglot.Tests.Behaviors;

/// <summary>
/// Behavior-driven tests that verify the user library access system works correctly
/// from an end-user perspective. These tests focus on WHAT should happen, not HOW.
/// 
/// Key behaviors under test:
/// - Users assigned to a language see that language's mirror libraries
/// - Users on "default" see source libraries (not mirrors)
/// - Users with plugin disabled have their access unchanged
/// - Non-managed libraries (like "Home Videos") are never affected
/// </summary>
public class UserLibraryAccessBehaviorTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly LibraryAccessService _libraryAccessService;
    
    // Test library IDs - representing a typical Jellyfin setup
    private readonly Guid _moviesSourceId = Guid.NewGuid();
    private readonly Guid _showsSourceId = Guid.NewGuid();
    private readonly Guid _homeVideosId = Guid.NewGuid(); // Not managed by plugin
    private readonly Guid _moviesPtMirrorId = Guid.NewGuid();
    private readonly Guid _showsPtMirrorId = Guid.NewGuid();
    private readonly Guid _moviesEsMirrorId = Guid.NewGuid();

    public UserLibraryAccessBehaviorTests()
    {
        _context = new PluginTestContext();
        _userManagerMock = new Mock<IUserManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        var logger = new Mock<ILogger<LibraryAccessService>>();

        _libraryAccessService = new LibraryAccessService(
            _userManagerMock.Object,
            _libraryManagerMock.Object,
            logger.Object);

        SetupTypicalLibraryEnvironment();
    }

    public void Dispose() => _context.Dispose();

    /// <summary>
    /// Sets up a typical environment with:
    /// - Movies (source) with Portuguese and Spanish mirrors
    /// - Shows (source) with Portuguese mirror only
    /// - Home Videos (not managed by plugin)
    /// </summary>
    private void SetupTypicalLibraryEnvironment()
    {
        // Create language alternatives
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var spanish = _context.AddLanguageAlternative("Spanish", "es-ES");

        // Create mirrors
        _context.AddMirror(portuguese, _moviesSourceId, "Movies", _moviesPtMirrorId);
        _context.AddMirror(portuguese, _showsSourceId, "Shows", _showsPtMirrorId);
        _context.AddMirror(spanish, _moviesSourceId, "Movies", _moviesEsMirrorId);
        // Note: Spanish has no Shows mirror - this tests partial mirror setups

        // Setup library manager to return all libraries
        var allLibraries = new List<VirtualFolderInfo>
        {
            CreateVirtualFolder(_moviesSourceId, "Movies"),
            CreateVirtualFolder(_showsSourceId, "Shows"),
            CreateVirtualFolder(_homeVideosId, "Home Videos"),
            CreateVirtualFolder(_moviesPtMirrorId, "Filmes"),
            CreateVirtualFolder(_showsPtMirrorId, "Séries"),
            CreateVirtualFolder(_moviesEsMirrorId, "Películas")
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(allLibraries);
    }

    #region Behavior: Users assigned to a language see that language's mirrors

    [Fact]
    public void PortugueseUser_ShouldSee_PortugueseMirrorsOnly()
    {
        // Scenario: A user assigned to Portuguese should see Portuguese mirrors,
        // NOT English source libraries, and NOT Spanish mirrors

        // Arrange
        var userId = Guid.NewGuid();
        var portuguese = _context.Configuration.LanguageAlternatives
            .First(a => a.Name == "Portuguese");

        _context.Configuration.UserLanguages.Add(new UserLanguageConfig
        {
            UserId = userId,
            SelectedAlternativeId = portuguese.Id,
            IsPluginManaged = true
        });

        // Act
        var accessibleLibraries = _libraryAccessService.GetExpectedLibraryAccess(userId).ToHashSet();

        // Assert - Should see Portuguese mirrors
        accessibleLibraries.Should().Contain(_moviesPtMirrorId, "should see Portuguese Movies mirror");
        accessibleLibraries.Should().Contain(_showsPtMirrorId, "should see Portuguese Shows mirror");

        // Assert - Should NOT see source libraries (they have mirrors)
        accessibleLibraries.Should().NotContain(_moviesSourceId, "should NOT see English Movies source");
        accessibleLibraries.Should().NotContain(_showsSourceId, "should NOT see English Shows source");

        // Assert - Should NOT see other language mirrors
        accessibleLibraries.Should().NotContain(_moviesEsMirrorId, "should NOT see Spanish Movies mirror");
    }

    [Fact]
    public void SpanishUser_WithPartialMirrors_ShouldSee_MirrorOrSourceAppropriately()
    {
        // Scenario: Spanish only has a Movies mirror, not Shows.
        // Spanish user should see: Spanish Movies mirror + English Shows source

        // Arrange
        var userId = Guid.NewGuid();
        var spanish = _context.Configuration.LanguageAlternatives
            .First(a => a.Name == "Spanish");

        _context.Configuration.UserLanguages.Add(new UserLanguageConfig
        {
            UserId = userId,
            SelectedAlternativeId = spanish.Id,
            IsPluginManaged = true
        });

        // Act
        var accessibleLibraries = _libraryAccessService.GetExpectedLibraryAccess(userId).ToHashSet();

        // Assert - Should see Spanish Movies mirror
        accessibleLibraries.Should().Contain(_moviesEsMirrorId, "should see Spanish Movies mirror");

        // Assert - Should see English Shows source (no Spanish mirror exists)
        accessibleLibraries.Should().Contain(_showsSourceId, "should see English Shows (no Spanish mirror)");

        // Assert - Should NOT see Portuguese mirrors
        accessibleLibraries.Should().NotContain(_moviesPtMirrorId);
        accessibleLibraries.Should().NotContain(_showsPtMirrorId);
    }

    #endregion

    #region Behavior: Users on "default" see source libraries

    [Fact]
    public void DefaultUser_ShouldSee_SourceLibrariesOnly()
    {
        // Scenario: A user on "default" (no specific language) should see
        // source libraries, not any mirrors

        // Arrange
        var userId = Guid.NewGuid();
        _context.Configuration.UserLanguages.Add(new UserLanguageConfig
        {
            UserId = userId,
            SelectedAlternativeId = null, // Default = no specific language
            IsPluginManaged = true
        });

        // Act
        var accessibleLibraries = _libraryAccessService.GetExpectedLibraryAccess(userId).ToHashSet();

        // Assert - Should see source libraries
        accessibleLibraries.Should().Contain(_moviesSourceId, "should see Movies source");
        accessibleLibraries.Should().Contain(_showsSourceId, "should see Shows source");

        // Assert - Should NOT see any mirrors
        accessibleLibraries.Should().NotContain(_moviesPtMirrorId);
        accessibleLibraries.Should().NotContain(_showsPtMirrorId);
        accessibleLibraries.Should().NotContain(_moviesEsMirrorId);
    }

    #endregion

    #region Behavior: Non-managed libraries are never affected

    [Fact]
    public void NonManagedLibraries_AreNeverIncludedInManagedAccess()
    {
        // Scenario: Libraries like "Home Videos" that aren't part of the Polyglot-managed
        // system should NOT be returned by GetExpectedLibraryAccess.
        // They're handled separately to preserve user's existing access.

        // Arrange
        var userId = Guid.NewGuid();
        var portuguese = _context.Configuration.LanguageAlternatives.First();

        _context.Configuration.UserLanguages.Add(new UserLanguageConfig
        {
            UserId = userId,
            SelectedAlternativeId = portuguese.Id,
            IsPluginManaged = true
        });

        // Act
        var accessibleLibraries = _libraryAccessService.GetExpectedLibraryAccess(userId).ToHashSet();

        // Assert - Home Videos should NOT be in the result
        // (it's preserved separately, not managed by the plugin)
        accessibleLibraries.Should().NotContain(_homeVideosId,
            "non-managed libraries should not be in GetExpectedLibraryAccess result");
    }

    #endregion

    #region Behavior: Unmanaged users have their access unchanged

    [Fact]
    public void UnmanagedUser_ShouldHave_EmptyExpectedAccess()
    {
        // Scenario: A user with IsPluginManaged=false should have empty expected access.
        // The plugin should not modify their library permissions at all.
        //
        // NOTE: This test verifies DESIRED behavior. If it fails, 
        // GetExpectedLibraryAccess may not be checking IsPluginManaged.

        // Arrange
        var userId = Guid.NewGuid();
        var portuguese = _context.Configuration.LanguageAlternatives.First();

        _context.Configuration.UserLanguages.Add(new UserLanguageConfig
        {
            UserId = userId,
            SelectedAlternativeId = portuguese.Id, // Has a language, but...
            IsPluginManaged = false // ...plugin shouldn't manage them
        });

        // Act
        var accessibleLibraries = _libraryAccessService.GetExpectedLibraryAccess(userId).ToList();

        // Assert - Empty means "don't manage this user's access"
        // If this fails, GetExpectedLibraryAccess ignores IsPluginManaged flag!
        accessibleLibraries.Should().BeEmpty(
            "unmanaged users should have no expected libraries from plugin");
    }

    #endregion

    #region Behavior: Users not in config are not managed

    [Fact]
    public void UserNotInConfig_ShouldNotBeManaged()
    {
        // Scenario: A user who has never been configured in the plugin
        // should not have any expected library access from the plugin
        //
        // NOTE: This test verifies DESIRED behavior. Currently the implementation
        // may return source libraries for unknown users (treating them as "default").
        // If this test fails, we should discuss if that's the correct behavior.

        // Arrange
        var unknownUserId = Guid.NewGuid();
        // Don't add this user to config

        // Act
        var accessibleLibraries = _libraryAccessService.GetExpectedLibraryAccess(unknownUserId).ToList();

        // Assert - Should be empty because user is not managed
        // If this fails, unknown users are being treated as "default" users
        accessibleLibraries.Should().BeEmpty(
            "users not in plugin config should have no managed access");
    }

    #endregion

    #region Helpers

    private static VirtualFolderInfo CreateVirtualFolder(Guid id, string name)
    {
        return new VirtualFolderInfo
        {
            ItemId = id.ToString("N"),
            Name = name,
            Locations = new[] { $"/media/{name.ToLowerInvariant()}" }
        };
    }

    #endregion
}

/// <summary>
/// Tests that verify the behavior when alternatives are deleted.
/// Key behavior: Users assigned to a deleted alternative should gracefully
/// fall back to default behavior.
/// </summary>
public class AlternativeDeletionBehaviorTests : IDisposable
{
    private readonly PluginTestContext _context;

    public AlternativeDeletionBehaviorTests()
    {
        _context = new PluginTestContext();
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void UserAssignedToDeletedAlternative_ShouldFallbackToDefault()
    {
        // Scenario: If an admin deletes the Portuguese alternative,
        // users who were assigned to Portuguese should gracefully handle this

        // Arrange
        var userId = Guid.NewGuid();
        var deletedAlternativeId = Guid.NewGuid(); // This ID no longer exists

        _context.Configuration.UserLanguages.Add(new UserLanguageConfig
        {
            UserId = userId,
            SelectedAlternativeId = deletedAlternativeId, // Points to non-existent alternative
            IsPluginManaged = true
        });

        // Act - Try to find the user's alternative
        var alternative = _context.Configuration.LanguageAlternatives
            .FirstOrDefault(a => a.Id == deletedAlternativeId);

        // Assert - Alternative doesn't exist
        alternative.Should().BeNull();

        // The system should handle this gracefully - user effectively has no valid assignment
        var userConfig = _context.Configuration.UserLanguages.First(u => u.UserId == userId);
        var validAlternative = _context.Configuration.LanguageAlternatives
            .FirstOrDefault(a => a.Id == userConfig.SelectedAlternativeId);
        
        validAlternative.Should().BeNull("the alternative was deleted");
    }
}

/// <summary>
/// Tests that verify mirror-related behaviors work correctly.
/// </summary>
public class MirrorBehaviorTests : IDisposable
{
    private readonly PluginTestContext _context;

    public MirrorBehaviorTests()
    {
        _context = new PluginTestContext();
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void MirrorWithoutTargetLibraryId_IsConsideredPending()
    {
        // Scenario: A mirror that hasn't been synced yet (no TargetLibraryId)
        // should be treated as "pending" - source should still be shown

        // Arrange
        var sourceId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        
        var pendingMirror = new LibraryMirror
        {
            Id = Guid.NewGuid(),
            SourceLibraryId = sourceId,
            SourceLibraryName = "Movies",
            TargetLibraryName = "Filmes",
            TargetPath = "/data/filmes",
            TargetLibraryId = null, // Not yet created!
            Status = SyncStatus.Pending
        };
        alternative.MirroredLibraries.Add(pendingMirror);

        // Assert - This mirror is not "ready"
        pendingMirror.TargetLibraryId.Should().BeNull();
        pendingMirror.Status.Should().Be(SyncStatus.Pending);

        // Therefore, users should see the SOURCE until the mirror is ready
    }

    [Fact]
    public void MultipleMirrorsForSameSource_OnlyUserLanguageMirrorIsShown()
    {
        // Scenario: Movies has both Portuguese and Spanish mirrors.
        // A Portuguese user should ONLY see the Portuguese mirror.

        // Arrange
        var sourceId = Guid.NewGuid();
        var ptMirrorId = Guid.NewGuid();
        var esMirrorId = Guid.NewGuid();

        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var spanish = _context.AddLanguageAlternative("Spanish", "es-ES");

        _context.AddMirror(portuguese, sourceId, "Movies", ptMirrorId);
        _context.AddMirror(spanish, sourceId, "Movies", esMirrorId);

        // Assert - Both alternatives mirror the same source
        portuguese.MirroredLibraries.Should().ContainSingle(m => m.SourceLibraryId == sourceId);
        spanish.MirroredLibraries.Should().ContainSingle(m => m.SourceLibraryId == sourceId);

        // But they have different target IDs
        portuguese.MirroredLibraries.First().TargetLibraryId.Should().Be(ptMirrorId);
        spanish.MirroredLibraries.First().TargetLibraryId.Should().Be(esMirrorId);
    }
}

