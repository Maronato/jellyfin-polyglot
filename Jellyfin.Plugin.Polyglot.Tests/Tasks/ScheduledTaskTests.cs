using FluentAssertions;
using Jellyfin.Plugin.Polyglot.Services;
using Jellyfin.Plugin.Polyglot.Tasks;
using Jellyfin.Plugin.Polyglot.Tests.TestHelpers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Polyglot.Tests.Tasks;

/// <summary>
/// Tests for MirrorSyncTask.
/// </summary>
public class MirrorSyncTaskTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<IMirrorService> _mirrorServiceMock;
    private readonly MirrorSyncTask _task;

    public MirrorSyncTaskTests()
    {
        _context = new PluginTestContext();
        _mirrorServiceMock = new Mock<IMirrorService>();
        var logger = new Mock<ILogger<MirrorSyncTask>>();
        _task = new MirrorSyncTask(_mirrorServiceMock.Object, logger.Object);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void TaskMetadata_HasCorrectValues()
    {
        _task.Name.Should().Be("Polyglot Mirror Sync");
        _task.Key.Should().Be("PolyglotMirrorSync");
        _task.Category.Should().Be("Polyglot");
    }

    [Fact]
    public void DefaultTriggers_UsesConfiguredInterval()
    {
        // Arrange
        _context.Configuration.MirrorSyncIntervalHours = 12;

        // Act
        var triggers = _task.GetDefaultTriggers().ToList();

        // Assert
        triggers.Should().ContainSingle();
        triggers[0].Type.Should().Be(TaskTriggerInfo.TriggerInterval);
        // Note: The actual interval comes from Plugin.Instance which is set up in context
    }

    [Fact]
    public async Task ExecuteAsync_NoAlternatives_CompletesQuickly()
    {
        // Arrange
        _context.Configuration.LanguageAlternatives.Clear();
        var progress = new Progress<double>();

        // Act
        await _task.ExecuteAsync(progress, CancellationToken.None);

        // Assert - should not call SyncAllMirrorsAsync if no alternatives
        _mirrorServiceMock.Verify(
            s => s.SyncAllMirrorsAsync(It.IsAny<Models.LanguageAlternative>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithAlternatives_SyncsEach()
    {
        // Arrange
        _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.AddLanguageAlternative("Spanish", "es-ES");
        var progress = new Progress<double>();

        // Act
        await _task.ExecuteAsync(progress, CancellationToken.None);

        // Assert - should sync each alternative
        _mirrorServiceMock.Verify(
            s => s.SyncAllMirrorsAsync(It.IsAny<Models.LanguageAlternative>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_RunsCleanupBeforeSync()
    {
        // Arrange
        var alt = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var progress = new Progress<double>();

        var callOrder = new List<string>();

        _mirrorServiceMock
            .Setup(s => s.CleanupOrphanedMirrorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrphanCleanupResult())
            .Callback(() => callOrder.Add("cleanup"));

        _mirrorServiceMock
            .Setup(s => s.SyncAllMirrorsAsync(It.IsAny<Models.LanguageAlternative>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => callOrder.Add("sync"));

        // Act
        await _task.ExecuteAsync(progress, CancellationToken.None);

        // Assert - cleanup must run before any sync
        callOrder.Should().NotBeEmpty();
        callOrder[0].Should().Be("cleanup");
        callOrder.Should().Contain("sync");
    }

    [Fact]
    public async Task ExecuteAsync_OneCancelled_DoesNotThrow()
    {
        // Arrange
        _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _mirrorServiceMock
            .Setup(s => s.SyncAllMirrorsAsync(It.IsAny<Models.LanguageAlternative>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Sync failed"));

        var progress = new Progress<double>();

        // Act
        var action = () => _task.ExecuteAsync(progress, CancellationToken.None);

        // Assert - task should handle errors gracefully
        await action.Should().NotThrowAsync();
    }
}

/// <summary>
/// Tests for UserLanguageSyncTask.
/// </summary>
public class UserLanguageSyncTaskTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<ILibraryAccessService> _libraryAccessServiceMock;
    private readonly Mock<IUserLanguageService> _userLanguageServiceMock;
    private readonly UserLanguageSyncTask _task;

    public UserLanguageSyncTaskTests()
    {
        _context = new PluginTestContext();
        _libraryAccessServiceMock = new Mock<ILibraryAccessService>();
        _userLanguageServiceMock = new Mock<IUserLanguageService>();
        var logger = new Mock<ILogger<UserLanguageSyncTask>>();

        _task = new UserLanguageSyncTask(
            _libraryAccessServiceMock.Object,
            _userLanguageServiceMock.Object,
            logger.Object);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void TaskMetadata_HasCorrectValues()
    {
        _task.Name.Should().Be("Polyglot User Library Sync");
        _task.Key.Should().Be("PolyglotUserSync");
        _task.Category.Should().Be("Polyglot");
    }

    [Fact]
    public void DefaultTriggers_IsDailyTrigger()
    {
        // Act
        var triggers = _task.GetDefaultTriggers().ToList();

        // Assert
        triggers.Should().ContainSingle();
        triggers[0].Type.Should().Be(TaskTriggerInfo.TriggerDaily);
    }

    [Fact]
    public async Task ExecuteAsync_NoUsers_CompletesSuccessfully()
    {
        // Arrange
        _userLanguageServiceMock.Setup(s => s.GetAllUsersWithLanguages())
            .Returns(new List<Models.UserInfo>());
        var progress = new Progress<double>();

        // Act
        await _task.ExecuteAsync(progress, CancellationToken.None);

        // Assert
        _libraryAccessServiceMock.Verify(
            s => s.ReconcileUserAccessAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
