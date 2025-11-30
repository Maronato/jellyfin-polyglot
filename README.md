![Logo](./logo.png)

# Polyglot

**Multi-language metadata for Jellyfin.**

> 🤖⚠️ **AI-Generated Notice** ⚠️🤖  
> This repository was created entirely by AI.
>
> The code is thoroughly tested and actively used on my main Jellyfin server. However, **please be aware of potential risks before installing**, and report any issues you encounter!

Polyglot creates language-specific "mirror" libraries using filesystem hardlinks. Your media files stay exactly where they are, but each language gets its own library with native metadata. Users are then assigned to their preferred language and only see libraries in that language.

```
/media/movies/                      ← Original library (English metadata)
    Inception (2010)/
        Inception.mkv

/media/polyglot/spanish/movies/     ← Mirror library (Spanish metadata)
    Inception (2010)/
        Inception.mkv  ──────────→ [hardlink to original, zero extra storage]
```

## Features

-   **Zero-Copy Mirroring** — Hardlinks share actual file data, mirrors use negligible disk space
-   **Per-User Language Control** — Each user sees only libraries matching their assigned language
-   **Watch Progress Syncs** — Since hardlinks point to the same file, watch history is automatically shared
-   **Automatic Sync** — Mirrors update after library scans and on a configurable schedule (default: every 6 hours)
-   **Auto-Manage New Users** — Optionally assign a default language to newly created users
-   **Built-in Diagnostics** — Generate debug reports for easy troubleshooting

## Requirements

-   Jellyfin Server **10.10.x** or higher
-   Source and mirror paths on the **same filesystem** (hardlinks can't cross mount points)
-   Write permissions to the mirror destination path

## Installation

### From Plugin Repository (Recommended)

1. **Dashboard → Plugins → Repositories → Add**
2. Enter:
    - Name: `Polyglot`
    - URL: `https://raw.githubusercontent.com/Maronato/jellyfin-plugin-polyglot/main/manifest.json`
3. **Catalog → Polyglot → Install**
4. Restart Jellyfin

### Manual Installation

Download from [Releases](https://github.com/Maronato/jellyfin-plugin-polyglot/releases) and extract to:

-   Linux: `/var/lib/jellyfin/plugins/Polyglot/`
-   Windows: `%APPDATA%\Jellyfin\Server\plugins\Polyglot\`
-   Docker: `/config/plugins/Polyglot/`

## Quick Start

### 1. Create a Language Alternative

1. Go to **Dashboard → Plugins → Polyglot → Languages tab**
2. Click the **+** button and fill in:
    - **Name**: Display name (e.g., "Spanish")
    - **Language**: Select from dropdown (e.g., "Spanish")
    - **Country** _(optional)_: For region-specific metadata (e.g., "Spain" for es-ES)
    - **Destination Path**: Where mirrors will be created (e.g., `/media/polyglot/spanish`)

### 2. Add Library Mirrors

1. Click the **+** button on your language alternative
2. Select a source library (e.g., "Movies")
3. Confirm the target path (auto-suggested based on your destination)
4. The plugin creates hardlinks and a new Jellyfin library with your target language's metadata

### 3. Assign Users

1. Go to the **Users tab**
2. For each user, select their language from the dropdown:
    - **Not managed** — Plugin doesn't control this user's library access
    - **Default libraries** — User sees original source libraries only
    - **[Language name]** — User sees that language's mirror libraries

> **Tip:** Use the "Enable Plugin for All Users" button to quickly enable management for all users at once.

## Docker Configuration

Hardlinks require source and destination to be on the **same filesystem**. In Docker, this means a **single mount point**:

```yaml
# ✅ Correct — single mount allows hardlinks
volumes:
  - /mnt/media:/media:rw
# Source: /media/movies → Mirror: /media/polyglot/spanish/movies

# ❌ Wrong — separate mounts break hardlinks
volumes:
  - /mnt/movies:/movies:rw
  - /mnt/mirrors:/mirrors:rw
```

## Settings

The **Settings tab** offers additional configuration:

| Setting                      | Description                                                             |
| ---------------------------- | ----------------------------------------------------------------------- |
| **Auto-manage new users**    | Automatically assign a default language to newly created users          |
| **Default language**         | Which language new users get (or "Default libraries" for source-only)   |
| **Sync after library scans** | Keep mirrors updated automatically after Jellyfin scans (default: on)   |
| **File exclusions**          | Customize which file extensions to skip (metadata, images)              |
| **Directory exclusions**     | Customize which directories to skip (e.g., `extrafanart`, `.trickplay`) |

## Troubleshooting

### Generate a Debug Report

1. Go to **Settings tab → Troubleshooting**
2. Select what to include (paths/names are anonymized by default for privacy)
3. Click **Generate Debug Report**
4. Copy the report and include it when opening an issue

### Common Issues

**Mirrors not syncing?**

-   Check that source and destination are on the same filesystem
-   Verify write permissions on the destination path
-   Look for errors in the mirror status (red error icon)

**Users seeing wrong libraries?**

-   Ensure the user is set to "managed" in the Users tab
-   Check that the user doesn't have "Enable access to all libraries" in their Jellyfin profile
-   Run the daily reconciliation task manually: **Dashboard → Scheduled Tasks → Polyglot User Library Sync**

## How It Works

### Mirroring

When you create a mirror, Polyglot:

1. Walks the source library directory
2. Creates **hardlinks** for media files (video, audio, subtitles)
3. **Skips** metadata files (`.nfo`, images) and metadata directories
4. Creates a Jellyfin library configured for your target language
5. Triggers a library scan to fetch metadata in the new language

### Library Access Control

When a user is assigned to a language:

-   ✅ They see mirror libraries for their language
-   ✅ They see source libraries that don't have a mirror in their language
-   ❌ They don't see mirrors for other languages
-   ✅ Non-managed libraries (like "Home Videos") remain accessible

### Automatic Synchronization

Mirrors stay in sync through:

-   **Post-scan hook** — Syncs after every Jellyfin library scan
-   **Scheduled task** — Runs every 6 hours by default
-   **Orphan cleanup** — Removes mirror configs when source libraries are deleted

## Contributing

Interested in contributing? Check out the [Developer Guide](Jellyfin.Plugin.Polyglot/README.md) for:

-   Architecture overview and system diagrams
-   Code organization and service documentation
-   Build and test instructions
-   Contribution guidelines and code style

## License

[GPL-3.0](LICENSE)
