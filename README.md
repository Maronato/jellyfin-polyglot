# Polyglot

**Multi-language metadata for Jellyfin—without duplicating files.**

Polyglot creates language-specific "mirror" libraries using filesystem hardlinks. Your media files stay exactly where they are, but each language gets its own library with native metadata. Users are then assigned to their preferred language and only see libraries in that language.

```
/media/movies/                      ← Original library (English metadata)
    Inception (2010)/
        Inception.mkv

/media/polyglot/portuguese/movies/  ← Mirror library (Portuguese metadata)
    Inception (2010)/
        Inception.mkv  ──────────→ [hardlink to original, zero extra storage]
```

## Features

-   **Zero-Copy Mirroring** — Hardlinks share actual file data, mirrors use negligible disk space
-   **Per-User Language Control** — Each user sees only libraries matching their assigned language
-   **Watch History Preserved** — Watch progress syncs across source and mirror libraries
-   **Automatic Sync** — Mirrors update after library scans and on a configurable schedule
-   **LDAP Integration** — Auto-assign languages based on LDAP group membership

## Requirements

-   Jellyfin Server 10.10.x or higher
-   Source and mirror paths on the **same filesystem** (hardlinks can't cross mount points)
-   Write permissions to the mirror destination path

## Installation

### From Plugin Repository

1. **Dashboard → Plugins → Repositories → Add**
2. Enter:
    - Name: `Polyglot`
    - URL: `https://raw.githubusercontent.com/Maronato/jellyfin-polyglot/main/manifest.json`
3. **Catalog → Polyglot → Install**
4. Restart Jellyfin

### Manual

Download from [Releases](https://github.com/Maronato/jellyfin-polyglot/releases) and extract to:

-   Linux: `/var/lib/jellyfin/plugins/Polyglot/`
-   Windows: `%APPDATA%\Jellyfin\Server\plugins\Polyglot\`
-   Docker: `/config/plugins/Polyglot/`

## Quick Start

### 1. Create a Language Alternative

1. **Dashboard → Plugins → Polyglot → Languages tab**
2. Click **+** and fill in:
    - **Name**: e.g., "Portuguese"
    - **Language**: Select from dropdown
    - **Destination Path**: e.g., `/media/polyglot/portuguese`

### 2. Add Library Mirrors

1. Click **+** on your language alternative
2. Select a source library (e.g., "Movies")
3. The plugin will create hardlinks and a new Jellyfin library with the target language's metadata settings

### 3. Assign Users

1. Go to the **Users tab**
2. For each user, select their language:
    - **Not managed**: Plugin doesn't control this user
    - **Default libraries**: User sees original source libraries
    - **[Language name]**: User sees only that language's mirrors

## Docker Configuration

Hardlinks require a **single mount point**:

```yaml
# ✅ Correct
volumes:
  - /mnt/media:/media:rw
# Source: /media/movies → Mirror: /media/polyglot/portuguese/movies

# ❌ Wrong (hardlinks will fail)
volumes:
  - /mnt/movies:/movies:rw
  - /mnt/mirrors:/mirrors:rw
```

## How It Works

### Mirroring

When you create a mirror, Polyglot:

1. Walks the source library directory
2. Creates **hardlinks** for media files (video, audio, subtitles)
3. **Skips** metadata files (`.nfo`, images) and metadata directories
4. Creates a Jellyfin library with the target language's metadata settings
5. Triggers a library scan

### Library Access Control

When a user is assigned to a language:

-   They see mirror libraries for their language
-   They see source libraries that don't have a mirror in their language
-   They don't see mirrors for other languages
-   Non-managed libraries (like "Home Videos") remain accessible

### Synchronization

Mirrors stay in sync through:

-   **Post-scan hook**: After every Jellyfin library scan
-   **Scheduled task**: Configurable interval (default: 6 hours)
-   **Orphan detection**: Automatically cleans up mirrors when source libraries are deleted

## Known Limitations

-   **Hardlinks are filesystem-bound**: Source and mirror paths must be on the same filesystem
-   **LDAP requires separate plugin**: Install [jellyfin-plugin-ldapauth](https://github.com/jellyfin/jellyfin-plugin-ldapauth) first

## Building from Source

```bash
git clone https://github.com/Maronato/jellyfin-polyglot.git
cd jellyfin-polyglot/Jellyfin.Plugin.Polyglot
dotnet build --configuration Release
```

## License

MIT
