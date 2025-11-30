using System;
using Jellyfin.Plugin.Polyglot.Models;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Polyglot.Helpers;

/// <summary>
/// Extension methods for creating privacy-aware log entities with automatic data fetching.
/// When the caller only has an ID, these methods fetch the entity data from the appropriate service.
/// When the caller already has the data, they should use the LogEntity constructors directly for performance.
/// </summary>
public static class LogEntityFactory
{
    #region Mirror Entities

    /// <summary>
    /// Creates a LogMirror entity, fetching data from configuration if not provided.
    /// </summary>
    /// <param name="configService">The configuration service to fetch data from.</param>
    /// <param name="mirrorId">The mirror ID.</param>
    /// <param name="sourceLibraryName">Optional source library name. If null, fetched from config.</param>
    /// <param name="targetLibraryName">Optional target library name. If null, fetched from config.</param>
    /// <returns>A LogMirror entity with populated data.</returns>
    public static LogMirror CreateLogMirror(
        this IConfigurationService configService,
        Guid mirrorId,
        string? sourceLibraryName = null,
        string? targetLibraryName = null)
    {
        // If both values provided, use them directly
        if (sourceLibraryName != null && targetLibraryName != null)
        {
            return new LogMirror(mirrorId, sourceLibraryName, targetLibraryName);
        }

        // Fetch missing data from config
        var mirror = configService.GetMirror(mirrorId);
        if (mirror != null)
        {
            return new LogMirror(
                mirrorId,
                sourceLibraryName ?? mirror.SourceLibraryName,
                targetLibraryName ?? mirror.TargetLibraryName);
        }

        // Mirror not found - use placeholders that indicate the mirror is unknown
        return new LogMirror(
            mirrorId,
            sourceLibraryName ?? "[unknown]",
            targetLibraryName ?? "[unknown]");
    }

    #endregion

    #region Alternative Entities

    /// <summary>
    /// Creates a LogAlternative entity, fetching data from configuration if not provided.
    /// </summary>
    /// <param name="configService">The configuration service to fetch data from.</param>
    /// <param name="alternativeId">The alternative ID.</param>
    /// <param name="name">Optional name. If null, fetched from config.</param>
    /// <param name="languageCode">Optional language code. If null, fetched from config.</param>
    /// <returns>A LogAlternative entity with populated data.</returns>
    public static LogAlternative CreateLogAlternative(
        this IConfigurationService configService,
        Guid alternativeId,
        string? name = null,
        string? languageCode = null)
    {
        // If both values provided, use them directly
        if (name != null && languageCode != null)
        {
            return new LogAlternative(alternativeId, name, languageCode);
        }

        // Fetch missing data from config
        var alternative = configService.GetAlternative(alternativeId);
        if (alternative != null)
        {
            return new LogAlternative(
                alternativeId,
                name ?? alternative.Name,
                languageCode ?? alternative.LanguageCode);
        }

        // Alternative not found - use placeholders
        return new LogAlternative(
            alternativeId,
            name ?? "[unknown]",
            languageCode ?? "[unknown]");
    }

    #endregion

    #region User Entities

    /// <summary>
    /// Creates a LogUser entity, fetching data from user manager if not provided.
    /// </summary>
    /// <param name="userManager">The user manager to fetch data from.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="username">Optional username. If null, fetched from user manager.</param>
    /// <returns>A LogUser entity with populated data.</returns>
    public static LogUser CreateLogUser(
        this IUserManager userManager,
        Guid userId,
        string? username = null)
    {
        // If username provided, use it directly
        if (username != null)
        {
            return new LogUser(userId, username);
        }

        // Fetch from user manager
        var user = userManager.GetUserById(userId);
        if (user != null)
        {
            return new LogUser(userId, user.Username);
        }

        // User not found - use placeholder
        return new LogUser(userId, "[unknown]");
    }

    /// <summary>
    /// Creates a LogUser entity with a placeholder username when IUserManager is not available.
    /// Use this only in contexts where user info cannot be fetched (e.g., low-level config services).
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>A LogUser entity with [user] placeholder.</returns>
    public static LogUser CreateLogUserById(Guid userId)
    {
        return new LogUser(userId, "[user]");
    }

    #endregion

    #region Library Entities

    /// <summary>
    /// Creates a LogLibrary entity for a source library, fetching data from library manager if not provided.
    /// </summary>
    /// <param name="libraryManager">The library manager to fetch data from.</param>
    /// <param name="libraryId">The library ID.</param>
    /// <param name="name">Optional name. If null, fetched from library manager.</param>
    /// <returns>A LogLibrary entity with populated data.</returns>
    public static LogLibrary CreateLogLibrary(
        this ILibraryManager libraryManager,
        Guid libraryId,
        string? name = null)
    {
        return CreateLogLibraryInternal(libraryManager, libraryId, name, isMirror: false);
    }

    /// <summary>
    /// Creates a LogLibrary entity for a mirror library, fetching data from library manager if not provided.
    /// </summary>
    /// <param name="libraryManager">The library manager to fetch data from.</param>
    /// <param name="libraryId">The library ID.</param>
    /// <param name="name">Optional name. If null, fetched from library manager.</param>
    /// <returns>A LogLibrary entity with populated data (marked as mirror).</returns>
    public static LogLibrary CreateLogMirrorLibrary(
        this ILibraryManager libraryManager,
        Guid libraryId,
        string? name = null)
    {
        return CreateLogLibraryInternal(libraryManager, libraryId, name, isMirror: true);
    }

    private static LogLibrary CreateLogLibraryInternal(
        ILibraryManager libraryManager,
        Guid libraryId,
        string? name,
        bool isMirror)
    {
        // If name provided, use it directly
        if (name != null)
        {
            return new LogLibrary(libraryId, name, isMirror);
        }

        // Fetch from library manager
        var folders = libraryManager.GetVirtualFolders();
        foreach (var folder in folders)
        {
            if (Guid.TryParse(folder.ItemId, out var folderId) && folderId == libraryId)
            {
                return new LogLibrary(libraryId, folder.Name, isMirror);
            }
        }

        // Library not found - use placeholder
        return new LogLibrary(libraryId, "[unknown]", isMirror);
    }

    #endregion
}

