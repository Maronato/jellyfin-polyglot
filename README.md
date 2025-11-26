# Polyglot

**Serve your Jellyfin library in multiple languages—without duplicating a single file.**

> ⚠️ **Pre-release Software**: This plugin was developed with AI assistance and is not yet production-ready. Use at your own risk until v1.0 is released.

---

## The Problem

Jellyfin supports only **one metadata language per library**. If your household includes English, Portuguese, and Spanish speakers, everyone sees the same movie titles, descriptions, and artwork—in whatever language you configured.

## The Solution

Polyglot creates **language-specific "mirror" libraries** using filesystem hardlinks. Your media files stay exactly where they are (zero storage duplication), but each language gets its own library with native metadata.

```
/media/movies/                    ← Your original library (English metadata)
    Inception (2010)/
        Inception.mkv

/media/polyglot/portuguese/movies/  ← Mirror library (Portuguese metadata)
    Inception (2010)/
        Inception.mkv  ────────────→ [hardlink to original]
```

Users are then assigned to their preferred language. A Portuguese user sees only the Portuguese library; an English user sees the original. Same files, different metadata, seamless experience.

---

## Features

| Feature                       | Description                                                                        |
| ----------------------------- | ---------------------------------------------------------------------------------- |
| **Zero-Copy Mirroring**       | Hardlinks share the actual file data—mirrors consume negligible disk space         |
| **Per-User Language Control** | Each user sees only libraries matching their assigned language                     |
| **Automatic Sync**            | Mirrors update automatically after library scans and on a configurable schedule    |
| **LDAP Integration**          | Auto-assign languages based on LDAP/Active Directory group membership              |
| **Preference Sync**           | Optionally sync subtitle and audio language preferences to match                   |
| **Clean Separation**          | Only media files (video, audio, subtitles) are mirrored—metadata stays independent |

---

## Requirements

-   **Jellyfin Server** 10.10.x or higher
-   **Same Filesystem**: Source and mirror paths must be on the same filesystem (hardlinks cannot cross mount points)
-   **Write Permissions**: Jellyfin must have write access to the mirror destination path

---

## Installation

### Plugin Repository (Recommended)

1. Go to **Dashboard → Plugins → Repositories**
2. Click **Add** and enter:
    - **Name**: `Polyglot`
    - **URL**: `https://raw.githubusercontent.com/Maronato/jellyfin-polyglot/main/manifest.json`
3. Go to **Catalog**, find **Polyglot**, and click **Install**
4. Restart Jellyfin

### Manual Installation

1. Download the latest release from the [Releases page](https://github.com/Maronato/jellyfin-polyglot/releases)
2. Extract to your plugins directory:
    - **Linux**: `/var/lib/jellyfin/plugins/Polyglot/`
    - **Windows**: `%APPDATA%\Jellyfin\Server\plugins\Polyglot\`
    - **Docker**: `/config/plugins/Polyglot/`
3. Restart Jellyfin

---

## Quick Start

### 1. Create a Language Alternative

A "language alternative" defines a target language and where its mirror libraries will live.

1. Go to **Dashboard → Plugins → Polyglot**
2. In the **Languages** tab, click the **+** button
3. Fill in:
    - **Name**: e.g., "Portuguese"
    - **Language**: Select from dropdown (e.g., Portuguese)
    - **Country**: Optional region (e.g., BR for Brazilian Portuguese)
    - **Destination Path**: e.g., `/media/polyglot/portuguese`
4. Click **Create**

### 2. Add Library Mirrors

For each source library you want available in this language:

1. Click the **+** button on your language alternative
2. Select the source library (e.g., "Movies")
3. The target path auto-fills based on your base path
4. Click **Create Mirror**

The plugin will:

-   Create hardlinks for all media files
-   Create a new Jellyfin library with the correct metadata language
-   Trigger an initial library scan

### 3. Assign Users

1. Go to the **Users** tab
2. For each user, select their language from the dropdown:
    - **Not managed**: Plugin doesn't control this user's library access
    - **Default libraries**: User sees original/source libraries only
    - **[Language name]**: User sees only that language's mirrors

---

## Configuration Reference

### Languages Tab

-   View all Jellyfin libraries and their current metadata language
-   Create/delete language alternatives
-   Add/remove library mirrors
-   Manually trigger sync for any alternative

### Users Tab

-   View all users with their current language assignment
-   Assign languages via dropdown
-   **Enable Plugin for All Users**: Bulk-enable plugin management for every user

### LDAP Tab

_Only visible if the LDAP Authentication plugin is installed._

-   Map LDAP groups to language alternatives
-   Set priority for overlapping group memberships
-   Test LDAP connection and user group lookup

### Settings Tab

| Setting                    | Description                                                              |
| -------------------------- | ------------------------------------------------------------------------ |
| **Sync Display Language**  | Update user's Jellyfin UI language to match their assigned language      |
| **Sync Subtitle Language** | Set preferred subtitle language to match                                 |
| **Sync Audio Language**    | Set preferred audio language to match                                    |
| **Mirror Sync Interval**   | Hours between scheduled sync tasks (also syncs after every library scan) |

---

## Docker Configuration

Hardlinks require source and mirror to be on the **same filesystem**. In Docker, this means they must come from the same volume mount:

```yaml
# ✅ Correct: Single mount point
volumes:
    - /mnt/media:/media:rw
# Plugin paths:
#   Source: /media/movies
#   Mirror: /media/polyglot/portuguese/movies
```

```yaml
# ❌ Wrong: Separate mounts (hardlinks will fail)
volumes:
    - /mnt/movies:/movies:rw
    - /mnt/mirrors:/mirrors:rw
```

---

## How It Works

### Hardlink Mirroring

When you create a mirror, Polyglot:

1. **Walks the source library** directory tree
2. **Creates hardlinks** for media files (`.mkv`, `.mp4`, `.srt`, `.mp3`, etc.)
3. **Skips metadata files** (`.nfo`, `.jpg`, `.png`, etc.) and metadata directories (`extrafanart`, `.actors`, etc.)
4. **Creates the Jellyfin library** with the target language's metadata settings

Because hardlinks point to the same underlying data blocks, the mirror consumes almost no additional storage.

### Automatic Synchronization

Mirrors stay in sync through:

-   **Post-Scan Hook**: After every Jellyfin library scan, all mirrors are synchronized
-   **Scheduled Task**: Configurable interval (default: 6 hours)
-   **Orphan Detection**: If you delete a source or mirror library, the plugin detects this and cleans up

### User Library Access

When a user is assigned to a language:

1. Their `EnableAllFolders` permission is disabled
2. They're granted access to:
    - Mirror libraries for their assigned language
    - Source libraries that don't have a mirror in their language
    - Any non-managed libraries (e.g., "Home Videos")
3. They lose access to:
    - Source libraries that have mirrors in their language (they see the mirror instead)
    - Mirror libraries for other languages

---

## API Reference

All endpoints require admin authentication and are prefixed with `/Polyglot`.

| Method   | Endpoint                                  | Description                                          |
| -------- | ----------------------------------------- | ---------------------------------------------------- |
| `GET`    | `/Libraries`                              | List all Jellyfin libraries with metadata info       |
| `GET`    | `/Alternatives`                           | List all language alternatives                       |
| `POST`   | `/Alternatives`                           | Create a language alternative                        |
| `DELETE` | `/Alternatives/{id}`                      | Delete alternative (optionally with libraries/files) |
| `POST`   | `/Alternatives/{id}/Libraries`            | Add a library mirror                                 |
| `DELETE` | `/Alternatives/{id}/Libraries/{sourceId}` | Remove a library mirror                              |
| `POST`   | `/Alternatives/{id}/Sync`                 | Trigger manual sync                                  |
| `GET`    | `/Users`                                  | List users with language assignments                 |
| `PUT`    | `/Users/{id}/Language`                    | Set user's language                                  |
| `POST`   | `/Users/EnableAll`                        | Enable plugin management for all users               |
| `POST`   | `/Users/{id}/Disable`                     | Disable plugin management for a user                 |
| `GET`    | `/LdapStatus`                             | Get LDAP integration status                          |
| `GET`    | `/LdapGroups`                             | List LDAP group mappings                             |
| `POST`   | `/LdapGroups`                             | Add LDAP group mapping                               |
| `DELETE` | `/LdapGroups/{id}`                        | Remove LDAP group mapping                            |
| `POST`   | `/TestLdap`                               | Test LDAP connection                                 |
| `GET`    | `/Settings`                               | Get plugin settings                                  |
| `PUT`    | `/Settings`                               | Update plugin settings                               |
| `POST`   | `/CleanupOrphanedMirrors`                 | Remove mirrors for deleted libraries                 |

---

## Known Limitations

### Watch History Doesn't Transfer

Jellyfin tracks watch progress per library item. Since mirror libraries have different item IDs, switching a user's language makes them "lose" their watch history. The history still exists—it's just tied to the original library.

### Hardlinks Are Filesystem-Bound

Hardlinks cannot cross filesystem boundaries. Your source and mirror paths must be on the same mount. This is a fundamental limitation of how hardlinks work, not a plugin limitation.

### LDAP Requires Separate Plugin

LDAP integration requires the [Jellyfin LDAP Authentication Plugin](https://github.com/jellyfin/jellyfin-plugin-ldapauth) to be installed and configured. Polyglot reads LDAP configuration from that plugin.

---

## Building from Source

```bash
git clone https://github.com/Maronato/jellyfin-polyglot.git
cd jellyfin-polyglot/Jellyfin.Plugin.Polyglot
dotnet build --configuration Release
```

Output: `bin/Release/net8.0/Jellyfin.Plugin.Polyglot.dll`

---

## Contributing

Contributions are welcome! Please open an issue to discuss major changes before submitting a PR.

## License

MIT License—see [LICENSE](LICENSE) for details.
