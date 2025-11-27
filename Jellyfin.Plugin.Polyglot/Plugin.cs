using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Polyglot.Configuration;
using Jellyfin.Plugin.Polyglot.Helpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot;

/// <summary>
/// Polyglot Plugin for Jellyfin.
/// Enables multi-language metadata support through library mirroring.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="logger">The plugin logger.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILibraryManager libraryManager,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public override string Name => "Polyglot";

    /// <inheritdoc />
    public override string Description => "Multi-language metadata support through library mirroring with hardlinks.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    /// <summary>
    /// Gets the plugin configuration.
    /// </summary>
    public PluginConfiguration PluginConfiguration => Configuration;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }

    /// <summary>
    /// Saves the plugin configuration.
    /// </summary>
    public new void SaveConfiguration()
    {
        SaveConfiguration(Configuration);
    }

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        var config = Configuration;

        try
        {
            _logger.LogInformation("Polyglot plugin uninstall: starting cleanup of mirror libraries and directories");

            foreach (var alternative in config.LanguageAlternatives)
            {
                foreach (var mirror in alternative.MirroredLibraries)
                {
                    // Remove Jellyfin virtual folders created for mirrors
                    if (mirror.TargetLibraryId.HasValue && !string.IsNullOrWhiteSpace(mirror.TargetLibraryName))
                    {
                        try
                        {
                            _logger.LogInformation(
                                "Polyglot uninstall: removing mirror library {LibraryName}",
                                mirror.TargetLibraryName);

                            // Block until removal completes so we don't leave behind references
                            _libraryManager
                                .RemoveVirtualFolder(mirror.TargetLibraryName, true)
                                .GetAwaiter()
                                .GetResult();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "Polyglot uninstall: failed to remove mirror library {LibraryName}",
                                mirror.TargetLibraryName);
                        }
                    }

                    // Delete mirror directories, but only when safely under the configured base path
                    if (!string.IsNullOrWhiteSpace(mirror.TargetPath)
                        && !string.IsNullOrWhiteSpace(alternative.DestinationBasePath)
                        && Directory.Exists(mirror.TargetPath)
                        && FileSystemHelper.IsPathSafe(mirror.TargetPath, alternative.DestinationBasePath))
                    {
                        try
                        {
                            _logger.LogInformation(
                                "Polyglot uninstall: deleting mirror directory {Path}",
                                mirror.TargetPath);

                            Directory.Delete(mirror.TargetPath, true);

                            // Clean up any empty parent directories up to the base path
                            var parent = Path.GetDirectoryName(mirror.TargetPath)
                                         ?? alternative.DestinationBasePath;
                            FileSystemHelper.CleanupEmptyDirectories(parent, alternative.DestinationBasePath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "Polyglot uninstall: failed to delete mirror directory {Path}",
                                mirror.TargetPath);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Polyglot plugin uninstall: unexpected error during cleanup");
        }

        // Clear configuration to avoid leaving stale state behind on disk
        try
        {
            config.LanguageAlternatives.Clear();
            config.UserLanguages.Clear();
            config.LdapGroupMappings.Clear();
            SaveConfiguration();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Polyglot plugin uninstall: failed to save cleaned configuration");
        }
    }
}
