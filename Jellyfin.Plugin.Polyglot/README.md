# Polyglot Plugin — Developer Guide

This document covers the internal architecture, code organization, and contribution guidelines for developers.

For user documentation, see the [root README](../README.md).

## Architecture Overview

Polyglot is a Jellyfin plugin that creates "mirror" libraries using filesystem hardlinks. Each mirror fetches metadata in a different language, while the actual media files remain in place.

### System Diagram

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│                                   JELLYFINSERVER                                     │
├──────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                      │
│  ┌─────────────────────────────────────────────────────────────────────────────────┐ │
│  │                              POLYGLOT PLUGIN                                    │ │
│  ├─────────────────────────────────────────────────────────────────────────────────┤ │
│  │                                                                                 │ │
│  │   ┌──────────────────┐      ┌──────────────────────────────────────────────┐    │ │
│  │   │  Admin UI        │      │              REST API                        │    │ │
│  │   │  configPage.html │◄────►│         PolyglotController                   │    │ │
│  │   └──────────────────┘      └────────────────────┬─────────────────────────┘    │ │
│  │                                                  │                              │ │
│  │                    ┌─────────────────────────────┼─────────────────────────┐    │ │
│  │                    │                             │                         │    │ │
│  │                    ▼                             ▼                         ▼    │ │
│  │   ┌────────────────────────┐  ┌────────────────────────┐  ┌──────────────────┐  │ │
│  │   │    MirrorService       │  │  UserLanguageService   │  │ LibraryAccess-   │  │ │
│  │   │                        │  │                        │  │   Service        │  │ │
│  │   │ • CreateMirrorAsync    │  │ • AssignLanguageAsync  │  │                  │  │ │
│  │   │ • SyncMirrorAsync      │  │ • GetUserLanguage      │  │ • GetExpected-   │  │ │
│  │   │ • DeleteMirrorAsync    │  │ • ClearLanguageAsync   │  │   Access         │  │ │
│  │   │ • CleanupOrphans       │  │ • RemoveUser           │  │ • UpdateUser-    │  │ │
│  │   │                        │  │                        │  │   Access         │  │ │
│  │   └───────────┬────────────┘  └───────────┬────────────┘  └────────┬─────────┘  │ │
│  │               │                           │                        │            │ │
│  │               │                           │                        │            │ │
│  │               ▼                           ▼                        ▼            │ │
│  │   ┌─────────────────────────────────────────────────────────────────────────┐   │ │
│  │   │                         JELLYFIN APIs                                   │   │ │
│  │   │  ILibraryManager  │  IUserManager  │  IProviderManager  │  IFileSystem  │   │ │
│  │   └─────────────────────────────────────────────────────────────────────────┘   │ │
│  │                                                                                 │ │
│  ├─────────────────────────────────────────────────────────────────────────────────┤ │
│  │                           EVENT CONSUMERS                                       │ │
│  │  ┌──────────────────┐  ┌──────────────────┐  ┌────────────────────────────┐     │ │
│  │  │ UserCreated      │  │ UserDeleted      │  │ LibraryChanged             │     │ │
│  │  │ Consumer         │  │ Consumer         │  │ Consumer                   │     │ │
│  │  │                  │  │                  │  │                            │     │ │
│  │  │ Auto-assign      │  │ Cleanup config   │  │ ItemRemoved ──► Cleanup    │     │ │
│  │  │ default language │  │                  │  │ orphaned mirrors           │     │ │
│  │  └──────────────────┘  └──────────────────┘  └────────────────────────────┘     │ │
│  │                                                                                 │ │
│  ├─────────────────────────────────────────────────────────────────────────────────┤ │
│  │                           SCHEDULED TASKS                                       │ │
│  │  ┌──────────────────┐  ┌──────────────────┐  ┌────────────────────────────┐     │ │
│  │  │ MirrorSyncTask   │  │ MirrorPostScan   │  │ UserLanguageSyncTask       │     │ │
│  │  │                  │  │ Task             │  │                            │     │ │
│  │  │ Every 6 hours    │  │ After lib scans  │  │ Daily at 3:00 AM           │     │ │
│  │  │ Sync all mirrors │  │ Sync mirrors     │  │ Reconcile user access      │     │ │
│  │  └──────────────────┘  └──────────────────┘  └────────────────────────────┘     │ │
│  │                                                                                 │ │
│  ├─────────────────────────────────────────────────────────────────────────────────┤ │
│  │                              HELPERS                                            │ │
│  │  ┌──────────────────┐  ┌──────────────────┐  ┌────────────────────────────┐     │ │
│  │  │ FileClassifier   │  │ FileSystemHelper │  │ DebugReportService         │     │ │
│  │  │                  │  │                  │  │                            │     │ │
│  │  │ What to hardlink │  │ P/Invoke for     │  │ Log buffer + diagnostics   │     │ │
│  │  │ vs skip          │  │ hardlinks        │  │                            │     │ │
│  │  └──────────────────┘  └──────────────────┘  └────────────────────────────┘     │ │
│  │                                                                                 │ │
│  └─────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                      │
├──────────────────────────────────────────────────────────────────────────────────────┤
│                              JELLYFIN LIBRARIES                                      │
│                                                                                      │
│    ┌──────────────┐    ┌──────────────┐    ┌──────────────┐    ┌──────────────┐      │
│    │   Movies     │    │ Movies (PT)  │    │  TV Shows    │    │ TV Shows(PT) │      │
│    │   (Source)   │    │  (Mirror)    │    │   (Source)   │    │   (Mirror)   │      │
│    │              │    │              │    │              │    │              │      │
│    │  Lang: en    │    │  Lang: pt    │    │  Lang: en    │    │  Lang: pt    │      │
│    └──────┬───────┘    └──────┬───────┘    └──────┬───────┘    └──────┬───────┘      │
│           │                   │                   │                   │              │
└───────────┼───────────────────┼───────────────────┼───────────────────┼──────────────┘
            │                   │                   │                   │
            │    ┌──────────────┴───────────────────┴──────────────┐    │
            │    │                  HARDLINKS                      │    │
            └────►                                                 ◄────┘
                 │   Mirror files point to same inodes as source   │
                 │                                                 │
                 └──────────────────────┬──────────────────────────┘
                                        │
                                        ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                                    FILESYSTEM                                          │
│                                                                                        │
│    /media/                                                                             │
│    ├── movies/                          ← Source library path                          │
│    │   └── Inception (2010)/                                                           │
│    │       ├── Inception.mkv            ← Actual file (inode 12345)                    │
│    │       ├── Inception.nfo            ← English metadata (NOT hardlinked)            │
│    │       └── poster.jpg               ← English artwork (NOT hardlinked)             │
│    │                                                                                   │
│    └── polyglot/                                                                       │
│        └── spanish/                  ← Language alternative destination                │
│            └── movies/                  ← Mirror library path                          │
│                └── Inception (2010)/                                                   │
│                    ├── Inception.mkv    ← Hardlink to same inode 12345                 │
│                    ├── Inception.nfo    ← Spanish metadata                             │
│                    └── poster.png       ← Spanish artwork                              │
│                                           (Spanish metadata fetched by Jellyfin)       │
│                                                                                        │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

### Data Flow: Mirror Creation

```
Admin clicks "Add Mirror"
         │
         ▼
┌──────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│PolyglotController│────►│  MirrorService  │────►│FileSystemHelper │
│                  │     │                 │     │                 │
│ POST /Libraries  │     │CreateMirrorAsync│     │ CreateHardLink  │
└──────────────────┘     └───────┬─────────┘     └─────────────────┘
                                 │
                    ┌────────────┼────────────┐
                    │            │            │
                    ▼            ▼            ▼
           ┌──────────────┐ ┌──────────┐ ┌───────────────┐
           │Validate same │ │ Walk dir │ │Create Jellyfin│
           │filesystem    │ │ structure│ │library + scan │
           └──────────────┘ └──────────┘ └───────────────┘
                                 │
                                 ▼
                    ┌────────────────────────┐
                    │Update user library     │
                    │access for users with   │
                    │this language           │
                    └────────────────────────┘
```

### Data Flow: User Library Access Calculation

```
User assigned to "Spanish" language
                    │
                    ▼
        ┌───────────────────────┐
        │ LibraryAccessService  │
        │ GetExpectedAccess()   │
        └───────────┬───────────┘
                    │
    ┌───────────────┼───────────────┐
    │               │               │
    ▼               ▼               ▼
┌─────────┐   ┌─────────────┐   ┌──────────────┐
│ Get all │   │Get user's   │   │Get all       │
│Jellyfin │   │language     │   │mirror IDs    │
│libraries│   │mirrors      │   │(all langs)   │
└────┬────┘   └──────┬──────┘   └──────┬───────┘
     │               │                 │
     └───────────────┴─────────────────┘
                     │
                     ▼
        ┌────────────────────────┐
        │   For each library:    │
        │                        │
        │ • Is it user's mirror? │──► Include ✓
        │ • Is it OTHER mirror?  │──► Exclude ✗
        │ • Is it source WITH    │
        │   mirror for user?     │──► Exclude ✗
        │ • Is it source WITHOUT │
        │   mirror for user?     │──► Include ✓
        │ • Is it non-managed?   │──► Preserve existing
        └────────────────────────┘
                     │
                     ▼
        ┌────────────────────────┐
        │ Set EnableAllFolders   │
        │ = false                │
        │                        │
        │ Set EnabledFolders     │
        │ = calculated IDs       │
        └────────────────────────┘
```

### External Integrations

```
┌─────────────────────────────────────────────────────────────────────┐
│                    EXTERNAL DEPENDENCIES                            │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌───────────────────────────────────────────────────────────────┐  │
│  │                    Operating System                            │  │
│  │                                                                │  │
│  │  ┌────────────────────────┐  ┌────────────────────────┐       │  │
│  │  │ Windows: kernel32.dll  │  │ Unix: libc             │       │  │
│  │  │   CreateHardLink()     │  │   link()               │       │  │
│  │  └────────────────────────┘  └────────────────────────┘       │  │
│  │                                                                │  │
│  │  Filesystem must support hardlinks (ext4, NTFS, APFS,         │  │
│  │  XFS, btrfs, ZFS)                                             │  │
│  └───────────────────────────────────────────────────────────────┘  │
│                                   │                                 │
│                                   ▼                                 │
│             ┌──────────────────────────────────────────┐            │
│             │          FileSystemHelper                │            │
│             │                                          │            │
│             │ P/Invoke calls for native hardlink       │            │
│             │ creation on Windows and Unix             │            │
│             └──────────────────────────────────────────┘            │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

## Project Structure

```
Jellyfin.Plugin.Polyglot/
├── Api/
│   └── PolyglotController.cs        # REST API (admin-only endpoints)
├── Configuration/
│   ├── PluginConfiguration.cs       # Persisted settings model
│   └── configPage.html              # Admin UI (3 tabs: Languages, Users, Settings)
├── EventConsumers/
│   ├── LibraryChangedConsumer.cs    # Detects library deletions → cleanup orphans
│   ├── UserCreatedConsumer.cs       # Auto-assign language on new user
│   └── UserDeletedConsumer.cs       # Cleanup user config on deletion
├── Helpers/
│   ├── FileClassifier.cs            # Decides what to hardlink vs skip
│   ├── FileSystemHelper.cs          # Cross-platform hardlink creation (P/Invoke)
│   └── PolyglotLogger.cs            # Logger extensions + debug buffer capture
├── Models/
│   ├── LanguageAlternative.cs       # Language config (name, path, mirrors)
│   ├── LibraryMirror.cs             # Source → target mapping with sync status
│   ├── UserLanguageConfig.cs        # Per-user language assignment
│   ├── UserInfo.cs                  # API response model
│   ├── LibraryInfo.cs               # API response model
│   └── SyncStatus.cs                # Enum: Pending, Syncing, Synced, Error
├── Services/
│   ├── IMirrorService.cs            # Interface
│   ├── MirrorService.cs             # Hardlink creation, sync, cleanup
│   ├── IUserLanguageService.cs      # Interface
│   ├── UserLanguageService.cs       # Language assignment management
│   ├── ILibraryAccessService.cs     # Interface
│   ├── LibraryAccessService.cs      # User permission calculations
│   ├── IDebugReportService.cs       # Interface
│   └── DebugReportService.cs        # Diagnostic report generation
├── Tasks/
│   ├── MirrorPostScanTask.cs        # ILibraryPostScanTask: sync after scans
│   ├── MirrorSyncTask.cs            # IScheduledTask: periodic sync (6h default)
│   └── UserLanguageSyncTask.cs      # IScheduledTask: daily access reconciliation
├── Plugin.cs                        # Entry point, IHasWebPages, OnUninstalling cleanup
└── PluginServiceRegistrator.cs      # DI registration
```

## Core Services

### MirrorService

Handles all hardlink operations with per-mirror locking:

```csharp
// Key operations
Task CreateMirrorAsync(alternative, mirror, ct)    // Initial mirror creation
Task SyncMirrorAsync(mirror, progress, ct)         // Incremental sync
Task DeleteMirrorAsync(mirror, deleteLib, deleteFiles, ct)
Task<OrphanCleanupResult> CleanupOrphanedMirrorsAsync(ct)
```

**Sync algorithm:**

1. Build file sets with signatures (size + mtime) for source and target
2. Detect additions (new in source), deletions (missing from source), modifications (signature mismatch)
3. Delete removed files, recreate modified hardlinks, add new hardlinks
4. Clean up empty directories

### LibraryAccessService

Calculates which libraries each user should see:

```csharp
IEnumerable<Guid> GetExpectedLibraryAccess(userId)
Task UpdateUserLibraryAccessAsync(userId, ct)
Task<bool> ReconcileUserAccessAsync(userId, ct)
```

**Access rules:**

-   User's language mirrors: ✅ included
-   Other languages' mirrors: ❌ excluded
-   Sources with mirror in user's language: ❌ excluded (user sees mirror instead)
-   Sources without mirror in user's language: ✅ included
-   Non-managed libraries: preserved (not touched)

### DebugReportService

Generates troubleshooting reports with optional anonymization:

-   Captures recent logs in a circular buffer (500 entries, 1 hour)
-   Verifies hardlinks by checking link count
-   Reports filesystem info, disk space, and mirror health

## File Classification

`FileClassifier` determines what gets hardlinked:

| Hardlinked                             | Skipped                                                                |
| -------------------------------------- | ---------------------------------------------------------------------- |
| `.mkv`, `.mp4`, `.avi`, `.ts`, `.m2ts` | `.nfo`                                                                 |
| `.mp3`, `.flac`, `.m4a`, `.ogg`        | `.jpg`, `.jpeg`, `.png`, `.gif`, `.webp`, `.tbn`, `.bmp`               |
| `.srt`, `.ass`, `.vtt`, `.sup`, `.sub` | `extrafanart/`, `extrathumbs/`, `.trickplay/`, `metadata/`, `.actors/` |

Exclusions are configurable in plugin settings.

## REST API

All endpoints require admin privileges (`[Authorize(Policy = "RequiresElevation")]`):

| Method   | Endpoint                                           | Description                       |
| -------- | -------------------------------------------------- | --------------------------------- |
| GET      | `/Polyglot/Libraries`                              | List Jellyfin libraries           |
| GET/POST | `/Polyglot/Alternatives`                           | List/create language alternatives |
| DELETE   | `/Polyglot/Alternatives/{id}`                      | Delete alternative + mirrors      |
| POST     | `/Polyglot/Alternatives/{id}/Libraries`            | Add mirror to alternative         |
| DELETE   | `/Polyglot/Alternatives/{id}/Libraries/{sourceId}` | Delete mirror                     |
| POST     | `/Polyglot/Alternatives/{id}/Sync`                 | Trigger sync                      |
| GET      | `/Polyglot/Users`                                  | List users with assignments       |
| PUT      | `/Polyglot/Users/{id}/Language`                    | Set user language                 |
| POST     | `/Polyglot/Users/EnableAll`                        | Enable plugin for all users       |
| GET      | `/Polyglot/DebugReport`                            | Generate debug report             |

## Event Consumers

| Consumer                 | Trigger              | Action                                          |
| ------------------------ | -------------------- | ----------------------------------------------- |
| `UserCreatedConsumer`    | New user created     | Apply auto-manage default language if enabled   |
| `UserDeletedConsumer`    | User deleted         | Remove from `UserLanguages` config              |
| `LibraryChangedConsumer` | Library item removed | Cleanup orphaned mirrors, reconcile user access |

## Scheduled Tasks

| Task                   | Default Schedule    | Purpose                                   |
| ---------------------- | ------------------- | ----------------------------------------- |
| `MirrorSyncTask`       | Every 6 hours       | Sync all mirrors + cleanup orphans        |
| `MirrorPostScanTask`   | After library scans | Keep mirrors in sync with source changes  |
| `UserLanguageSyncTask` | Daily at 3:00 AM    | Reconcile user library access permissions |

## Cross-Platform Hardlinks

`FileSystemHelper` uses P/Invoke for native hardlink operations:

```csharp
// Windows
[DllImport("kernel32.dll")]
static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

// Unix/Linux/macOS
[DllImport("libc")]
static extern int link(string oldpath, string newpath);
```

Same-filesystem detection is done by attempting a test hardlink (most reliable cross-platform method).

## Building

```bash
cd Jellyfin.Plugin.Polyglot
dotnet build --configuration Release
# Output: bin/Release/net8.0/Jellyfin.Plugin.Polyglot.dll
```

## Testing

```bash
cd Jellyfin.Plugin.Polyglot.Tests
dotnet test
```

Tests use `PluginTestContext` to create a real `Plugin.Instance` with mocked Jellyfin dependencies.

### Test Categories

-   **Services/**: Unit tests for business logic (MirrorService, LibraryAccessService, etc.)
-   **Behaviors/**: End-to-end scenarios (user assignment, library access calculations)
-   **Helpers/**: FileClassifier rules, FileSystemHelper operations
-   **Api/**: Controller tests with mocked services
-   **EventConsumers/**: Event handling tests
-   **Configuration/**: Serialization and plugin lifecycle tests

### Running Specific Tests

```bash
dotnet test --filter "FullyQualifiedName~MirrorServiceTests"
dotnet test --filter "Category=Integration"
```

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Write tests for your changes
4. Ensure all tests pass (`dotnet test`)
5. Build in Release mode to catch additional warnings
6. Submit a pull request

### Code Style

-   Follow existing patterns in the codebase
-   Use `PolyglotInfo`, `PolyglotWarning`, `PolyglotError` for logging (captures to debug buffer)
-   Add XML documentation for public APIs
-   Keep services focused and testable

### Areas for Contribution

-   Additional metadata provider integrations
-   Performance optimizations for large libraries
-   UI/UX improvements in the config page
-   Documentation and examples
