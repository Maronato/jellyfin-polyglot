using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Polyglot.Configuration;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
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
    private readonly IApplicationHost _applicationHost;
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
    /// <param name="applicationHost">The Jellyfin application host for resolving services.</param>
    /// <param name="logger">The plugin logger.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IApplicationHost applicationHost,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _applicationHost = applicationHost;
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
            _logger.PolyglotInfo("Polyglot plugin uninstall: starting cleanup of mirror libraries and directories");

            // Resolve the mirror service to handle proper cleanup
            var mirrorService = _applicationHost.Resolve<IMirrorService>();

            foreach (var alternative in config.LanguageAlternatives)
            {
                // Create a copy of the list since DeleteMirrorAsync may modify it
                var mirrorsToDelete = alternative.MirroredLibraries.ToList();

                foreach (var mirror in mirrorsToDelete)
                {
                    try
                    {
                        _logger.PolyglotInfo(
                            "Polyglot uninstall: deleting mirror {0} ({1})",
                            mirror.Id,
                            mirror.TargetLibraryName);

                        // Use the service's DeleteMirrorAsync which handles:
                        // - Removing the Jellyfin virtual folder with refreshLibrary: true
                        // - Deleting the mirror files/directory
                        // - Proper locking and error handling
                        mirrorService
                            .DeleteMirrorAsync(mirror, deleteLibrary: true, deleteFiles: true)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.PolyglotWarning(
                            ex,
                            "Polyglot uninstall: failed to delete mirror {0}",
                            mirror.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "Polyglot plugin uninstall: unexpected error during cleanup");
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
            _logger.PolyglotWarning(ex, "Polyglot plugin uninstall: failed to save cleaned configuration");
        }
    }
}
