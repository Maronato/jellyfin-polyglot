# Polyglot Plugin — Developer Guide

Source code for the Jellyfin Polyglot plugin.

## Project Structure

```
Jellyfin.Plugin.Polyglot/
├── Api/
│   └── PolyglotController.cs       # REST API endpoints
├── Configuration/
│   ├── PluginConfiguration.cs      # Settings model
│   └── configPage.html             # Admin UI
├── EventConsumers/
│   ├── LibraryChangedConsumer.cs   # Detects library deletions
│   ├── UserCreatedConsumer.cs      # LDAP auto-assignment for new users
│   ├── UserDeletedConsumer.cs      # Cleanup on user deletion
│   └── UserUpdatedConsumer.cs      # Re-evaluates LDAP groups
├── Helpers/
│   ├── FileClassifier.cs           # Decides what to hardlink
│   └── FileSystemHelper.cs         # Cross-platform hardlink ops
├── Models/                         # Data models
├── Services/
│   ├── MirrorService.cs            # Hardlink creation & sync
│   ├── LibraryAccessService.cs     # User permission management
│   ├── UserLanguageService.cs      # Language assignments
│   └── LdapIntegrationService.cs   # LDAP group lookup
├── Tasks/
│   ├── MirrorPostScanTask.cs       # Sync after library scans
│   ├── MirrorSyncTask.cs           # Periodic sync
│   └── UserLanguageSyncTask.cs     # User access reconciliation
├── Plugin.cs                       # Entry point
└── PluginServiceRegistrator.cs     # DI registration
```

## Key Services

### MirrorService

Handles hardlink operations:

-   Creates hardlinks for media files, skips metadata
-   Creates Jellyfin libraries with target language settings
-   Syncs mirrors incrementally (adds new files, removes deleted ones)
-   Cleans up orphaned mirrors when libraries are deleted

### LibraryAccessService

Calculates which libraries each user should see:

-   Users with a language → see that language's mirrors
-   Users on "default" → see source libraries only
-   Unmanaged users → plugin doesn't touch their access
-   Non-managed libraries (e.g., "Home Videos") → access preserved

### UserLanguageService

Manages language assignments:

-   Stores per-user preferences in plugin config
-   Supports "manual override" flag to prevent LDAP overwriting admin choices
-   Triggers library access updates on assignment changes

### LdapIntegrationService

Integrates with jellyfin-plugin-ldapauth:

-   Reads LDAP config via reflection (no direct dependency)
-   Queries group memberships via `memberOf` attribute
-   Maps groups to languages based on priority

## File Classification

`FileClassifier` determines what gets hardlinked:

| Hardlinked                     | Skipped                                    |
| ------------------------------ | ------------------------------------------ |
| `.mkv`, `.mp4`, `.avi`, `.ts`  | `.nfo`                                     |
| `.mp3`, `.flac`, `.m4a`        | `.jpg`, `.png`, `.gif`, `.webp`            |
| `.srt`, `.ass`, `.vtt`, `.sup` | `extrafanart/`, `.trickplay/`, `metadata/` |

Configurable via plugin settings.

## Building

```bash
dotnet build --configuration Release
# Output: bin/Release/net8.0/Jellyfin.Plugin.Polyglot.dll
```

## Testing

```bash
cd ../Jellyfin.Plugin.Polyglot.Tests
dotnet test
```

Tests use `PluginTestContext` to create a real `Plugin.Instance` with mocked dependencies. Key test categories:

-   **Service tests**: Unit tests for business logic
-   **Behavior tests**: End-to-end library access scenarios
-   **File operation tests**: Real filesystem hardlink tests
