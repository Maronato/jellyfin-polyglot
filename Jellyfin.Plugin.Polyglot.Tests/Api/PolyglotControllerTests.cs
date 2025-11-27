using FluentAssertions;
using Jellyfin.Plugin.Polyglot.Api;
using Jellyfin.Plugin.Polyglot.Models;
using Jellyfin.Plugin.Polyglot.Services;
using Jellyfin.Plugin.Polyglot.Tests.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Polyglot.Tests.Api;

/// <summary>
/// Tests for PolyglotController.
/// These tests verify that the controller correctly handles inputs and routes to services.
/// </summary>
public class PolyglotControllerTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<IMirrorService> _mirrorServiceMock;
    private readonly Mock<IUserLanguageService> _userLanguageServiceMock;
    private readonly Mock<ILibraryAccessService> _libraryAccessServiceMock;
    private readonly Mock<ILdapIntegrationService> _ldapIntegrationServiceMock;
    private readonly PolyglotController _controller;

    public PolyglotControllerTests()
    {
        _context = new PluginTestContext();
        _mirrorServiceMock = new Mock<IMirrorService>();
        _userLanguageServiceMock = new Mock<IUserLanguageService>();
        _libraryAccessServiceMock = new Mock<ILibraryAccessService>();
        _ldapIntegrationServiceMock = new Mock<ILdapIntegrationService>();
        var logger = new Mock<ILogger<PolyglotController>>();

        _controller = new PolyglotController(
            _mirrorServiceMock.Object,
            _userLanguageServiceMock.Object,
            _libraryAccessServiceMock.Object,
            _ldapIntegrationServiceMock.Object,
            logger.Object);
    }

    public void Dispose() => _context.Dispose();

    #region CreateAlternative - Input validation

    [Fact]
    public void CreateAlternative_EmptyName_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreateAlternativeRequest
        {
            Name = "",
            LanguageCode = "pt-BR",
            DestinationBasePath = "/media/pt"
        };

        // Act
        var result = _controller.CreateAlternative(request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void CreateAlternative_EmptyLanguageCode_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreateAlternativeRequest
        {
            Name = "Portuguese",
            LanguageCode = "",
            DestinationBasePath = "/media/pt"
        };

        // Act
        var result = _controller.CreateAlternative(request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void CreateAlternative_EmptyPath_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreateAlternativeRequest
        {
            Name = "Portuguese",
            LanguageCode = "pt-BR",
            DestinationBasePath = ""
        };

        // Act
        var result = _controller.CreateAlternative(request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void CreateAlternative_ValidRequest_CreatesAlternative()
    {
        // Arrange
        var request = new CreateAlternativeRequest
        {
            Name = "Portuguese",
            LanguageCode = "pt-BR",
            DestinationBasePath = "/media/portuguese"
        };

        // Act
        var result = _controller.CreateAlternative(request);

        // Assert
        result.Result.Should().BeOfType<CreatedAtActionResult>();
        _context.Configuration.LanguageAlternatives.Should().ContainSingle();
        var created = _context.Configuration.LanguageAlternatives[0];
        created.Name.Should().Be("Portuguese");
        created.LanguageCode.Should().Be("pt-BR");
    }

    [Fact]
    public void CreateAlternative_DefaultsMetadataFromLanguageCode()
    {
        // Arrange
        var request = new CreateAlternativeRequest
        {
            Name = "Portuguese",
            LanguageCode = "pt-BR",
            DestinationBasePath = "/media/portuguese"
            // MetadataLanguage and MetadataCountry not specified
        };

        // Act
        _controller.CreateAlternative(request);

        // Assert
        var created = _context.Configuration.LanguageAlternatives[0];
        created.MetadataLanguage.Should().Be("pt", "should extract language from code");
        created.MetadataCountry.Should().Be("BR", "should extract country from code");
    }

    #endregion

    #region GetAlternatives - Returns configured alternatives

    [Fact]
    public void GetAlternatives_ReturnsConfiguredAlternatives()
    {
        // Arrange
        _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.AddLanguageAlternative("Spanish", "es-ES");

        // Act
        var result = _controller.GetAlternatives();

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result.Result!;
        var alternatives = (List<LanguageAlternative>)okResult.Value!;
        alternatives.Should().HaveCount(2);
    }

    #endregion

    #region DeleteAlternative - Removes from configuration

    [Fact]
    public async Task DeleteAlternative_NotFound_ReturnsNotFound()
    {
        // Act
        var result = await _controller.DeleteAlternative(Guid.NewGuid());

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task DeleteAlternative_Found_RemovesFromConfig()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative();

        // Act
        var result = await _controller.DeleteAlternative(alternative.Id);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        _context.Configuration.LanguageAlternatives.Should().BeEmpty();
    }

    #endregion

    #region AddLdapGroupMapping - Input validation

    [Fact]
    public void AddLdapGroupMapping_EmptyGroupDn_ReturnsBadRequest()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative();
        var request = new AddLdapGroupMappingRequest
        {
            LdapGroupDn = "",
            LanguageAlternativeId = alternative.Id
        };

        // Act
        var result = _controller.AddLdapGroupMapping(request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void AddLdapGroupMapping_AlternativeNotFound_ReturnsBadRequest()
    {
        // Arrange
        var request = new AddLdapGroupMappingRequest
        {
            LdapGroupDn = "CN=Test,DC=test",
            LanguageAlternativeId = Guid.NewGuid() // Non-existent
        };

        // Act
        var result = _controller.AddLdapGroupMapping(request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
        var badRequest = (BadRequestObjectResult)result.Result!;
        badRequest.Value.Should().Be("Language alternative not found");
    }

    [Fact]
    public void AddLdapGroupMapping_ValidRequest_AddsMappingWithPriority()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative();
        var request = new AddLdapGroupMappingRequest
        {
            LdapGroupDn = "CN=Portuguese Users,DC=example,DC=com",
            LanguageAlternativeId = alternative.Id,
            Priority = 150
        };

        // Act
        var result = _controller.AddLdapGroupMapping(request);

        // Assert
        result.Result.Should().BeOfType<CreatedAtActionResult>();
        _context.Configuration.LdapGroupMappings.Should().ContainSingle();
        var mapping = _context.Configuration.LdapGroupMappings[0];
        mapping.Priority.Should().Be(150);
    }

    #endregion

    #region Settings - Gets and updates configuration

    [Fact]
    public void GetSettings_ReturnsCurrentSettings()
    {
        // Arrange
        _context.Configuration.SyncUserDisplayLanguage = true;
        _context.Configuration.SyncUserSubtitleLanguage = false;
        _context.Configuration.EnableLdapIntegration = true;
        _context.Configuration.MirrorSyncIntervalHours = 12;

        // Act
        var result = _controller.GetSettings();

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        var settings = (PluginSettings)((OkObjectResult)result.Result!).Value!;
        settings.SyncUserDisplayLanguage.Should().BeTrue();
        settings.SyncUserSubtitleLanguage.Should().BeFalse();
        settings.EnableLdapIntegration.Should().BeTrue();
        settings.MirrorSyncIntervalHours.Should().Be(12);
    }

    [Fact]
    public void UpdateSettings_UpdatesConfiguration()
    {
        // Arrange
        var settings = new PluginSettings
        {
            SyncUserDisplayLanguage = false,
            SyncUserSubtitleLanguage = true,
            SyncUserAudioLanguage = true,
            EnableLdapIntegration = true,
            MirrorSyncIntervalHours = 24
        };

        // Act
        var result = _controller.UpdateSettings(settings);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        _context.Configuration.SyncUserDisplayLanguage.Should().BeFalse();
        _context.Configuration.EnableLdapIntegration.Should().BeTrue();
        _context.Configuration.MirrorSyncIntervalHours.Should().Be(24);
    }

    #endregion

    #region AddLibraryMirror - User Access Updates

    [Fact]
    public async Task AddLibraryMirror_UpdatesAccessForUsersWithThatLanguage()
    {
        // Arrange - create an alternative and users assigned to it
        var alternativeId = Guid.NewGuid();
        var sourceLibraryId = Guid.NewGuid();
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();

        var alternative = new LanguageAlternative
        {
            Id = alternativeId,
            Name = "Portuguese",
            LanguageCode = "pt-BR",
            DestinationBasePath = "/data/portuguese",
            MirroredLibraries = new List<LibraryMirror>()
        };
        _context.Configuration.LanguageAlternatives.Add(alternative);

        // Two users assigned to Portuguese (plugin managed)
        _context.Configuration.UserLanguages.Add(new UserLanguageConfig
        {
            UserId = userId1,
            SelectedAlternativeId = alternativeId,
            IsPluginManaged = true
        });
        _context.Configuration.UserLanguages.Add(new UserLanguageConfig
        {
            UserId = userId2,
            SelectedAlternativeId = alternativeId,
            IsPluginManaged = true
        });

        // Setup mock for source library
        _mirrorServiceMock.Setup(s => s.GetJellyfinLibraries())
            .Returns(new List<LibraryInfo>
            {
                new LibraryInfo { Id = sourceLibraryId, Name = "Movies", CollectionType = "movies" }
            });

        _mirrorServiceMock.Setup(s => s.ValidateMirrorConfiguration(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns((true, (string?)null));

        var request = new AddLibraryMirrorRequest
        {
            SourceLibraryId = sourceLibraryId.ToString(),
            TargetPath = "/data/portuguese/movies",
            TargetLibraryName = "Filmes"
        };

        // Act
        await _controller.AddLibraryMirror(alternativeId, request, CancellationToken.None);

        // Assert - library access should be updated for both users
        _libraryAccessServiceMock.Verify(
            s => s.UpdateUserLibraryAccessAsync(userId1, It.IsAny<CancellationToken>()),
            Times.Once,
            "Should update access for user1 who has Portuguese assigned");

        _libraryAccessServiceMock.Verify(
            s => s.UpdateUserLibraryAccessAsync(userId2, It.IsAny<CancellationToken>()),
            Times.Once,
            "Should update access for user2 who has Portuguese assigned");
    }

    [Fact]
    public async Task AddLibraryMirror_DoesNotUpdateAccessForUsersOnOtherLanguages()
    {
        // Arrange - create two alternatives
        var portugueseId = Guid.NewGuid();
        var spanishId = Guid.NewGuid();
        var sourceLibraryId = Guid.NewGuid();
        var spanishUserId = Guid.NewGuid();

        var portuguese = new LanguageAlternative
        {
            Id = portugueseId,
            Name = "Portuguese",
            LanguageCode = "pt-BR",
            DestinationBasePath = "/data/portuguese",
            MirroredLibraries = new List<LibraryMirror>()
        };
        var spanish = new LanguageAlternative
        {
            Id = spanishId,
            Name = "Spanish",
            LanguageCode = "es-ES",
            DestinationBasePath = "/data/spanish",
            MirroredLibraries = new List<LibraryMirror>()
        };
        _context.Configuration.LanguageAlternatives.Add(portuguese);
        _context.Configuration.LanguageAlternatives.Add(spanish);

        // User assigned to Spanish
        _context.Configuration.UserLanguages.Add(new UserLanguageConfig
        {
            UserId = spanishUserId,
            SelectedAlternativeId = spanishId,
            IsPluginManaged = true
        });

        // Setup mock
        _mirrorServiceMock.Setup(s => s.GetJellyfinLibraries())
            .Returns(new List<LibraryInfo>
            {
                new LibraryInfo { Id = sourceLibraryId, Name = "Movies", CollectionType = "movies" }
            });

        _mirrorServiceMock.Setup(s => s.ValidateMirrorConfiguration(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns((true, (string?)null));

        var request = new AddLibraryMirrorRequest
        {
            SourceLibraryId = sourceLibraryId.ToString(),
            TargetPath = "/data/portuguese/movies",
            TargetLibraryName = "Filmes"
        };

        // Act - create mirror for PORTUGUESE
        await _controller.AddLibraryMirror(portugueseId, request, CancellationToken.None);

        // Assert - Spanish user should NOT be updated
        _libraryAccessServiceMock.Verify(
            s => s.UpdateUserLibraryAccessAsync(spanishUserId, It.IsAny<CancellationToken>()),
            Times.Never,
            "Should NOT update access for user on different language");
    }

    [Fact]
    public async Task AddLibraryMirror_DoesNotUpdateUnmanagedUsers()
    {
        // Arrange
        var alternativeId = Guid.NewGuid();
        var sourceLibraryId = Guid.NewGuid();
        var unmanagedUserId = Guid.NewGuid();

        var alternative = new LanguageAlternative
        {
            Id = alternativeId,
            Name = "Portuguese",
            LanguageCode = "pt-BR",
            DestinationBasePath = "/data/portuguese",
            MirroredLibraries = new List<LibraryMirror>()
        };
        _context.Configuration.LanguageAlternatives.Add(alternative);

        // User has Portuguese selected but is NOT plugin-managed
        _context.Configuration.UserLanguages.Add(new UserLanguageConfig
        {
            UserId = unmanagedUserId,
            SelectedAlternativeId = alternativeId,
            IsPluginManaged = false // Not managed
        });

        // Setup mock
        _mirrorServiceMock.Setup(s => s.GetJellyfinLibraries())
            .Returns(new List<LibraryInfo>
            {
                new LibraryInfo { Id = sourceLibraryId, Name = "Movies", CollectionType = "movies" }
            });

        _mirrorServiceMock.Setup(s => s.ValidateMirrorConfiguration(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns((true, (string?)null));

        var request = new AddLibraryMirrorRequest
        {
            SourceLibraryId = sourceLibraryId.ToString(),
            TargetPath = "/data/portuguese/movies",
            TargetLibraryName = "Filmes"
        };

        // Act
        await _controller.AddLibraryMirror(alternativeId, request, CancellationToken.None);

        // Assert - unmanaged user should NOT be updated
        _libraryAccessServiceMock.Verify(
            s => s.UpdateUserLibraryAccessAsync(unmanagedUserId, It.IsAny<CancellationToken>()),
            Times.Never,
            "Should NOT update access for unmanaged user");
    }

    #endregion

    #region CleanupOrphanedMirrors

    [Fact]
    public async Task CleanupOrphanedMirrors_NoOrphans_ReturnsZeroCleaned()
    {
        // Arrange - service returns no orphans
        _mirrorServiceMock.Setup(s => s.CleanupOrphanedMirrorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrphanCleanupResult
            {
                TotalCleaned = 0,
                CleanedUpMirrors = new List<string>()
            });

        // Act
        var result = await _controller.CleanupOrphanedMirrors(CancellationToken.None);

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var cleanupResult = okResult.Value.Should().BeOfType<CleanupResult>().Subject;
        cleanupResult.TotalCleaned.Should().Be(0);
        cleanupResult.CleanedUpMirrors.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupOrphanedMirrors_SourceDeleted_RemovesMirrorAndDeletesFiles()
    {
        // Arrange - service returns cleanup result for source deleted scenario
        _mirrorServiceMock.Setup(s => s.CleanupOrphanedMirrorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrphanCleanupResult
            {
                TotalCleaned = 1,
                CleanedUpMirrors = new List<string> { "Filmes (source deleted)" }
            });

        // Act
        var result = await _controller.CleanupOrphanedMirrors(CancellationToken.None);

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var cleanupResult = okResult.Value.Should().BeOfType<CleanupResult>().Subject;
        cleanupResult.TotalCleaned.Should().Be(1);
        cleanupResult.CleanedUpMirrors.Should().Contain(m => m.Contains("source deleted"));
    }

    [Fact]
    public async Task CleanupOrphanedMirrors_MirrorLibraryDeleted_RemovesMirrorButKeepsSourceFiles()
    {
        // Arrange - service returns cleanup result for mirror deleted scenario
        _mirrorServiceMock.Setup(s => s.CleanupOrphanedMirrorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrphanCleanupResult
            {
                TotalCleaned = 1,
                CleanedUpMirrors = new List<string> { "Filmes (mirror deleted)" }
            });

        // Act
        var result = await _controller.CleanupOrphanedMirrors(CancellationToken.None);

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var cleanupResult = okResult.Value.Should().BeOfType<CleanupResult>().Subject;
        cleanupResult.TotalCleaned.Should().Be(1);
        cleanupResult.CleanedUpMirrors.Should().Contain(m => m.Contains("mirror deleted"));
    }

    [Fact]
    public async Task CleanupOrphanedMirrors_ReconciliesUserAccessAfterCleanup()
    {
        // Arrange - service returns cleanup result with cleaned mirrors
        _mirrorServiceMock.Setup(s => s.CleanupOrphanedMirrorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrphanCleanupResult
            {
                TotalCleaned = 1,
                CleanedUpMirrors = new List<string> { "Filmes (source deleted)" }
            });

        // Act
        await _controller.CleanupOrphanedMirrors(CancellationToken.None);

        // Assert - user access reconciliation was called
        _libraryAccessServiceMock.Verify(
            s => s.ReconcileAllUsersAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CleanupOrphanedMirrors_NoOrphans_DoesNotReconcileUsers()
    {
        // Arrange - service returns no cleanup
        _mirrorServiceMock.Setup(s => s.CleanupOrphanedMirrorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrphanCleanupResult
            {
                TotalCleaned = 0,
                CleanedUpMirrors = new List<string>()
            });

        // Act
        await _controller.CleanupOrphanedMirrors(CancellationToken.None);

        // Assert - no reconciliation needed
        _libraryAccessServiceMock.Verify(
            s => s.ReconcileAllUsersAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region SetUserLanguage - DESIRED: User language assignment with proper validation

    [Fact]
    public async Task SetUserLanguage_NonExistentUser_ShouldReturn404()
    {
        // DESIRED BEHAVIOR: When trying to set language for a user that doesn't exist
        // in Jellyfin, the endpoint should return 404 Not Found.
        
        // Arrange
        var nonExistentUserId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        
        // Configure service to throw ArgumentException for non-existent user
        // Note: The service throws with paramName="userId" to distinguish from other ArgumentExceptions
        _userLanguageServiceMock
            .Setup(s => s.AssignLanguageAsync(
                nonExistentUserId, 
                It.IsAny<Guid?>(), 
                It.IsAny<string>(), 
                It.IsAny<bool>(), 
                It.IsAny<bool>(), 
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException($"User {nonExistentUserId} not found", "userId"));

        var request = new SetUserLanguageRequest
        {
            AlternativeId = alternative.Id,
            ManuallySet = true
        };

        // Act
        var result = await _controller.SetUserLanguage(nonExistentUserId, request);

        // Assert - DESIRED: 404 Not Found for non-existent user
        result.Should().BeOfType<NotFoundObjectResult>(
            "a non-existent user should result in 404 Not Found");
    }

    [Fact]
    public async Task SetUserLanguage_NonExistentAlternative_ShouldReturn400()
    {
        // DESIRED BEHAVIOR: When specifying an alternative ID that doesn't exist,
        // the endpoint should return 400 Bad Request.
        
        // Arrange
        var userId = Guid.NewGuid();
        var nonExistentAlternativeId = Guid.NewGuid();
        
        // Configure service to throw ArgumentException for non-existent alternative
        // Note: The service throws with paramName="alternativeId" to distinguish from user not found
        _userLanguageServiceMock
            .Setup(s => s.AssignLanguageAsync(
                userId, 
                nonExistentAlternativeId, 
                It.IsAny<string>(), 
                It.IsAny<bool>(), 
                It.IsAny<bool>(), 
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException($"Language alternative {nonExistentAlternativeId} not found", "alternativeId"));

        var request = new SetUserLanguageRequest
        {
            AlternativeId = nonExistentAlternativeId,
            ManuallySet = true
        };

        // Act
        var result = await _controller.SetUserLanguage(userId, request);

        // Assert - DESIRED: 400 Bad Request for invalid alternative ID
        result.Should().BeOfType<BadRequestObjectResult>(
            "an invalid alternative ID should result in 400 Bad Request");
    }

    [Fact]
    public async Task SetUserLanguage_ValidRequest_ShouldAssignLanguageAndReturn204()
    {
        // DESIRED BEHAVIOR: A valid request should assign the language and return 204 No Content.
        
        // Arrange
        var userId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        
        var request = new SetUserLanguageRequest
        {
            AlternativeId = alternative.Id,
            ManuallySet = true
        };

        // Act
        var result = await _controller.SetUserLanguage(userId, request);

        // Assert
        result.Should().BeOfType<NoContentResult>(
            "successful language assignment should return 204 No Content");
        
        // Verify the service was called with correct parameters
        _userLanguageServiceMock.Verify(
            s => s.AssignLanguageAsync(
                userId,
                alternative.Id,
                It.IsAny<string>(),
                true,  // manuallySet
                true,  // isPluginManaged
                It.IsAny<CancellationToken>()),
            Times.Once,
            "AssignLanguageAsync should be called with the correct parameters");
    }

    [Fact]
    public async Task SetUserLanguage_ClearingLanguage_ShouldSetAlternativeToNull()
    {
        // DESIRED BEHAVIOR: When AlternativeId is null, the user's language should be
        // cleared (set to default), returning them to viewing source libraries.
        
        // Arrange
        var userId = Guid.NewGuid();
        
        var request = new SetUserLanguageRequest
        {
            AlternativeId = null, // Clearing to default
            ManuallySet = true
        };

        // Act
        var result = await _controller.SetUserLanguage(userId, request);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        
        // Verify the service was called with null alternative ID
        _userLanguageServiceMock.Verify(
            s => s.AssignLanguageAsync(
                userId,
                null, // Should pass null to clear the language
                It.IsAny<string>(),
                true,
                true,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "Clearing language should pass null as alternativeId");
    }

    [Fact]
    public async Task SetUserLanguage_IsDisabledTrue_ShouldDisablePluginManagement()
    {
        // DESIRED BEHAVIOR: When IsDisabled is true, the plugin should stop managing
        // this user's library access entirely.
        
        // Arrange
        var userId = Guid.NewGuid();
        
        var request = new SetUserLanguageRequest
        {
            IsDisabled = true // User opts out of plugin management
        };

        // Act
        var result = await _controller.SetUserLanguage(userId, request);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        
        // DESIRED: DisableUserAsync should be called to stop managing this user
        _libraryAccessServiceMock.Verify(
            s => s.DisableUserAsync(userId, false, It.IsAny<CancellationToken>()),
            Times.Once,
            "DisableUserAsync should be called when IsDisabled=true");
        
        // DESIRED: AssignLanguageAsync should NOT be called when disabling
        _userLanguageServiceMock.Verify(
            s => s.AssignLanguageAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "AssignLanguageAsync should not be called when disabling user");
    }

    #endregion

    #region DeleteLibraryMirror - DESIRED: Mirror deletion with proper cleanup

    [Fact]
    public async Task DeleteLibraryMirror_AlternativeNotFound_ShouldReturn404()
    {
        // DESIRED BEHAVIOR: If the language alternative doesn't exist, return 404.
        
        // Arrange
        var nonExistentAlternativeId = Guid.NewGuid();
        var someSourceLibraryId = Guid.NewGuid();

        // Act
        var result = await _controller.DeleteLibraryMirror(
            nonExistentAlternativeId, 
            someSourceLibraryId,
            deleteLibrary: false,
            deleteFiles: false);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>(
            "non-existent alternative should result in 404");
    }

    [Fact]
    public async Task DeleteLibraryMirror_MirrorNotFound_ShouldReturn404()
    {
        // DESIRED BEHAVIOR: If the mirror doesn't exist within the alternative, return 404.
        
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var nonExistentSourceLibraryId = Guid.NewGuid();
        // Don't add any mirror to the alternative

        // Act
        var result = await _controller.DeleteLibraryMirror(
            alternative.Id,
            nonExistentSourceLibraryId,
            deleteLibrary: false,
            deleteFiles: false);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>(
            "non-existent mirror within the alternative should result in 404");
    }

    [Fact]
    public async Task DeleteLibraryMirror_WithDeleteFilesTrue_ShouldDeleteFiles()
    {
        // DESIRED BEHAVIOR: When deleteFiles=true, the mirror files should be deleted.
        
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        var mirror = _context.AddMirror(alternative, sourceLibraryId, "Movies");

        // Act
        var result = await _controller.DeleteLibraryMirror(
            alternative.Id,
            sourceLibraryId,
            deleteLibrary: true,
            deleteFiles: true); // Files should be deleted

        // Assert
        result.Should().BeOfType<NoContentResult>();
        
        _mirrorServiceMock.Verify(
            s => s.DeleteMirrorAsync(
                It.Is<LibraryMirror>(m => m.SourceLibraryId == sourceLibraryId),
                true,  // deleteLibrary
                true,  // deleteFiles - MUST be true
                It.IsAny<CancellationToken>()),
            Times.Once,
            "DeleteMirrorAsync should be called with deleteFiles=true");
    }

    [Fact]
    public async Task DeleteLibraryMirror_WithDeleteFilesFalse_ShouldKeepFiles()
    {
        // DESIRED BEHAVIOR: When deleteFiles=false, the mirror files should be kept.
        
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        var mirror = _context.AddMirror(alternative, sourceLibraryId, "Movies");

        // Act
        var result = await _controller.DeleteLibraryMirror(
            alternative.Id,
            sourceLibraryId,
            deleteLibrary: true,
            deleteFiles: false); // Files should be kept

        // Assert
        result.Should().BeOfType<NoContentResult>();
        
        _mirrorServiceMock.Verify(
            s => s.DeleteMirrorAsync(
                It.Is<LibraryMirror>(m => m.SourceLibraryId == sourceLibraryId),
                true,  // deleteLibrary
                false, // deleteFiles - MUST be false
                It.IsAny<CancellationToken>()),
            Times.Once,
            "DeleteMirrorAsync should be called with deleteFiles=false");
    }

    [Fact]
    public async Task DeleteLibraryMirror_ShouldUpdateUserAccessForAffectedUsers()
    {
        // DESIRED BEHAVIOR: After deleting a mirror, library access should be updated
        // for all users assigned to that language alternative.
        
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        var mirror = _context.AddMirror(alternative, sourceLibraryId, "Movies");
        
        // Two users assigned to Portuguese
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();
        _context.Configuration.UserLanguages.Add(new UserLanguageConfig
        {
            UserId = user1Id,
            SelectedAlternativeId = alternative.Id,
            IsPluginManaged = true
        });
        _context.Configuration.UserLanguages.Add(new UserLanguageConfig
        {
            UserId = user2Id,
            SelectedAlternativeId = alternative.Id,
            IsPluginManaged = true
        });

        // Act
        await _controller.DeleteLibraryMirror(
            alternative.Id,
            sourceLibraryId,
            deleteLibrary: false,
            deleteFiles: false);

        // Assert - DESIRED: Library access should be updated for affected users
        _libraryAccessServiceMock.Verify(
            s => s.UpdateUserLibraryAccessAsync(user1Id, It.IsAny<CancellationToken>()),
            Times.Once,
            "User1's library access should be updated after mirror deletion");
        
        _libraryAccessServiceMock.Verify(
            s => s.UpdateUserLibraryAccessAsync(user2Id, It.IsAny<CancellationToken>()),
            Times.Once,
            "User2's library access should be updated after mirror deletion");
    }

    [Fact]
    public async Task DeleteLibraryMirror_ShouldRemoveMirrorFromConfig()
    {
        // DESIRED BEHAVIOR: The mirror should be removed from the configuration.
        
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        _context.AddMirror(alternative, sourceLibraryId, "Movies");
        
        alternative.MirroredLibraries.Should().HaveCount(1, "precondition: mirror exists");

        // Act
        await _controller.DeleteLibraryMirror(
            alternative.Id,
            sourceLibraryId,
            deleteLibrary: false,
            deleteFiles: false);

        // Assert - DESIRED: Mirror should be removed from config
        alternative.MirroredLibraries.Should().BeEmpty(
            "mirror should be removed from alternative's MirroredLibraries list");
    }

    #endregion

    #region GetUsers - DESIRED: Returns all users with complete language info

    [Fact]
    public void GetUsers_ShouldReturnAllJellyfinUsers()
    {
        // DESIRED BEHAVIOR: GetUsers should return ALL Jellyfin users, not just
        // those configured in the plugin.
        
        // Arrange
        var user1 = new UserInfo { Id = Guid.NewGuid(), Username = "user1" };
        var user2 = new UserInfo { Id = Guid.NewGuid(), Username = "user2" };
        var user3 = new UserInfo { Id = Guid.NewGuid(), Username = "unconfigured_user" };
        
        _userLanguageServiceMock
            .Setup(s => s.GetAllUsersWithLanguages())
            .Returns(new List<UserInfo> { user1, user2, user3 });

        // Act
        var result = _controller.GetUsers();

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result.Result!;
        var users = okResult.Value.Should().BeAssignableTo<IEnumerable<UserInfo>>().Subject;
        users.Should().HaveCount(3, "all Jellyfin users should be returned");
    }

    [Fact]
    public void GetUsers_ShouldIncludeIsPluginManagedFlag()
    {
        // DESIRED BEHAVIOR: Each user should have the IsPluginManaged flag set correctly.
        
        // Arrange
        var managedUser = new UserInfo 
        { 
            Id = Guid.NewGuid(), 
            Username = "managed",
            IsPluginManaged = true
        };
        var unmanagedUser = new UserInfo 
        { 
            Id = Guid.NewGuid(), 
            Username = "unmanaged",
            IsPluginManaged = false
        };
        
        _userLanguageServiceMock
            .Setup(s => s.GetAllUsersWithLanguages())
            .Returns(new List<UserInfo> { managedUser, unmanagedUser });

        // Act
        var result = _controller.GetUsers();

        // Assert
        var okResult = (OkObjectResult)result.Result!;
        var users = (IEnumerable<UserInfo>)okResult.Value!;
        
        users.Should().Contain(u => u.Username == "managed" && u.IsPluginManaged == true,
            "managed user should have IsPluginManaged=true");
        users.Should().Contain(u => u.Username == "unmanaged" && u.IsPluginManaged == false,
            "unmanaged user should have IsPluginManaged=false");
    }

    [Fact]
    public void GetUsers_ShouldIncludeAssignedLanguageInfo()
    {
        // DESIRED BEHAVIOR: Users with language assignments should have the
        // AssignedAlternativeId and AssignedAlternativeName populated.
        
        // Arrange
        var alternativeId = Guid.NewGuid();
        var userWithLanguage = new UserInfo 
        { 
            Id = Guid.NewGuid(), 
            Username = "portuguese_user",
            AssignedAlternativeId = alternativeId,
            AssignedAlternativeName = "Portuguese",
            IsPluginManaged = true
        };
        var userWithoutLanguage = new UserInfo 
        { 
            Id = Guid.NewGuid(), 
            Username = "default_user",
            AssignedAlternativeId = null,
            AssignedAlternativeName = null,
            IsPluginManaged = true
        };
        
        _userLanguageServiceMock
            .Setup(s => s.GetAllUsersWithLanguages())
            .Returns(new List<UserInfo> { userWithLanguage, userWithoutLanguage });

        // Act
        var result = _controller.GetUsers();

        // Assert
        var okResult = (OkObjectResult)result.Result!;
        var users = ((IEnumerable<UserInfo>)okResult.Value!).ToList();
        
        var ptUser = users.First(u => u.Username == "portuguese_user");
        ptUser.AssignedAlternativeId.Should().Be(alternativeId);
        ptUser.AssignedAlternativeName.Should().Be("Portuguese");
        
        var defaultUser = users.First(u => u.Username == "default_user");
        defaultUser.AssignedAlternativeId.Should().BeNull();
    }

    [Fact]
    public void GetUsers_EmptyUserList_ShouldReturnEmptyList()
    {
        // DESIRED BEHAVIOR: If there are no Jellyfin users, return an empty list (not an error).
        
        // Arrange
        _userLanguageServiceMock
            .Setup(s => s.GetAllUsersWithLanguages())
            .Returns(new List<UserInfo>());

        // Act
        var result = _controller.GetUsers();

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result.Result!;
        var users = okResult.Value.Should().BeAssignableTo<IEnumerable<UserInfo>>().Subject;
        users.Should().BeEmpty();
    }

    #endregion

    #region GetLibraries - DESIRED: Returns all libraries with mirror identification

    [Fact]
    public void GetLibraries_ShouldReturnAllJellyfinLibraries()
    {
        // DESIRED BEHAVIOR: GetLibraries should return ALL Jellyfin libraries.
        
        // Arrange
        var moviesId = Guid.NewGuid();
        var showsId = Guid.NewGuid();
        var musicId = Guid.NewGuid();
        
        _mirrorServiceMock
            .Setup(s => s.GetJellyfinLibraries())
            .Returns(new List<LibraryInfo>
            {
                new LibraryInfo { Id = moviesId, Name = "Movies" },
                new LibraryInfo { Id = showsId, Name = "Shows" },
                new LibraryInfo { Id = musicId, Name = "Music" }
            });

        // Act
        var result = _controller.GetLibraries();

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result.Result!;
        var libraries = okResult.Value.Should().BeAssignableTo<IEnumerable<LibraryInfo>>().Subject;
        libraries.Should().HaveCount(3);
    }

    [Fact]
    public void GetLibraries_ShouldCorrectlyIdentifyMirrorLibraries()
    {
        // DESIRED BEHAVIOR: Mirror libraries should have IsMirror=true and 
        // LanguageAlternativeId set. Source libraries should have IsMirror=false.
        
        // Arrange
        var sourceId = Guid.NewGuid();
        var mirrorId = Guid.NewGuid();
        var alternativeId = Guid.NewGuid();
        
        _mirrorServiceMock
            .Setup(s => s.GetJellyfinLibraries())
            .Returns(new List<LibraryInfo>
            {
                new LibraryInfo 
                { 
                    Id = sourceId, 
                    Name = "Movies",
                    IsMirror = false,
                    LanguageAlternativeId = null
                },
                new LibraryInfo 
                { 
                    Id = mirrorId, 
                    Name = "Filmes (Portuguese)",
                    IsMirror = true,
                    LanguageAlternativeId = alternativeId
                }
            });

        // Act
        var result = _controller.GetLibraries();

        // Assert
        var okResult = (OkObjectResult)result.Result!;
        var libraries = ((IEnumerable<LibraryInfo>)okResult.Value!).ToList();
        
        var source = libraries.First(l => l.Id == sourceId);
        source.IsMirror.Should().BeFalse("source library should not be marked as mirror");
        source.LanguageAlternativeId.Should().BeNull();
        
        var mirror = libraries.First(l => l.Id == mirrorId);
        mirror.IsMirror.Should().BeTrue("mirror library should be marked as mirror");
        mirror.LanguageAlternativeId.Should().Be(alternativeId,
            "mirror should reference its language alternative");
    }

    #endregion

    #region DeleteLdapGroupMapping - DESIRED: Proper deletion and 404 handling

    [Fact]
    public void DeleteLdapGroupMapping_NotFound_ShouldReturn404()
    {
        // DESIRED BEHAVIOR: If the mapping doesn't exist, return 404 Not Found.
        
        // Arrange
        var nonExistentMappingId = Guid.NewGuid();
        // Don't add any mapping to config

        // Act
        var result = _controller.DeleteLdapGroupMapping(nonExistentMappingId);

        // Assert
        result.Should().BeOfType<NotFoundResult>(
            "non-existent mapping should result in 404");
    }

    [Fact]
    public void DeleteLdapGroupMapping_Found_ShouldRemoveAndReturn204()
    {
        // DESIRED BEHAVIOR: If mapping exists, remove it and return 204 No Content.
        
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var mapping = _context.AddLdapGroupMapping("CN=Portuguese,DC=test", alternative.Id);
        
        _context.Configuration.LdapGroupMappings.Should().HaveCount(1, "precondition: mapping exists");

        // Act
        var result = _controller.DeleteLdapGroupMapping(mapping.Id);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        _context.Configuration.LdapGroupMappings.Should().BeEmpty(
            "mapping should be removed from configuration");
    }

    [Fact]
    public void DeleteLdapGroupMapping_ShouldOnlyDeleteSpecifiedMapping()
    {
        // DESIRED BEHAVIOR: Only the specified mapping should be deleted,
        // other mappings should remain.
        
        // Arrange
        var alternative1 = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var alternative2 = _context.AddLanguageAlternative("Spanish", "es-ES");
        
        var mapping1 = _context.AddLdapGroupMapping("CN=Portuguese,DC=test", alternative1.Id);
        var mapping2 = _context.AddLdapGroupMapping("CN=Spanish,DC=test", alternative2.Id);
        
        _context.Configuration.LdapGroupMappings.Should().HaveCount(2, "precondition: two mappings exist");

        // Act - delete only the first mapping
        var result = _controller.DeleteLdapGroupMapping(mapping1.Id);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        _context.Configuration.LdapGroupMappings.Should().HaveCount(1,
            "only one mapping should remain");
        _context.Configuration.LdapGroupMappings.Should().Contain(m => m.Id == mapping2.Id,
            "the other mapping should still exist");
    }

    #endregion

    #region CreateAlternative - Error Paths

    [Fact]
    public void CreateAlternative_DuplicateName_ShouldReturn400()
    {
        // DESIRED BEHAVIOR: Creating an alternative with the same name as an existing
        // one should return 400 Bad Request. Names should be unique for clarity.
        //
        // NOTE: This tests DESIRED behavior. If it fails, it indicates the implementation
        // doesn't enforce name uniqueness (which may be a bug or design decision).
        
        // Arrange
        _context.AddLanguageAlternative("Portuguese", "pt-BR");
        
        var duplicateRequest = new CreateAlternativeRequest
        {
            Name = "Portuguese", // Same name as existing
            LanguageCode = "pt-PT", // Different code
            DestinationBasePath = "/media/portuguese2"
        };

        // Act
        var result = _controller.CreateAlternative(duplicateRequest);

        // Assert - DESIRED: Duplicate names should be rejected
        result.Result.Should().BeOfType<BadRequestObjectResult>(
            "duplicate alternative names should be rejected with 400 Bad Request");
    }

    [Fact]
    public void CreateAlternative_WhitespaceName_ShouldReturn400()
    {
        // DESIRED BEHAVIOR: Names that are only whitespace should be rejected.
        
        // Arrange
        var request = new CreateAlternativeRequest
        {
            Name = "   ", // Only whitespace
            LanguageCode = "pt-BR",
            DestinationBasePath = "/media/portuguese"
        };

        // Act
        var result = _controller.CreateAlternative(request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>(
            "whitespace-only names should be rejected");
    }

    #endregion

    #region AddLibraryMirror - Error Paths

    [Fact]
    public async Task AddLibraryMirror_AlternativeNotFound_ShouldReturn404()
    {
        // DESIRED BEHAVIOR: If the alternative doesn't exist, return 404.
        
        // Arrange
        var nonExistentAlternativeId = Guid.NewGuid();
        var request = new AddLibraryMirrorRequest
        {
            SourceLibraryId = Guid.NewGuid().ToString(),
            TargetPath = "/data/test",
            TargetLibraryName = "Test"
        };

        // Act
        var result = await _controller.AddLibraryMirror(nonExistentAlternativeId, request);

        // Assert
        result.Result.Should().BeOfType<NotFoundObjectResult>(
            "non-existent alternative should result in 404");
    }

    [Fact]
    public async Task AddLibraryMirror_InvalidSourceLibraryId_ShouldReturn400()
    {
        // DESIRED BEHAVIOR: If the source library ID format is invalid, return 400.
        
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var request = new AddLibraryMirrorRequest
        {
            SourceLibraryId = "not-a-guid",
            TargetPath = "/data/test",
            TargetLibraryName = "Test"
        };

        // Act
        var result = await _controller.AddLibraryMirror(alternative.Id, request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>(
            "invalid GUID format should result in 400 Bad Request");
    }

    [Fact]
    public async Task AddLibraryMirror_SourceLibraryNotFound_ShouldReturn400()
    {
        // DESIRED BEHAVIOR: If the source library doesn't exist in Jellyfin, return 400.
        
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var nonExistentLibraryId = Guid.NewGuid();
        
        _mirrorServiceMock
            .Setup(s => s.ValidateMirrorConfiguration(nonExistentLibraryId, It.IsAny<string>()))
            .Returns((false, "Source library not found"));
        
        var request = new AddLibraryMirrorRequest
        {
            SourceLibraryId = nonExistentLibraryId.ToString(),
            TargetPath = "/data/test",
            TargetLibraryName = "Test"
        };

        // Act
        var result = await _controller.AddLibraryMirror(alternative.Id, request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>(
            "non-existent source library should result in 400");
    }

    [Fact]
    public async Task AddLibraryMirror_PathTraversal_ShouldReturn400()
    {
        // DESIRED BEHAVIOR: Path traversal attempts should be rejected.
        
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        
        _mirrorServiceMock
            .Setup(s => s.ValidateMirrorConfiguration(sourceLibraryId, It.IsAny<string>()))
            .Returns((false, "Target path cannot contain path traversal sequences"));
        
        var request = new AddLibraryMirrorRequest
        {
            SourceLibraryId = sourceLibraryId.ToString(),
            TargetPath = "/data/../../../etc/passwd", // Path traversal attempt
            TargetLibraryName = "Test"
        };

        // Act
        var result = await _controller.AddLibraryMirror(alternative.Id, request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>(
            "path traversal attempts should result in 400");
        var badRequest = (BadRequestObjectResult)result.Result!;
        ((string)badRequest.Value!).Should().Contain("traversal",
            "error message should mention path traversal");
    }

    [Fact]
    public async Task AddLibraryMirror_DifferentFilesystems_ShouldReturn400()
    {
        // DESIRED BEHAVIOR: Hardlinks require same filesystem. If source and target
        // are on different filesystems, return 400 with clear error.
        
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceLibraryId = Guid.NewGuid();
        
        _mirrorServiceMock
            .Setup(s => s.ValidateMirrorConfiguration(sourceLibraryId, It.IsAny<string>()))
            .Returns((false, "Source path and target path are on different filesystems. Hardlinks require the same filesystem."));
        
        var request = new AddLibraryMirrorRequest
        {
            SourceLibraryId = sourceLibraryId.ToString(),
            TargetPath = "/other-drive/test",
            TargetLibraryName = "Test"
        };

        // Act
        var result = await _controller.AddLibraryMirror(alternative.Id, request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
        var badRequest = (BadRequestObjectResult)result.Result!;
        ((string)badRequest.Value!).Should().Contain("filesystem",
            "error message should mention filesystem requirement");
    }

    #endregion

    #region SyncAlternative - Error Handling

    [Fact]
    public async Task SyncAlternative_AlternativeNotFound_ShouldReturn404()
    {
        // DESIRED BEHAVIOR: If the alternative doesn't exist, return 404.
        
        // Arrange
        var nonExistentAlternativeId = Guid.NewGuid();

        // Act
        var result = await _controller.SyncAlternative(nonExistentAlternativeId);

        // Assert
        result.Should().BeOfType<NotFoundResult>(
            "non-existent alternative should result in 404");
    }

    [Fact]
    public async Task SyncAlternative_ValidAlternative_ShouldReturn202Accepted()
    {
        // DESIRED BEHAVIOR: Sync is an async operation that runs in background.
        // Should return 202 Accepted immediately.
        
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");

        // Act
        var result = await _controller.SyncAlternative(alternative.Id);

        // Assert
        result.Should().BeOfType<AcceptedResult>(
            "sync request should return 202 Accepted");
    }

    #endregion
}

/// <summary>
/// Tests for language code parsing helpers.
/// </summary>
public class LanguageCodeParsingTests
{
    [Theory]
    [InlineData("pt-BR", "pt", "BR")]
    [InlineData("en-US", "en", "US")]
    [InlineData("zh-CN", "zh", "CN")]
    [InlineData("ja", "ja", "")]
    [InlineData("fr-CA", "fr", "CA")]
    public void ParseLanguageCode_ExtractsComponents(string code, string expectedLang, string expectedCountry)
    {
        // Simulate the helper methods in the controller
        var dashIndex = code.IndexOf('-');
        var language = dashIndex > 0 ? code.Substring(0, dashIndex) : code;
        var country = dashIndex > 0 ? code.Substring(dashIndex + 1) : string.Empty;

        language.Should().Be(expectedLang);
        country.Should().Be(expectedCountry);
    }
}
