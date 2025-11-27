using FluentAssertions;
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
    public void OnUninstalling_RemovesMirrorLibrariesAndDirectories()
    {
        using var context = new PluginTestContext();

        // Arrange: create a temp base path for mirrors
        var basePath = Path.Combine(Path.GetTempPath(), "polyglot_uninstall_base_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(basePath);

        var alternative = context.AddLanguageAlternative("Portuguese", "pt-BR", basePath);

        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var targetDir = Path.Combine(basePath, "Movies (Portuguese)");
        Directory.CreateDirectory(targetDir);

        var mirror = context.AddMirror(
            alternative,
            sourceId,
            "Movies",
            targetLibraryId: targetId,
            targetPath: targetDir);

        // Expect the plugin to remove the Jellyfin virtual folder with refreshLibrary: true
        context.LibraryManagerMock
            .Setup(m => m.RemoveVirtualFolder(mirror.TargetLibraryName, true))
            .Returns(Task.CompletedTask)
            .Verifiable();

        // Act
        context.PluginInstance.OnUninstalling();

        // Assert: library removal was invoked
        context.LibraryManagerMock.Verify(
            m => m.RemoveVirtualFolder(mirror.TargetLibraryName, true),
            Times.Once);

        // Mirror directory should have been deleted
        Directory.Exists(targetDir).Should().BeFalse("mirror directories should be removed on uninstall");
    }

    [Fact]
    public void OnUninstalling_DoesNotDeleteDirectoriesOutsideBasePath()
    {
        using var context = new PluginTestContext();

        // Arrange: base path and an external directory not under that base
        var basePath = Path.Combine(Path.GetTempPath(), "polyglot_uninstall_base_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(basePath);

        var externalDir = Path.Combine(Path.GetTempPath(), "polyglot_uninstall_external_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalDir);

        var alternative = context.AddLanguageAlternative("Portuguese", "pt-BR", basePath);

        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        // TargetPath is outside the configured base path
        context.AddMirror(
            alternative,
            sourceId,
            "Movies",
            targetLibraryId: targetId,
            targetPath: externalDir);

        // Act
        context.PluginInstance.OnUninstalling();

        // Assert: external directory should not be deleted because it's outside DestinationBasePath
        Directory.Exists(externalDir).Should().BeTrue("paths outside the base path must not be deleted on uninstall");
    }
}


