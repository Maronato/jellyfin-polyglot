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

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        _logger.PolyglotInfo("Plugin OnUninstalling: Starting cleanup");

        try
        {
            // Resolve services for cleanup
            var mirrorService = _applicationHost.Resolve<IMirrorService>();
            var configService = _applicationHost.Resolve<IConfigurationService>();

            // Get mirror IDs to iterate
            var mirrorIds = configService.Read(c => c.LanguageAlternatives
                .SelectMany(a => a.MirroredLibraries.Select(m => new { m.Id, m.TargetLibraryName }))
                .ToList());

            _logger.PolyglotInfo("Plugin OnUninstalling: Deleting {0} mirrors", mirrorIds.Count);

            foreach (var mirror in mirrorIds)
            {
                var mirrorEntity = configService.CreateLogMirror(mirror.Id);
                try
                {
                    _logger.PolyglotDebug("Plugin OnUninstalling: Deleting mirror {0}", mirrorEntity);

                    // Use forceConfigRemoval=true during uninstall to ensure cleanup completes
                    var result = mirrorService
                        .DeleteMirrorAsync(mirror.Id, deleteLibrary: true, deleteFiles: true, forceConfigRemoval: true)
                        .GetAwaiter()
                        .GetResult();

                    if (result.HasErrors)
                    {
                        _logger.PolyglotWarning("Plugin OnUninstalling: Mirror {0} removed with errors: {1} {2}",
                            mirrorEntity, result.LibraryDeletionError, result.FileDeletionError);
                    }
                }
                catch (Exception ex)
                {
                    _logger.PolyglotWarning(ex, "Plugin OnUninstalling: Failed to delete mirror {0}", mirrorEntity);
                }
            }

            // Clear all configuration
            configService.Update(c =>
            {
                c.LanguageAlternatives.Clear();
                c.UserLanguages.Clear();
            });

            _logger.PolyglotInfo("Plugin OnUninstalling: Cleanup completed");
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "Plugin OnUninstalling: Unexpected error during cleanup");
        }
    }
}
