# Changelog

## 1.1.2

This release fixes a database recovery issue and updates some default packages.

- Simplified database recovery backup names and cleaned up temporary files after successful restores.
- Automatically refresh the backup list after restoration.
- Updated default Apache, cURL, Node.js, and PHP packages, including PHP 8.5.12 and 8.4.27 with fixes for the [PHP2Shell vulnerabilities](https://www.php.net/index.php#2026-10-21-1).

## 1.1.1

This release improves snapshot and staging reliability and usability.

- Added validated, compact ZIP snapshots that preserve symbolic links and junctions with memory-efficient processing.
- Improved restore safeguards and recovery for project files, databases, virtual hosts, and metadata.
- Made staging cleanup safer with ownership checks and removal of resources created by the staging instance.
- Added persistent progress, automatic list refresh, clearer localized dialogs, and shorter backup names.

## 1.1.0

This release adds portable database backup, project snapshot, restore, and local staging workflows while keeping project discovery filesystem-based.

- Added database backups for one, multiple, or all user databases of the active MySQL or MariaDB version, including explicit restore targets and backup deletion.
- Added encrypted, portable database credentials to the version-specific MySQL and MariaDB component settings.
- Added project snapshots with optional database, project metadata, and virtual-host data. Files, dotfiles, empty directories, symbolic links, directory links, and junctions are preserved without dereferencing external targets.
- Added snapshot restore with an optional safety snapshot of the current project state before replacement.
- Added local staging creation with portable project copies, link-aware paths, optional database and virtual-host copies, numbered suggestions for multiple staging instances, and complete staging cleanup.
- Kept `www/` as the sole source of existing projects. `app/config/projects.json` now stores only explicitly configured metadata such as display name, favorite state, database, URL, virtual host, and staging relationship.
- Added optional project assignments to virtual hosts and automatic metadata updates when a virtual host is changed or removed.
- Reworked the Projects and Database Backups interfaces with compact icon actions, clearer project hierarchy, localized date formatting, and expanded multilingual help.

## 1.0.1

This release focuses on lower idle memory usage and several usability improvements.

- Significantly reduced idle memory usage by releasing inactive views and optimizing UI rendering.
- Reworked tray integration and main-window lifecycle to free resources while Drageon runs in the background.
- Improved component-list virtualization to reduce memory usage on the Packages page.
- Restored automatic loading of virtual hosts when opening the page.
- Extended log search to include timestamps and component names.
- Sorted the log component filter alphabetically and limited it to components with available log entries.
