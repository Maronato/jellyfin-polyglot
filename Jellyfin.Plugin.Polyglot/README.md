# Polyglot Plugin

This directory contains the Jellyfin plugin source code for Polyglot.

## Overview

Polyglot enables multi-language metadata support in Jellyfin by creating mirror libraries with hardlinks. Users are assigned to language alternatives, which controls which libraries they can access.

## Project Structure

```
Jellyfin.Plugin.Polyglot/
├── Api/
│   └── PolyglotController.cs     # REST API endpoints
├── Configuration/
│   ├── PluginConfiguration.cs    # Plugin settings model
│   └── configPage.html           # Admin UI (embedded resource)
├── EventConsumers/
│   ├── LibraryChangedConsumer.cs # Orphan detection on library deletion
│   ├── UserCreatedConsumer.cs    # New user handling
│   ├── UserDeletedConsumer.cs    # User cleanup
│   └── UserUpdatedConsumer.cs    # User update handling
├── Helpers/
│   ├── FileClassifier.cs         # Determines which files to hardlink
│   └── FileSystemHelper.cs       # Cross-platform hardlink operations
├── Models/
│   ├── LanguageAlternative.cs    # Language configuration
│   ├── LdapGroupMapping.cs       # LDAP group → language mapping
│   ├── LibraryInfo.cs            # Library metadata
│   ├── LibraryMirror.cs          # Mirror configuration
│   ├── SyncStatus.cs             # Mirror sync state
│   ├── UserInfo.cs               # User with language info
│   └── UserLanguageConfig.cs     # Per-user language assignment
├── Services/
│   ├── ILdapIntegrationService.cs
│   ├── ILibraryAccessService.cs
│   ├── IMirrorService.cs
│   ├── IUserLanguageService.cs
│   ├── LdapIntegrationService.cs # LDAP group lookup
│   ├── LibraryAccessService.cs   # User library permissions
│   ├── MirrorService.cs          # Hardlink creation/sync
│   └── UserLanguageService.cs    # Language assignments
├── Tasks/
│   ├── MirrorPostScanTask.cs     # Sync after library scans
│   ├── MirrorSyncTask.cs         # Scheduled sync task
│   └── UserLanguageSyncTask.cs   # LDAP user reconciliation
├── Plugin.cs                     # Plugin entry point
├── PluginServiceRegistrator.cs   # DI registration
└── build.yaml                    # Jellyfin plugin manifest
```

## Building

```bash
dotnet build --configuration Release
```

The output DLL will be in `bin/Release/net8.0/`.

## Key Components

### MirrorService

Handles all hardlink operations:
- Creates directory structure in target path
- Creates hardlinks for media files (video, audio, subtitles)
- Skips metadata files (NFO, images) and metadata directories
- Creates corresponding Jellyfin library with target language settings
- Syncs mirrors when source libraries change

### LibraryAccessService

Manages user library permissions:
- Tracks which libraries are "managed" by the plugin
- Calculates expected library access per user based on language assignment
- Updates Jellyfin user permissions (`EnabledFolders`)
- Preserves access to non-managed libraries (e.g., "Home Videos")

### UserLanguageService

Handles language assignments:
- Stores per-user language preferences
- Supports manual override to prevent LDAP from changing assignments
- Triggers library access updates when language changes

### LdapIntegrationService

Integrates with the Jellyfin LDAP plugin:
- Reads LDAP configuration via reflection
- Queries user group memberships
- Maps groups to language alternatives based on configured mappings

## File Classification

The `FileClassifier` determines which files are hardlinked vs. skipped:

**Hardlinked (media files):**
- Video: `.mkv`, `.mp4`, `.avi`, `.m4v`, `.ts`, etc.
- Audio: `.mp3`, `.flac`, `.m4a`, `.ogg`, etc.
- Subtitles: `.srt`, `.ass`, `.vtt`, `.sup`, etc.

**Skipped (metadata files):**
- NFO files: `.nfo`
- Images: `.jpg`, `.png`, `.gif`, `.webp`, `.tbn`
- Metadata directories: `extrafanart`, `extrathumbs`, `.actors`, `metadata`

## Testing

Tests are in the `Jellyfin.Plugin.Polyglot.Tests` project:

```bash
cd ../Jellyfin.Plugin.Polyglot.Tests
dotnet test
```
