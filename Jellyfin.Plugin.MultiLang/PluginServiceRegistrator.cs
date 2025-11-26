using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.MultiLang.EventConsumers;
using Jellyfin.Plugin.MultiLang.Services;
using Jellyfin.Plugin.MultiLang.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MultiLang;

/// <summary>
/// Registers plugin services with the dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Core services
        serviceCollection.AddSingleton<IMirrorService, MirrorService>();
        serviceCollection.AddSingleton<IUserLanguageService, UserLanguageService>();
        serviceCollection.AddSingleton<ILibraryAccessService, LibraryAccessService>();
        serviceCollection.AddSingleton<ILdapIntegrationService, LdapIntegrationService>();

        // Event consumers
        serviceCollection.AddSingleton<IEventConsumer<UserCreatedEventArgs>, UserCreatedConsumer>();
        serviceCollection.AddSingleton<IEventConsumer<UserDeletedEventArgs>, UserDeletedConsumer>();
        serviceCollection.AddSingleton<IEventConsumer<UserUpdatedEventArgs>, UserUpdatedConsumer>();

        // Hosted service for library change monitoring
        serviceCollection.AddHostedService<LibraryChangedConsumer>();

        // Post-scan task - triggers mirror sync after library scans
        serviceCollection.AddSingleton<ILibraryPostScanTask, MirrorPostScanTask>();
    }
}

