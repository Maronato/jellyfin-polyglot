# Jellyfin Multi-Language Library Plugin

> **Note:** This repository was entirely written by AI and shouldn't be used in production before a stable 1.0 version is released.

A Jellyfin plugin that enables multi-language metadata support through library mirroring with hardlinks.

## Features

-   **Library Mirroring**: Create language-specific copies of your libraries using hardlinks (no storage duplication)
-   **Per-User Language Assignment**: Control which libraries users see based on their assigned language
-   **LDAP Integration**: Automatically assign languages to users based on LDAP group membership
-   **Real-Time Sync**: FileSystemWatcher monitors source libraries and updates mirrors automatically
-   **Scheduled Tasks**: Periodic sync and user reconciliation tasks

## How It Works

1. **Create Language Alternatives**: Define language configurations with a name, locale code (e.g., "pt-BR"), and destination path
2. **Add Library Mirrors**: For each language, select which source libraries to mirror
3. **Hardlink Creation**: The plugin creates hardlinks for media files (video, audio, subtitles) while excluding metadata files (NFO, images)
4. **Jellyfin Library Creation**: A new Jellyfin library is created pointing to the mirror with the appropriate metadata language settings
5. **User Assignment**: Assign users to language alternatives to control their library access

## Requirements

-   Jellyfin Server 10.10.x or higher
-   .NET 8.0 Runtime
-   **Source and mirror paths must be on the same filesystem** (hardlinks cannot cross filesystems)
-   Appropriate filesystem permissions for the Jellyfin process

## Installation

### From Plugin Repository (Recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Click **Add** and enter:
    - **Repository Name:** `Multi-Language Library`
    - **Repository URL:**
        ```
        https://raw.githubusercontent.com/Maronato/jellyfin-multi-lang/main/manifest.json
        ```
3. Click **Save**
4. Go to **Catalog** and find "Multi-Language Library"
5. Click **Install**
6. Restart Jellyfin Server

### Manual Installation

1. Download the latest `.zip` from the [releases page](https://github.com/Maronato/jellyfin-multi-lang/releases)
2. Extract to your Jellyfin plugins directory:
    - Linux: `/var/lib/jellyfin/plugins/MultiLang/`
    - Windows: `%APPDATA%\Jellyfin\Server\plugins\MultiLang\`
    - Docker: `/config/plugins/MultiLang/`
3. Restart Jellyfin Server

### After Installation

Access the plugin settings from **Dashboard → Plugins → Multi-Language Library**

## Configuration

### Languages Tab

-   View existing Jellyfin libraries and their metadata settings
-   Create new language alternatives
-   Add library mirrors to each alternative
-   Trigger manual sync operations

### Users Tab

-   View all users with their current language assignment
-   Assign languages to users via dropdown
-   Toggle "Manual Override" to prevent LDAP from changing the assignment

> **Warning**: Changing a user's language changes which libraries they can access. Watch history is stored per-library, so users may appear to lose their progress when switching languages.

### LDAP Tab

-   View LDAP integration status
-   Map LDAP groups to language alternatives
-   Set priority for group mappings (higher wins if user is in multiple groups)
-   Test LDAP connection and user lookup

### Settings Tab

-   **Sync Display Language**: Update user's Jellyfin UI language when plugin language changes
-   **Sync Subtitle/Audio Language**: Update user's preferred subtitle/audio language preferences
-   **Enable LDAP Integration**: Automatically assign languages based on LDAP groups
-   **Enable File Watching**: Real-time sync using FileSystemWatcher
-   **Sync Interval**: How often to run the scheduled sync task (default: 6 hours)

## Docker Considerations

When running Jellyfin in Docker, ensure both source and mirror paths are on the same filesystem:

```yaml
volumes:
    # Both must be from the same underlying filesystem for hardlinks to work
    - /media:/media:rw
```

Invalid configurations (hardlinks will fail):

-   Source from one volume, mirrors from another
-   Source as bind mount, mirrors as tmpfs

## API Endpoints

All endpoints require admin authentication.

| Method          | Endpoint                                 | Description                 |
| --------------- | ---------------------------------------- | --------------------------- |
| GET             | `/MultiLang/Libraries`                   | List Jellyfin libraries     |
| GET             | `/MultiLang/Alternatives`                | List language alternatives  |
| POST            | `/MultiLang/Alternatives`                | Create language alternative |
| DELETE          | `/MultiLang/Alternatives/{id}`           | Delete language alternative |
| POST            | `/MultiLang/Alternatives/{id}/Libraries` | Add library mirror          |
| POST            | `/MultiLang/Alternatives/{id}/Sync`      | Trigger sync                |
| GET             | `/MultiLang/Users`                       | List users with languages   |
| PUT             | `/MultiLang/Users/{id}/Language`         | Set user language           |
| GET             | `/MultiLang/LdapStatus`                  | Get LDAP status             |
| GET/POST/DELETE | `/MultiLang/LdapGroups`                  | Manage LDAP mappings        |

## Known Limitations

1. **Watch History**: Jellyfin tracks watch history per `ItemId`, which is unique per library. Switching a user's language will not transfer their watch history.

2. **Cross-Filesystem**: Hardlinks cannot span filesystems. Source and target paths must be on the same mount.

3. **LDAP Plugin Required**: LDAP integration requires the [Jellyfin LDAP Authentication Plugin](https://github.com/jellyfin/jellyfin-plugin-ldapauth) to be installed and configured.

## Building from Source

```bash
cd Jellyfin.Plugin.MultiLang
dotnet build --configuration Release
```

The compiled DLL will be in `bin/Release/net8.0/`.
