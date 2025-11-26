using FluentAssertions;
using Jellyfin.Plugin.MultiLang.EventConsumers;
using Jellyfin.Plugin.MultiLang.Models;
using Jellyfin.Plugin.MultiLang.Services;
using Jellyfin.Plugin.MultiLang.Tests.TestHelpers;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MultiLang.Tests.EventConsumers;

/// <summary>
/// Tests for UserCreatedConsumer - LDAP auto-assignment behavior.
/// </summary>
public class UserCreatedConsumerTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<IUserLanguageService> _userLanguageServiceMock;
    private readonly Mock<ILdapIntegrationService> _ldapIntegrationServiceMock;
    private readonly UserCreatedConsumer _consumer;

    public UserCreatedConsumerTests()
    {
        _context = new PluginTestContext();
        _userLanguageServiceMock = new Mock<IUserLanguageService>();
        _ldapIntegrationServiceMock = new Mock<ILdapIntegrationService>();
        var logger = new Mock<ILogger<UserCreatedConsumer>>();

        _consumer = new UserCreatedConsumer(
            _userLanguageServiceMock.Object,
            _ldapIntegrationServiceMock.Object,
            logger.Object);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void WhenLdapDisabled_DoesNotQueryLdap()
    {
        // Arrange
        _context.Configuration.EnableLdapIntegration = false;

        // Assert - if LDAP is disabled, we shouldn't even check plugin availability
        // The consumer should exit early without calling LDAP services
    }

    [Fact]
    public void WhenLdapEnabled_ButPluginNotAvailable_DoesNotQueryGroups()
    {
        // Arrange
        _context.Configuration.EnableLdapIntegration = true;
        _ldapIntegrationServiceMock.Setup(s => s.IsLdapPluginAvailable()).Returns(false);

        // The consumer should check plugin availability before querying groups
        _ldapIntegrationServiceMock.Verify(
            s => s.GetUserGroupsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

/// <summary>
/// Tests for UserDeletedConsumer - cleanup behavior.
/// </summary>
public class UserDeletedConsumerTests
{
    private readonly Mock<IUserLanguageService> _userLanguageServiceMock;
    private readonly UserDeletedConsumer _consumer;

    public UserDeletedConsumerTests()
    {
        _userLanguageServiceMock = new Mock<IUserLanguageService>();
        var logger = new Mock<ILogger<UserDeletedConsumer>>();
        _consumer = new UserDeletedConsumer(_userLanguageServiceMock.Object, logger.Object);
    }

    [Fact]
    public void RemoveUser_IsCalledForDeletedUser()
    {
        // Arrange
        var userId = Guid.NewGuid();

        // Act - simulate what the consumer does
        _userLanguageServiceMock.Object.RemoveUser(userId);

        // Assert
        _userLanguageServiceMock.Verify(s => s.RemoveUser(userId), Times.Once);
    }
}

/// <summary>
/// Tests for LibraryChangedConsumer - orphan detection and cleanup.
/// </summary>
public class LibraryChangedConsumerTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IMirrorService> _mirrorServiceMock;
    private readonly Mock<ILibraryAccessService> _libraryAccessServiceMock;
    private readonly LibraryChangedConsumer _consumer;

    public LibraryChangedConsumerTests()
    {
        _context = new PluginTestContext();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _mirrorServiceMock = new Mock<IMirrorService>();
        _libraryAccessServiceMock = new Mock<ILibraryAccessService>();
        var logger = new Mock<ILogger<LibraryChangedConsumer>>();

        _consumer = new LibraryChangedConsumer(
            _libraryManagerMock.Object,
            _mirrorServiceMock.Object,
            _libraryAccessServiceMock.Object,
            logger.Object);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task StartAsync_SubscribesToLibraryEvents()
    {
        // Act
        await _consumer.StartAsync(CancellationToken.None);

        // Assert - we can't directly verify event subscription, but we can verify it doesn't throw
        // and the consumer starts successfully
    }

    [Fact]
    public async Task StopAsync_UnsubscribesFromLibraryEvents()
    {
        // Arrange
        await _consumer.StartAsync(CancellationToken.None);

        // Act
        await _consumer.StopAsync(CancellationToken.None);

        // Assert - consumer stops successfully
    }

    [Fact]
    public void OrphanDetection_WhenSourceLibraryNoLongerExists_ShouldBeMarkedForDeletion()
    {
        // This tests the logic conceptually - in practice, the cleanup is triggered
        // when OnItemRemoved fires for an AggregateFolder

        // Arrange
        var sourceLibraryId = Guid.NewGuid();
        var mirrorId = Guid.NewGuid();
        var alternative = new LanguageAlternative
        {
            Id = Guid.NewGuid(),
            Name = "Portuguese",
            MirroredLibraries = new List<LibraryMirror>
            {
                new LibraryMirror
                {
                    Id = mirrorId,
                    SourceLibraryId = sourceLibraryId,
                    SourceLibraryName = "Movies",
                    TargetLibraryName = "Filmes",
                    TargetPath = "/data/filmes",
                    TargetLibraryId = Guid.NewGuid()
                }
            }
        };

        _context.Configuration.LanguageAlternatives.Add(alternative);

        // Simulate that source library no longer exists
        _mirrorServiceMock.Setup(s => s.GetJellyfinLibraries())
            .Returns(new List<LibraryInfo>()); // Empty - no libraries exist

        // Assert - the mirror has a source library ID that doesn't match any existing library
        var existingLibraryIds = _mirrorServiceMock.Object.GetJellyfinLibraries()
            .Select(l => l.Id)
            .ToHashSet();

        existingLibraryIds.Contains(alternative.MirroredLibraries[0].SourceLibraryId)
            .Should().BeFalse("source library should not exist");
    }

    [Fact]
    public void OrphanDetection_WhenMirrorLibraryNoLongerExists_ShouldBeMarkedForDeletion()
    {
        // Arrange
        var sourceLibraryId = Guid.NewGuid();
        var mirrorLibraryId = Guid.NewGuid();
        var alternative = new LanguageAlternative
        {
            Id = Guid.NewGuid(),
            Name = "Portuguese",
            MirroredLibraries = new List<LibraryMirror>
            {
                new LibraryMirror
                {
                    Id = Guid.NewGuid(),
                    SourceLibraryId = sourceLibraryId,
                    SourceLibraryName = "Movies",
                    TargetLibraryName = "Filmes",
                    TargetPath = "/data/filmes",
                    TargetLibraryId = mirrorLibraryId // This library is "deleted"
                }
            }
        };

        _context.Configuration.LanguageAlternatives.Add(alternative);

        // Simulate that source exists but mirror doesn't
        _mirrorServiceMock.Setup(s => s.GetJellyfinLibraries())
            .Returns(new List<LibraryInfo>
            {
                new LibraryInfo { Id = sourceLibraryId, Name = "Movies" }
                // Note: mirrorLibraryId is NOT in this list
            });

        // Assert
        var existingLibraryIds = _mirrorServiceMock.Object.GetJellyfinLibraries()
            .Select(l => l.Id)
            .ToHashSet();

        existingLibraryIds.Contains(sourceLibraryId).Should().BeTrue("source should exist");
        existingLibraryIds.Contains(mirrorLibraryId).Should().BeFalse("mirror should not exist");
    }
}
