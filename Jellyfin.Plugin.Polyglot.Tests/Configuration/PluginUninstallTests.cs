using FluentAssertions;
using Jellyfin.Plugin.Polyglot.Models;
using Jellyfin.Plugin.Polyglot.Services;
using Jellyfin.Plugin.Polyglot.Tests.TestHelpers;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Polyglot.Tests.Configuration;

/// <summary>
/// Tests for plugin uninstall cleanup behavior (OnUninstalling).
/// </summary>
public class PluginUninstallTests
{
    [Fact]
    public void OnUninstalling_CallsDeleteMirrorAsyncForEachMirror()
    {
        using var context = new PluginTestContext();

        // Arrange: create two alternatives with mirrors
        var alt1 = context.AddLanguageAlternative("Portuguese", "pt-BR", "/media/portuguese");
        var alt2 = context.AddLanguageAlternative("Spanish", "es-ES", "/media/spanish");

        var mirror1 = context.AddMirror(alt1, Guid.NewGuid(), "Movies", targetPath: "/media/portuguese/movies");
        var mirror2 = context.AddMirror(alt1, Guid.NewGuid(), "TV Shows", targetPath: "/media/portuguese/tv");
        var mirror3 = context.AddMirror(alt2, Guid.NewGuid(), "Movies", targetPath: "/media/spanish/movies");

        // Set up MirrorService mock to accept any delete call
        context.MirrorServiceMock
            .Setup(m => m.DeleteMirrorAsync(
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteMirrorResult { RemovedFromConfig = true });

        // Act
        context.PluginInstance.OnUninstalling();

        // Assert: DeleteMirrorAsync was called for each mirror with deleteLibrary=true, deleteFiles=true, forceConfigRemoval=true
        context.MirrorServiceMock.Verify(
            m => m.DeleteMirrorAsync(
                mirror1.Id,
                true,
                true,
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);

        context.MirrorServiceMock.Verify(
            m => m.DeleteMirrorAsync(
                mirror2.Id,
                true,
                true,
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);

        context.MirrorServiceMock.Verify(
            m => m.DeleteMirrorAsync(
                mirror3.Id,
                true,
                true,
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Total of 3 calls
        context.MirrorServiceMock.Verify(
            m => m.DeleteMirrorAsync(
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public void OnUninstalling_ClearsConfiguration()
    {
        using var context = new PluginTestContext();

        // Arrange: add some configuration data
        var alt = context.AddLanguageAlternative("Portuguese", "pt-BR", "/media/portuguese");
        context.AddMirror(alt, Guid.NewGuid(), "Movies");
        context.AddUserLanguage(Guid.NewGuid(), alt.Id);

        context.MirrorServiceMock
            .Setup(m => m.DeleteMirrorAsync(
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteMirrorResult { RemovedFromConfig = true });

        // Act
        context.PluginInstance.OnUninstalling();

        // Assert: configuration should be cleared
        context.Configuration.LanguageAlternatives.Should().BeEmpty();
        context.Configuration.UserLanguages.Should().BeEmpty();
    }

    [Fact]
    public void OnUninstalling_ContinuesOnDeleteFailure()
    {
        using var context = new PluginTestContext();

        // Arrange: add two mirrors
        var alt = context.AddLanguageAlternative("Portuguese", "pt-BR", "/media/portuguese");
        var mirror1 = context.AddMirror(alt, Guid.NewGuid(), "Movies");
        var mirror2 = context.AddMirror(alt, Guid.NewGuid(), "TV Shows");

        // First delete throws, second succeeds
        context.MirrorServiceMock
            .Setup(m => m.DeleteMirrorAsync(
                mirror1.Id,
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Delete failed"));

        context.MirrorServiceMock
            .Setup(m => m.DeleteMirrorAsync(
                mirror2.Id,
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteMirrorResult { RemovedFromConfig = true });

        // Act - should not throw
        var act = () => context.PluginInstance.OnUninstalling();
        act.Should().NotThrow();

        // Assert: both mirrors were attempted
        context.MirrorServiceMock.Verify(
            m => m.DeleteMirrorAsync(
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }
}
