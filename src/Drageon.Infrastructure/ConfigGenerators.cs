using System.Text;
using System.Text.RegularExpressions;
using Drageon.Core;

namespace Drageon.Infrastructure;

public abstract class ConfigGeneratorBase(IPortablePathService paths, JsonFileStore store)
{
    protected static readonly UTF8Encoding Utf8WithoutBom = new(false);
    protected IPortablePathService Paths { get; } = paths;
    protected JsonFileStore Store { get; } = store;

    protected async Task<string?> GetActiveVersionAsync(string packageId, CancellationToken cancellationToken)
    {
        var active = await Store.ReadAsync<Dictionary<string, string>>(
            Paths.Resolve("app", "config", "packages.active.json"), cancellationToken).ConfigureAwait(false);
        return active?.GetValueOrDefault(packageId);
    }

    protected static string ApachePath(string path) => path.Replace('\\', '/');
}

public sealed class PhpConfigGenerator(IPortablePathService paths, JsonFileStore store)
    : ConfigGeneratorBase(paths, store), IConfigGenerator
{
    public string ComponentId => "php";

    public async Task<string> GenerateAsync(CancellationToken cancellationToken = default)
    {
        var phpVersion = await GetActiveVersionAsync("php", cancellationToken).ConfigureAwait(false);
        if (phpVersion is null)
        {
            return await GenerateCoreAsync(null, new PhpSettings(), cancellationToken).ConfigureAwait(false);
        }

        return await GenerateForVersionAsync(phpVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GenerateForVersionAsync(string phpVersion, CancellationToken cancellationToken = default)
    {
        var settings = await PhpVersionConfiguration.LoadAsync(Paths, Store, phpVersion, cancellationToken).ConfigureAwait(false);
        return await GenerateCoreAsync(phpVersion, settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GenerateCoreAsync(string? phpVersion, PhpSettings settings, CancellationToken cancellationToken)
    {
        var outputPath = phpVersion is null
            ? Paths.Resolve("app", "var", "generated", "php", "php.ini")
            : PhpVersionConfiguration.GetIniPath(Paths, phpVersion);
        var opcachePath = phpVersion is null
            ? Paths.Resolve("app", "var", "tmp", ".opcache")
            : PhpVersionConfiguration.GetOpcacheDirectory(Paths, phpVersion);
        Directory.CreateDirectory(opcachePath);
        var phpRoot = phpVersion is null ? null : Paths.Resolve("bin", "php", phpVersion);
        var templatePath = phpRoot is null ? null : new[] { "php.ini-development", "php.ini-production", "php.ini" }
            .Select(name => Path.Combine(phpRoot, name)).FirstOrDefault(File.Exists);
        var template = templatePath is null
            ? "; Keine aktive PHP-Version oder Original-INI-Vorlage gefunden.\r\n"
            : await File.ReadAllTextAsync(templatePath, cancellationToken).ConfigureAwait(false);
        var lines = template.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();

        SetDirective(lines, "memory_limit", settings.MemoryLimit);
        SetDirective(lines, "upload_max_filesize", settings.UploadMaxFilesize);
        SetDirective(lines, "post_max_size", settings.PostMaxSize);
        SetDirective(lines, "max_execution_time", settings.MaxExecutionTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetDirective(lines, "display_errors", settings.DisplayErrors ? "On" : "Off");
        SetDirective(lines, "log_errors", "On");
        SetDirective(lines, "variables_order", "\"EGPCS\"");
        SetDirective(lines, "error_log", $"\"{ApachePath(Paths.Resolve("app", "var", "logs", "php", "php-error.log"))}\"");
        SetDirective(lines, "opcache.enable", settings.OpcacheEnabled ? "1" : "0");
        SetDirective(lines, "opcache.file_cache", $"\"{ApachePath(opcachePath)}\"");
        if (phpRoot is not null)
        {
            SetDirective(lines, "extension_dir", $"\"{ApachePath(Path.Combine(phpRoot, "ext"))}\"");
        }

        var loadableExtensions = phpRoot is null
            ? null
            : await PhpExtensionProbe.GetLoadableAsync(phpRoot, settings.Extensions, cancellationToken).ConfigureAwait(false);
        var enabledExtensions = loadableExtensions is null
            ? settings.Extensions
            : settings.Extensions.Where(extension => loadableExtensions.Contains(PhpExtensionProbe.Normalize(extension))).ToArray();
        var skippedExtensions = loadableExtensions is null
            ? []
            : settings.Extensions.Where(extension => !loadableExtensions.Contains(PhpExtensionProbe.Normalize(extension))).ToArray();
        SetExtensions(lines, enabledExtensions);
        SetOpcacheExtension(lines, settings.OpcacheEnabled && phpRoot is not null && File.Exists(Path.Combine(phpRoot, "ext", "php_opcache.dll")));

        var builder = new StringBuilder()
            .AppendLine("; Automatisch durch Drageon aus der Original-INI erzeugt. Änderungen werden überschrieben.")
            .AppendLine($"; Grundlage: {Path.GetFileName(templatePath) ?? "keine"}")
            .AppendLine();
        if (skippedExtensions.Length > 0)
        {
            builder.AppendLine($"; Nicht ladbare Erweiterungen wurden deaktiviert: {string.Join(", ", skippedExtensions)}");
        }
        builder.AppendLine().Append(string.Join("\r\n", lines));
        if (!string.IsNullOrWhiteSpace(settings.CustomIni))
        {
            builder.AppendLine().AppendLine().AppendLine("; Benutzerdefinierte Einstellungen").AppendLine(settings.CustomIni);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, builder.ToString(), Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
        return outputPath;
    }

    private static void SetDirective(List<string> lines, string name, string value)
    {
        var pattern = new Regex($@"^\s*;?\s*{Regex.Escape(name)}\s*=", RegexOptions.IgnoreCase);
        var index = lines.FindIndex(line => pattern.IsMatch(line));
        var replacement = $"{name} = {value}";
        if (index >= 0) lines[index] = replacement;
        else lines.Add(replacement);
    }

    private static void SetExtensions(List<string> lines, IReadOnlyList<string> enabledExtensions)
    {
        var enabled = enabledExtensions.Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(PhpExtensionProbe.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Nur echte Direktiven wie "extension=curl" oder ";extension=curl" ändern.
        // Eingerückte Beispiele in der Originaldatei (";   extension=/path/...") bleiben Kommentare.
        var pattern = new Regex(@"^\s*;?extension\s*=\s*(?<value>[^;\r\n]+)", RegexOptions.IgnoreCase);
        for (var index = 0; index < lines.Count; index++)
        {
            var match = pattern.Match(lines[index]);
            if (!match.Success) continue;
            var originalValue = match.Groups["value"].Value.Trim().Trim('"', '\'');
            var normalized = PhpExtensionProbe.Normalize(originalValue);
            found.Add(normalized);
            lines[index] = enabled.Contains(normalized) ? $"extension={originalValue}" : $";extension={originalValue}";
        }

        foreach (var extension in enabledExtensions.Where(value => !found.Contains(PhpExtensionProbe.Normalize(value))))
        {
            lines.Add($"extension={Path.GetFileName(extension)}");
        }
    }

    private static void SetOpcacheExtension(List<string> lines, bool enabled)
    {
        var pattern = new Regex(@"^\s*;?\s*zend_extension\s*=\s*(?<value>[^;\r\n]+)", RegexOptions.IgnoreCase);
        var index = lines.FindIndex(line =>
        {
            var match = pattern.Match(line);
            return match.Success && match.Groups["value"].Value.Contains("opcache", StringComparison.OrdinalIgnoreCase);
        });
        var replacement = enabled ? "zend_extension=php_opcache.dll" : ";zend_extension=php_opcache.dll";
        if (index >= 0) lines[index] = replacement;
        else lines.Add(replacement);
    }

}

public sealed class ApacheConfigGenerator(IPortablePathService paths, JsonFileStore store)
    : ConfigGeneratorBase(paths, store), IConfigGenerator
{
    public string ComponentId => "apache";

    public async Task<string> GenerateAsync(CancellationToken cancellationToken = default)
    {
        var apacheVersion = await GetActiveVersionAsync("apache", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Es ist keine Apache-Version aktiviert.");
        return await GenerateForVersionAsync(apacheVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GenerateForVersionAsync(string apacheVersion, CancellationToken cancellationToken = default)
    {
        var settings = await VersionedComponentConfiguration.LoadAsync(
            Paths, Store, "apache", apacheVersion, static () => new ApacheSettings(), cancellationToken).ConfigureAwait(false);
        var appSettings = await Store.ReadAsync<AppSettings>(Paths.Resolve("app", "config", "settings.json"), cancellationToken).ConfigureAwait(false) ?? new();
        var httpPort = appSettings.HttpPort;
        var apacheRoot = Paths.Resolve("bin", "apache", apacheVersion);
        var moduleRoot = Path.Combine(apacheRoot, "modules");
        var serverRoot = Paths.AppRoot;
        string ServerPath(string path) => ApachePath(Path.GetRelativePath(serverRoot, path));
        var wwwRoot = Paths.Resolve("www");
        var outputPath = Path.Combine(VersionedComponentConfiguration.GetGeneratedDirectory(Paths, "apache", apacheVersion), "httpd.conf");
        var phpVersion = await GetActiveVersionAsync("php", cancellationToken).ConfigureAwait(false);

        var builder = new StringBuilder()
            .AppendLine("# Automatisch durch Drageon erzeugt. Änderungen werden überschrieben.")
            .AppendLine($"Define APPROOT \"{ServerPath(Paths.AppRoot)}\"")
            .AppendLine($"Listen {httpPort}")
            .AppendLine($"ServerName {settings.ServerName}:{httpPort}")
            .AppendLine($"PidFile \"{ServerPath(Paths.Resolve("app", "var", "tmp", "httpd.pid"))}\"")
            .AppendLine($"ThreadsPerChild {Math.Clamp(settings.ThreadsPerChild, 8, 256)}")
            .AppendLine($"LogLevel {SafeLogLevel(settings.LogLevel)}")
            .AppendLine("AccessFileName .htaccess")
            .AppendLine($"LoadModule access_compat_module \"{ServerPath(Path.Combine(moduleRoot, "mod_access_compat.so"))}\"")
            .AppendLine($"LoadModule alias_module \"{ServerPath(Path.Combine(moduleRoot, "mod_alias.so"))}\"")
            .AppendLine($"LoadModule autoindex_module \"{ServerPath(Path.Combine(moduleRoot, "mod_autoindex.so"))}\"")
            .AppendLine($"LoadModule authz_core_module \"{ServerPath(Path.Combine(moduleRoot, "mod_authz_core.so"))}\"")
            .AppendLine($"LoadModule authz_host_module \"{ServerPath(Path.Combine(moduleRoot, "mod_authz_host.so"))}\"")
            .AppendLine($"LoadModule dir_module \"{ServerPath(Path.Combine(moduleRoot, "mod_dir.so"))}\"")
            .AppendLine($"LoadModule env_module \"{ServerPath(Path.Combine(moduleRoot, "mod_env.so"))}\"")
            .AppendLine($"LoadModule log_config_module \"{ServerPath(Path.Combine(moduleRoot, "mod_log_config.so"))}\"")
            .AppendLine($"LoadModule mime_module \"{ServerPath(Path.Combine(moduleRoot, "mod_mime.so"))}\"")
            .AppendLine($"LoadModule rewrite_module \"{ServerPath(Path.Combine(moduleRoot, "mod_rewrite.so"))}\"")
            .AppendLine($"LoadModule setenvif_module \"{ServerPath(Path.Combine(moduleRoot, "mod_setenvif.so"))}\"")
            .AppendOptionalModule(moduleRoot, "filter_module", "mod_filter.so", ServerPath)
            .AppendOptionalModule(moduleRoot, "deflate_module", "mod_deflate.so", ServerPath)
            .AppendOptionalModule(moduleRoot, "headers_module", "mod_headers.so", ServerPath)
            .AppendOptionalModule(moduleRoot, "expires_module", "mod_expires.so", ServerPath)
            .AppendOptionalModule(moduleRoot, "negotiation_module", "mod_negotiation.so", ServerPath)
            .AppendLine($"DocumentRoot \"{ApachePath(wwwRoot)}\"")
            .AppendLine($"<Directory \"{ApachePath(wwwRoot)}\">")
            .AppendLine($"    Options {DirectoryOptions(settings.DirectoryListing)}")
            .AppendLine($"    AllowOverride {(settings.AllowHtaccess ? "All" : "None")}")
            .AppendLine("    Require all granted")
            .AppendLine("</Directory>")
            .AppendLine("DirectoryIndex index.php index.html")
            .AppendLine($"TypesConfig \"{ServerPath(Path.Combine(apacheRoot, "conf", "mime.types"))}\"")
            .AppendLine($"ErrorLog \"{ServerPath(Paths.Resolve("app", "var", "logs", "apache", "error.log"))}\"")
            .AppendLine("LogFormat \"%h %l %u %t \\\"%r\\\" %>s %b\" common")
            .AppendLine($"CustomLog \"{ServerPath(Paths.Resolve("app", "var", "logs", "apache", "access.log"))}\" common");

        if (phpVersion is not null)
        {
            var phpRoot = Paths.Resolve("bin", "php", phpVersion);
            var phpExecutable = Path.Combine(phpRoot, "php.exe");
            var phpConfigRoot = PhpVersionConfiguration.GetGeneratedDirectory(Paths, phpVersion);
            var cliPath = phpRoot + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
            builder
                .AppendLine($"LoadModule php_module \"{ServerPath(Path.Combine(phpRoot, "php8apache2_4.dll"))}\"")
                .AppendLine("AddHandler application/x-httpd-php .php")
                .AppendLine($"PHPIniDir \"{ServerPath(phpConfigRoot)}\"")
                .AppendLine("# CLI-Unterprozesse aus Webanwendungen verwenden dieselbe portable PHP-Konfiguration.")
                .AppendLine($"SetEnv PHPRC \"{ApacheEnvironmentValue(phpConfigRoot)}\"")
                .AppendLine($"SetEnv DRAGEON_PHP_INI \"{ApacheEnvironmentValue(Path.Combine(phpConfigRoot, "php.ini"))}\"")
                .AppendLine($"SetEnv DRAGEON_ROOT \"{ApacheEnvironmentValue(Paths.AppRoot)}\"")
                // Some Windows tools pass the discovered binary to `start /b`. A forward slash
                // causes their shell escaping to quote the executable, which cmd then mistakes
                // for a window title. Keep executable discovery paths in native Windows form.
                .AppendLine($"SetEnv PHP_PATH \"{ApacheNativeEnvironmentValue(phpExecutable)}\"")
                .AppendLine($"SetEnv PHP_PEAR_PHP_BIN \"{ApacheNativeEnvironmentValue(phpExecutable)}\"")
                .AppendLine($"SetEnv DRAGEON_PHP_BINARY \"{ApacheNativeEnvironmentValue(phpExecutable)}\"")
                .AppendLine($"SetEnv PATH \"{ApacheNativeEnvironmentValue(cliPath)}\"");
        }

        builder
            .AppendLine($"<VirtualHost *:{httpPort}>")
            .AppendLine($"    ServerName {settings.ServerName}")
            .AppendLine($"    DocumentRoot \"{ApachePath(wwwRoot)}\"")
            .AppendLine($"    <Directory \"{ApachePath(wwwRoot)}\">")
            .AppendLine($"        Options {DirectoryOptions(settings.DirectoryListing)}")
            .AppendLine($"        AllowOverride {(settings.AllowHtaccess ? "All" : "None")}")
            .AppendLine("        Require all granted")
            .AppendLine("    </Directory>")
            .AppendLine("</VirtualHost>");

        var phpMyAdminVersion = await GetActiveVersionAsync("phpmyadmin", cancellationToken).ConfigureAwait(false);
        if (phpMyAdminVersion is not null)
        {
            var phpMyAdminRoot = Paths.Resolve("bin", "phpmyadmin", phpMyAdminVersion);
            builder
                .AppendLine($"Alias /phpmyadmin \"{ApachePath(phpMyAdminRoot)}/\"")
                .AppendLine($"<Directory \"{ApachePath(phpMyAdminRoot)}\">")
                .AppendLine("    Options FollowSymLinks")
                .AppendLine("    AllowOverride None")
                .AppendLine("    Require local")
                .AppendLine("</Directory>");
        }

        var sharedHosts = (await new VirtualHostStore(Paths, Store).ReadAsync(cancellationToken))
            .Where(host => host.Enabled).ToArray();
        foreach (var port in sharedHosts.Select(host => host.Port ?? httpPort).Distinct().Where(port => port != httpPort))
            builder.AppendLine($"Listen {port}");
        foreach (var host in sharedHosts)
        {
            builder.AppendLine($"<VirtualHost *:{host.Port ?? httpPort}>")
                .AppendLine($"    ServerName {host.Host}")
                .AppendLine($"    DocumentRoot \"{ApachePath(host.DocumentRoot)}\"")
                .AppendLine($"    <Directory \"{ApachePath(host.DocumentRoot)}\">")
                .AppendLine($"        Options {DirectoryOptions(settings.DirectoryListing)}")
                .AppendLine($"        AllowOverride {(settings.AllowHtaccess ? "All" : "None")}")
                .AppendLine("        Require all granted")
                .AppendLine("    </Directory>");
            AppendIndented(builder, host.GetServerConfiguration(ComponentId), 4);
            builder
                .AppendLine("</VirtualHost>");
        }
        if (!string.IsNullOrWhiteSpace(settings.CustomConfiguration))
        {
            builder.AppendLine().AppendLine("# Benutzerdefinierte Einstellungen").AppendLine(settings.CustomConfiguration);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, builder.ToString(), Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
        return outputPath;
    }

    private static string SafeLogLevel(string value) => value.ToLowerInvariant() is "debug" or "info" or "notice" or "warn" or "error" or "crit"
        ? value.ToLowerInvariant() : "warn";

    private static string DirectoryOptions(bool directoryListing) =>
        directoryListing ? "Indexes FollowSymLinks" : "FollowSymLinks";

    private static string ApacheEnvironmentValue(string value) => value
        .Replace('\\', '/')
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", "", StringComparison.Ordinal);

    private static string ApacheNativeEnvironmentValue(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", "", StringComparison.Ordinal);

    private static void AppendIndented(StringBuilder builder, string value, int spaces)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var prefix = new string(' ', spaces);
        foreach (var line in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
            builder.Append(prefix).AppendLine(line);
    }
}

public sealed class PhpMyAdminConfigGenerator(IPortablePathService paths, JsonFileStore store)
    : ConfigGeneratorBase(paths, store), IConfigGenerator
{
    public string ComponentId => "phpmyadmin";

    public async Task<string> GenerateAsync(CancellationToken cancellationToken = default)
    {
        var version = await GetActiveVersionAsync("phpmyadmin", cancellationToken).ConfigureAwait(false);
        return version is null
            ? Paths.Resolve("app", "var", "generated", "phpmyadmin", "config.inc.php")
            : await GenerateForVersionAsync(version, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GenerateForVersionAsync(string version, CancellationToken cancellationToken = default)
    {
        var settingsPath = VersionedComponentConfiguration.GetSettingsPath(Paths, "phpmyadmin", version);
        var settings = await VersionedComponentConfiguration.LoadAsync(
            Paths, Store, "phpmyadmin", version, static () => new PhpMyAdminSettings(), cancellationToken).ConfigureAwait(false);
        if (settings.BlowfishSecret.Length < 32)
        {
            settings = settings with { BlowfishSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
            await Store.WriteAsync(settingsPath, settings, cancellationToken).ConfigureAwait(false);
        }

        var tempDirectory = Paths.Resolve("app", "var", "tmp", "phpmyadmin", version);
        Directory.CreateDirectory(tempDirectory);
        var outputPath = Path.Combine(
            VersionedComponentConfiguration.GetGeneratedDirectory(Paths, "phpmyadmin", version), "config.inc.php");
        var builder = new StringBuilder()
            .AppendLine("<?php")
            .AppendLine("// Automatisch durch Drageon erzeugt. Änderungen werden überschrieben.")
            .AppendLine($"$cfg['blowfish_secret'] = {PhpString(settings.BlowfishSecret)};")
            .AppendLine("$i = 1;")
            .AppendLine($"$cfg['Servers'][$i]['auth_type'] = {PhpString(settings.AuthType is "http" ? "http" : "cookie")};")
            .AppendLine($"$cfg['Servers'][$i]['host'] = {PhpString(settings.DatabaseHost)};")
            .AppendLine($"$cfg['Servers'][$i]['port'] = {PhpString(settings.DatabasePort.ToString(System.Globalization.CultureInfo.InvariantCulture))};")
            .AppendLine("$cfg['Servers'][$i]['connect_type'] = 'tcp';")
            .AppendLine($"$cfg['Servers'][$i]['AllowNoPassword'] = {(settings.AllowNoPassword ? "true" : "false")};")
            .AppendLine($"$cfg['LoginCookieValidity'] = {Math.Clamp(settings.LoginCookieValidity, 60, 86400)};")
            .AppendLine($"$cfg['MaxRows'] = {Math.Clamp(settings.MaxRows, 10, 1000)};")
            .AppendLine($"$cfg['ShowServerInfo'] = {(settings.ShowServerInfo ? "true" : "false")};")
            .AppendLine($"$cfg['TempDir'] = {PhpString(ApachePath(tempDirectory))};");
        if (!string.IsNullOrWhiteSpace(settings.CustomConfiguration))
        {
            builder.AppendLine().AppendLine("// Benutzerdefinierte Einstellungen").AppendLine(settings.CustomConfiguration);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, builder.ToString(), Utf8WithoutBom, cancellationToken).ConfigureAwait(false);

        var target = Paths.Resolve("bin", "phpmyadmin", version, "config.inc.php");
        File.Copy(outputPath, target, true);
        return outputPath;
    }

    private static string PhpString(string value) => $"'{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}'";
}

internal static class ApacheConfigBuilderExtensions
{
    public static StringBuilder AppendOptionalModule(this StringBuilder builder, string moduleRoot, string moduleName, string fileName, Func<string, string> pathFormatter)
    {
        var file = Path.Combine(moduleRoot, fileName);
        if (File.Exists(file)) builder.AppendLine($"LoadModule {moduleName} \"{pathFormatter(file)}\"");
        return builder;
    }
}

public sealed class MariaDbConfigGenerator(IPortablePathService paths, JsonFileStore store)
    : ConfigGeneratorBase(paths, store), IConfigGenerator
{
    public string ComponentId => "mariadb";

    public async Task<string> GenerateAsync(CancellationToken cancellationToken = default)
    {
        var mariaDbVersion = await GetActiveVersionAsync("mariadb", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Es ist keine MariaDB-Version aktiviert.");
        return await GenerateForVersionAsync(mariaDbVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GenerateForVersionAsync(string mariaDbVersion, CancellationToken cancellationToken = default)
    {
        var settings = await VersionedComponentConfiguration.LoadAsync(
            Paths, Store, "mariadb", mariaDbVersion, static () => new MariaDbSettings(), cancellationToken).ConfigureAwait(false);
        var outputPath = Path.Combine(VersionedComponentConfiguration.GetGeneratedDirectory(Paths, "mariadb", mariaDbVersion), "my.ini");
        var dataDirectory = VersionedComponentConfiguration.GetDataDirectory(Paths, "mariadb", mariaDbVersion);
        var baseDirectory = Paths.Resolve("bin", "mariadb", mariaDbVersion);
        Directory.CreateDirectory(dataDirectory);
        var builder = new StringBuilder()
            .AppendLine("[mysqld]")
            .AppendLine("# Automatisch durch Drageon erzeugt. Änderungen werden überschrieben.")
            .AppendLine($"port={settings.Port}")
            .AppendLine($"basedir=\"{ApachePath(baseDirectory)}\"")
            .AppendLine($"datadir=\"{ApachePath(dataDirectory)}\"")
            .AppendLine($"character-set-server={settings.CharacterSet}")
            .AppendLine($"max_connections={settings.MaxConnections}")
            .AppendLine($"innodb_buffer_pool_size={SafeMemorySize(settings.BufferPoolSize)}")
            .AppendLine($"log_error=\"{ApachePath(Paths.Resolve("app", "var", "logs", "mariadb", "error.log"))}\"");

        if (!string.IsNullOrWhiteSpace(settings.CustomConfiguration))
        {
            builder.AppendLine().AppendLine("# Benutzerdefinierte Einstellungen").AppendLine(settings.CustomConfiguration);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, builder.ToString(), Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
        return outputPath;
    }

    private static string SafeMemorySize(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, "^[1-9][0-9]*[KMG]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? value.ToUpperInvariant()
            : "64M";
}

public sealed class MySqlConfigGenerator(IPortablePathService paths, JsonFileStore store)
    : ConfigGeneratorBase(paths, store), IConfigGenerator
{
    public string ComponentId => "mysql";

    public async Task<string> GenerateAsync(CancellationToken cancellationToken = default)
    {
        var version = await GetActiveVersionAsync(ComponentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Es ist keine MySQL-Version aktiviert.");
        return await GenerateForVersionAsync(version, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GenerateForVersionAsync(string version, CancellationToken cancellationToken = default)
    {
        var settings = await VersionedComponentConfiguration.LoadAsync(
            Paths, Store, ComponentId, version, static () => new MySqlSettings(), cancellationToken).ConfigureAwait(false);
        var outputPath = Path.Combine(VersionedComponentConfiguration.GetGeneratedDirectory(Paths, ComponentId, version), "my.ini");
        var baseDirectory = Paths.Resolve("bin", ComponentId, version);
        var dataDirectory = VersionedComponentConfiguration.GetDataDirectory(Paths, ComponentId, version);
        Directory.CreateDirectory(dataDirectory);
        var builder = new StringBuilder()
            .AppendLine("[mysqld]")
            .AppendLine("# Automatisch durch Drageon erzeugt. Änderungen werden überschrieben.")
            .AppendLine($"port={settings.Port}")
            .AppendLine($"basedir=\"{ApachePath(baseDirectory)}\"")
            .AppendLine($"datadir=\"{ApachePath(dataDirectory)}\"")
            .AppendLine($"character-set-server={settings.CharacterSet}")
            .AppendLine($"max_connections={settings.MaxConnections}")
            .AppendLine($"innodb_buffer_pool_size={SafeMemorySize(settings.BufferPoolSize)}")
            .AppendLine($"log_error=\"{ApachePath(Paths.Resolve("app", "var", "logs", ComponentId, "error.log"))}\"");

        if (!string.IsNullOrWhiteSpace(settings.CustomConfiguration))
        {
            builder.AppendLine().AppendLine("# Benutzerdefinierte Einstellungen").AppendLine(settings.CustomConfiguration);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, builder.ToString(), Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
        return outputPath;
    }

    private static string SafeMemorySize(string value) =>
        Regex.IsMatch(value, "^[1-9][0-9]*[KMG]$", RegexOptions.IgnoreCase)
            ? value.ToUpperInvariant()
            : "64M";
}

