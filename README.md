<div align="center">
<img src="docs/images/drageon-cover.png" alt="Drageon Logo" />

# Drageon
<p><strong>A portable local development environment for Windows</strong></p>

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows)
![License](https://img.shields.io/badge/license-MPL--2.0-green)
![Version](https://img.shields.io/github/v/release/LIVID-Media/Drageon?color=f59e0b&label=version)
</div>

**English** · [Deutsch](README.de.md)

Drageon brings your local web development stack into one lightweight Windows application. Manage **Apache or Nginx, PHP, MariaDB or MySQL, phpMyAdmin, Composer, Node.js, cURL, projects, virtual hosts, backups, snapshots, staging, cron jobs, test emails and logs** from a single interface.

Everything stays inside the Drageon folder: applications, projects, databases and configuration. Drageon does not rely on Windows services, does not write fixed installation paths to the registry and does not modify the system-wide `PATH`.

Copy it, move it or keep it on an external drive — your development environment stays together.

![Drageon dashboard](docs/images/dashboard-en.png)

## Why Drageon?

- **Truly portable** — paths are resolved relative to `Drageon.exe`.
- **One app for the complete local stack** — web server, PHP, database, projects and tools in one place.
- **Multiple versions side by side** — install and switch between component versions when needed.
- **Apache or Nginx** — choose the web server that fits your project.
- **MariaDB or MySQL** — both database providers are supported with separate data directories.
- **Backups and snapshots** — create SQL backups, restore databases, and capture complete project states before risky changes.
- **Portable developer shell** — PowerShell automatically receives the active PHP environment and selected CLI tools.
- **Project friendly** — folders under `www/` are detected automatically; virtual hosts are optional.
- **Built-in development helpers** — cron scheduler, SMTP catcher, centralized logs and tray controls.
- **Extensible** — add or edit package manifests without hard-coding every tool into the application.
- **Multilingual** — German, English, French, Spanish, Italian and Brazilian Portuguese are included.

Visit the project website: [drageon.livid-media.de](https://drageon.livid-media.de/)

## Quick start

1. Download and extract a Drageon release.
2. Start `Drageon.exe`.
3. Open **Components** and install the versions of your web server, PHP and database that you need.
4. Place projects inside `www/`.
5. Start the servers and open `http://localhost/`.

Components are downloaded only when you install them, so each Drageon setup contains only the runtimes and tools you actually use.

> Published builds are self-contained. No separate .NET installation is required on the target PC.

![Drageon localhost start page](docs/images/localhost-en.png)

## Components

| Category | Included support |
| --- | --- |
| Web servers | Apache HTTP Server, Nginx |
| PHP | Multiple versions, INI settings, extension management, OPcache |
| Databases | MariaDB, MySQL |
| Database UI | phpMyAdmin |
| PHP tooling | Composer |
| Frontend tooling | Node.js, npm, npx |
| CLI / diagnostics | cURL |
| Local services | Cron scheduler, SMTP catcher |

Installed versions live under `bin/` and can be activated, switched or removed from the app. MariaDB and MySQL keep their data separately under `data/`, so removing a server version does not automatically remove its databases.

![Drageon component management](docs/images/components-en.png)

## Projects and virtual hosts

Drageon automatically detects projects in `www/`. For example, `www/my-project/` is available at `http://localhost/my-project/`. Projects can have a display name, favorite status, and an assigned database.

Virtual hosts provide local domains such as `website.localhost` and can point to a project's public folder. The same definitions work with Apache and Nginx.

Project snapshots save files and, optionally, the assigned database and virtual host for later restoration. Symbolic links and junctions are preserved. Before restoring, the current state can be saved as an additional snapshot.

Staging creates a separate local copy for testing, optionally with its own database and virtual host. The application's database connection must be adjusted manually. Deleting a staging instance removes its files, snapshots, and resources created for that instance.

![Drageon project management](docs/images/projects-en.png)

## Database backups

The **Database Backups** page lists only user databases from the currently active MySQL or MariaDB version. One, several, or all databases can be backed up; every database receives its own SQL dump under `data/backups/databases/`. Existing backups can be restored into an explicitly selected target database or deleted after confirmation.

Database credentials are stored encrypted in the version-specific component settings and remain portable with the Drageon directory. Drageon uses the client tools supplied by the active database version. phpMyAdmin remains responsible for tables, SQL queries, users, permissions, collations, and structural changes.

## Portable PowerShell

PowerShell sessions started from Drageon receive a temporary development `PATH` containing the active PHP version and all enabled CLI components.

That means commands such as these can work without global installations:

```powershell
php -v
composer install
npm run dev
curl --version
```

Opening PowerShell from a project starts the terminal directly inside that project folder.

Drageon enables script execution only for the PowerShell process it starts. This allows Node.js wrappers such as `npm.ps1` to run even when Windows uses a restrictive execution policy, without changing the system-wide policy.

## PHP configuration

PHP settings are stored per installed version. Drageon can manage, among other things:

- `memory_limit`
- upload and POST size
- maximum execution time
- error display
- OPcache with a separate cache per PHP version
- detected PHP extensions
- additional custom INI directives

The generated configuration is used consistently by Apache, Nginx/PHP-FastCGI, PHP cron jobs and the portable PowerShell environment.

## Cron jobs and test emails

Drageon includes a lightweight scheduler that runs while the application is open. Jobs can execute:

- PHP scripts
- HTTP URLs
- cURL calls
- local programs, batch files and commands

Missed jobs can optionally be executed when Drageon starts again.

The built-in SMTP catcher listens on `127.0.0.1:1025` by default and requires neither TLS nor authentication. Incoming test messages and attachments are stored locally under `data/mail/` and can be inspected directly in the app.

![Drageon SMTP catcher](docs/images/mail-en.png)

## Package manifests

Drageon's component manager is driven by package manifests. New tools and releases can be added from the integrated manifest editor.

A package can define, for example:

- display name and category
- download page
- installation directory
- ZIP archive or single-file download
- release URL
- optional SHA256 checksum
- optional archive prefix
- browser launch path
- portable `PATH` integration

This keeps the application flexible without requiring every downloadable tool to be hard-coded.

<details>
<summary><strong>Portable folder structure</strong></summary>

```text
Drageon/
├─ Drageon.exe
├─ app/
│  ├─ config/
│  ├─ languages/
│  ├─ packages/
│  └─ var/
│     ├─ generated/
│     ├─ logs/
│     └─ tmp/
├─ bin/
│  ├─ apache/
│  ├─ composer/
│  ├─ curl/
│  ├─ mariadb/
│  ├─ mysql/
│  ├─ nginx/
│  ├─ node/
│  ├─ php/
│  └─ phpmyadmin/
├─ data/
│  ├─ backups/databases/
│  ├─ cron/
│  ├─ mail/
│  ├─ mariadb/
│  ├─ mysql/
│  └─ snapshots/
└─ www/
```

All paths are derived from the location of `Drageon.exe`. Stop processes started by Drageon before moving or copying the folder.

</details>

<details>
<summary><strong>Build from source</strong></summary>

Requirements:

- Windows 10 or 11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- no additional NuGet or Node packages

Create a self-contained Windows x64 build with:

```text
.\scripts\publish-drageon-win-x64.cmd
```

or:

```powershell
.\scripts\publish-drageon-win-x64.ps1
```

The portable build is generated under:

```text
publish/win-x64/
```

</details>

## Languages

Drageon currently includes:

- German
- English
- French
- Spanish
- Italian
- Portuguese (Brazil)

UI strings are stored in JSON language files, making additional translations easy to add.

## License

Drageon is open-source software licensed under the [Mozilla Public License 2.0](LICENSE).

## Support

If Drageon is useful to you and you would like to support its development:

- [Ko-fi](https://ko-fi.com/livid_media)
- [PayPal](https://www.paypal.com/donate/?hosted_button_id=93Z5AXMPT25EE)

## Disclaimer

Drageon is provided as-is. Use it at your own risk and keep backups of important projects and databases before making major changes.

Snapshot and staging operations show progress in the main window even after closing the project tools or changing pages. File processing also displays the number of processed entries.

Snapshot restoration does not stop or restart the web server. File exchange precedes SQL import, so a blocked exchange leaves the database unchanged. Successful rollback removes proven preparation directories and archives journals in data/recovery. Cleanup failures report retained paths; foreign replacement directories are preserved.

Transient directory rename refusals use bounded native retries with repeated identity checks. Existing destinations are never replaced; persistent locks abort safely and report the Windows error code.
