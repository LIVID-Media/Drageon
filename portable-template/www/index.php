<?php
declare(strict_types=1);

if (isset($_GET['phpinfo']) && $_GET['phpinfo'] === 'frame') {
	phpinfo();
	exit;
}

$webLanguage = [];
$webLanguageCode = 'de';

/**
 * Drageon start page
 * Drop this file into the www root. It automatically lists subdirectories
 * as local projects and detects common local development components.
 */

function h(mixed $value): string
{
	return htmlspecialchars((string)$value, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
}

function webText(string $key, array $replace = []): string
{
	global $webLanguage;
	$value = isset($webLanguage[$key]) && is_string($webLanguage[$key]) ? $webLanguage[$key] : $key;
	return $replace ? strtr($value, $replace) : $value;
}

function loadWebLanguage(string $appRoot): array
{
	$settings = readJsonFile($appRoot . DIRECTORY_SEPARATOR . 'app' . DIRECTORY_SEPARATOR . 'config' . DIRECTORY_SEPARATOR . 'settings.json');
	$requested = preg_replace('/[^a-zA-Z0-9_-]/', '', (string) ($settings['language'] ?? 'de')) ?: 'de';
	$directory = $appRoot . DIRECTORY_SEPARATOR . 'app' . DIRECTORY_SEPARATOR . 'languages' . DIRECTORY_SEPARATOR . 'web';
	$catalog = readJsonFile($directory . DIRECTORY_SEPARATOR . $requested . '.json');
	if (!$catalog && $requested !== 'de') {
		$requested = 'de';
		$catalog = readJsonFile($directory . DIRECTORY_SEPARATOR . 'de.json');
	}
	return [$requested, $catalog];
}

function detectWebServer(): array
{
    $raw = (string) ($_SERVER['SERVER_SOFTWARE'] ?? '');
    if (function_exists('apache_get_version')) {
        $raw = (string) @apache_get_version();
    }
    $settings = readJsonFile(dirname(__DIR__) . '/app/config/settings.json');
    $configured = (string) ($settings['webServer'] ?? 'apache');
    $name = '';
    $version = null;
    if (preg_match('/^([a-z][a-z0-9_-]*)(?:\/([0-9]+(?:\.[0-9]+){1,3}))?/i', trim($raw), $match)) {
        $name = $match[1];
        $version = $match[2] ?? null;
    }
    return [
        'name' => $name !== '' ? $name : ucfirst($configured),
        'version' => $version ?: webText('detection.notRecognized'),
        'online' => $raw !== '',
        'raw' => $raw,
        'configured' => ucfirst($configured),
        'httpPort' => (int) ($_SERVER['SERVER_PORT'] ?? $settings['httpPort'] ?? 80),
        'fastCgi' => PHP_SAPI === 'cgi-fcgi'
            ? '127.0.0.1:' . (int) ($settings['fastCgiPort'] ?? 9000) : null,
    ];
}
function shellExecAvailable(): bool
{
	if (!function_exists('shell_exec')) {
		return false;
	}

	$disabled = array_filter(array_map('trim', explode(',', (string) ini_get('disable_functions'))));
	return !in_array('shell_exec', $disabled, true);
}

function isLocalTcpPortListening(int $port): bool
{
	if ($port < 1 || $port > 65535 || !shellExecAvailable()) {
		return false;
	}

	// Eine echte Verbindung würde den Datenbankserver ohne Anmeldung wieder schließen und dadurch
	// irreführende "Aborted connection / unauthenticated"-Warnungen erzeugen. netstat
	// prüft den lokalen Listener passiv, ohne den Datenbankserver zu kontaktieren.
	$netstat = (string) @shell_exec('netstat -an -p tcp 2>NUL');
	if ($netstat === '') {
		return false;
	}

	$portPattern = preg_quote((string) $port, '/');
	return preg_match(
					'/^\s*TCP\s+(?:127\.0\.0\.1|0\.0\.0\.0|\[::1\]|\[::\]):' . $portPattern
					. '\s+(?:0\.0\.0\.0:0|\[::\]:0)\s+\S+\s*$/mi',
					$netstat
			) === 1;
}

function detectDatabaseVersion(): array
{
	$appRoot = dirname(__DIR__);
	$activePackages = readJsonFile($appRoot . DIRECTORY_SEPARATOR . 'app' . DIRECTORY_SEPARATOR
			. 'config' . DIRECTORY_SEPARATOR . 'packages.active.json');
	$provider = isset($activePackages['mysql']) ? 'mysql' : (isset($activePackages['mariadb']) ? 'mariadb' : null);
	$configuredVersion = $provider !== null ? (string) $activePackages[$provider] : null;
	$settings = [];
	if ($configuredVersion && basename($configuredVersion) === $configuredVersion) {
		$settings = readJsonFile($appRoot . DIRECTORY_SEPARATOR . 'app' . DIRECTORY_SEPARATOR
				. 'config' . DIRECTORY_SEPARATOR . $provider . DIRECTORY_SEPARATOR
				. $configuredVersion . DIRECTORY_SEPARATOR . 'settings.json');
	}

	$host = getenv('DRAGEON_DB_HOST') ?: '127.0.0.1';
	$port = (int) (getenv('DRAGEON_DB_PORT') ?: ($settings['port'] ?? 3306));

	if (isLocalTcpPortListening($port)) {
		return [
				'name' => $provider === 'mysql' ? 'MySQL' : 'MariaDB',
				'version' => $configuredVersion ?: webText('detection.reachable'),
				'online' => true,
				'raw' => webText('detection.endpointReachable', ['{endpoint}' => "$host:$port"]),
		];
	}

	if ($configuredVersion) {
		return [
				'name' => $provider === 'mysql' ? 'MySQL' : 'MariaDB',
				'version' => $configuredVersion,
				'online' => false,
				'raw' => webText('detection.activeVersionOffline', ['{endpoint}' => "$host:$port"]),
		];
	}

	return [
			'name' => webText('database.name'),
			'version' => webText('detection.notRecognized'),
			'online' => false,
			'raw' => '',
	];
}

function readJsonFile(string $file): array
{
	if (!is_file($file) || !is_readable($file)) {
		return [];
	}

	$json = json_decode((string) @file_get_contents($file), true);
	return is_array($json) ? $json : [];
}

function detectProject(string $path, string $folder): array
{
	$type = 'Webprojekt';
	$description = '';
	$tags = [];

	$composer = readJsonFile($path . DIRECTORY_SEPARATOR . 'composer.json');
	$package = readJsonFile($path . DIRECTORY_SEPARATOR . 'package.json');

	if (is_file($path . DIRECTORY_SEPARATOR . 'wp-config.php') || is_dir($path . DIRECTORY_SEPARATOR . 'wp-content')) {
		$type = 'WordPress';
		$tags[] = 'WordPress';
	} elseif (is_file($path . DIRECTORY_SEPARATOR . 'public/contao-manager.phar.php')) {
		$type = 'Contao';
		$tags[] = 'Contao';
		$tags[] = 'Symfony';
	} elseif (is_file($path . DIRECTORY_SEPARATOR . 'artisan')) {
		$type = 'Laravel';
		$tags[] = 'Laravel';
	} elseif (is_file($path . DIRECTORY_SEPARATOR . 'symfony.lock') || is_file($path . DIRECTORY_SEPARATOR . 'bin' . DIRECTORY_SEPARATOR . 'console')) {
		$type = 'Symfony';
		$tags[] = 'Symfony';
	} elseif (!empty($composer)) {
		$type = 'PHP / Composer';
		$tags[] = 'Composer';
	} elseif (!empty($package)) {
		$type = 'Node / Frontend';
		$tags[] = 'Node';
	} elseif (is_file($path . DIRECTORY_SEPARATOR . 'index.php')) {
		$type = 'PHP';
		$tags[] = 'PHP';
	} elseif (is_file($path . DIRECTORY_SEPARATOR . 'index.html') || is_file($path . DIRECTORY_SEPARATOR . 'index.htm')) {
		$type = 'Static';
		$tags[] = 'HTML';
	}

	if (!empty($composer['description']) && is_string($composer['description'])) {
		$description = trim($composer['description']);
	} elseif (!empty($package['description']) && is_string($package['description'])) {
		$description = trim($package['description']);
	}

	if ($description === '') {
		$description = match ($type) {
			'Contao' => webText('project.description.contao'),
			'WordPress' => webText('project.description.wordpress'),
			'Laravel' => webText('project.description.laravel'),
			'Symfony' => webText('project.description.symfony'),
			'PHP / Composer' => webText('project.description.composer'),
			'Node / Frontend' => webText('project.description.node'),
			'PHP' => webText('project.description.php'),
			'Static' => webText('project.description.static'),
			default => webText('project.description.default'),
		};
	}

	if (!empty($composer)) {
		$tags[] = 'PHP';
	}
	if (!empty($package)) {
		$tags[] = 'npm';
	}

	$tags = array_values(array_unique($tags));

	return [
			'name' => $folder,
			'type' => $type,
			'description' => $description,
			'tags' => $tags,
			'url' => './' . rawurlencode($folder) . '/',
			'folder' => 'www/' . $folder,
	];
}

function findPhpMyAdmin(string $root): array
{
	$baseDir = dirname($root);

	$configFile = $baseDir
			. DIRECTORY_SEPARATOR . 'app'
			. DIRECTORY_SEPARATOR . 'packages'
			. DIRECTORY_SEPARATOR . 'phpmyadmin.json';

	$config = [];

	if (is_file($configFile)) {
		$json = @file_get_contents($configFile);

		if ($json !== false) {
			$config = json_decode($json, true) ?: [];
		}
	}

	$installDirectory = $config['installDirectory'] ?? 'phpmyadmin';

	$phpMyAdminBase = $baseDir
			. DIRECTORY_SEPARATOR . 'bin'
			. DIRECTORY_SEPARATOR . $installDirectory;

	$installedVersions = [];

	if (is_dir($phpMyAdminBase)) {
		foreach (@scandir($phpMyAdminBase) ?: [] as $entry) {
			if ($entry === '.' || $entry === '..') {
				continue;
			}

			$path = $phpMyAdminBase . DIRECTORY_SEPARATOR . $entry;

			if (is_dir($path)) {
				$installedVersions[] = $entry;
			}
		}
	}

	if (!$installedVersions) {
		return [
				'available' => false,
				'directory' => null,
				'version' => null,
				'url' => null,
		];
	}

	$version = null;
	$launchPath = null;
	$activeFile = $baseDir
			. DIRECTORY_SEPARATOR . 'app'
			. DIRECTORY_SEPARATOR . 'config'
			. DIRECTORY_SEPARATOR . 'packages.active.json';
	$activePackages = readJsonFile($activeFile);
	$activeVersion = $activePackages['phpmyadmin'] ?? null;

	if (is_string($activeVersion) && in_array($activeVersion, $installedVersions, true)) {
		$version = $activeVersion;
	}

	foreach ($config['releases'] ?? [] as $release) {
		$releaseVersion = $release['version'] ?? null;
		if ($releaseVersion === $version) {
			$launchPath = $release['launchPath'] ?? null;
			break;
		}
		if ($version === null && $releaseVersion !== null && in_array($releaseVersion, $installedVersions, true)) {
			$version = $releaseVersion;
			$launchPath = $release['launchPath'] ?? null;
		}
	}

	/*
	 * Letzter Fallback: irgendeine installierte Version.
	 */
	if ($version === null) {
		$version = reset($installedVersions);
	}

	/*
	 * Falls für diese Version kein launchPath definiert ist,
	 * Standardpfad verwenden.
	 */
	if (!$launchPath) {
		$launchPath = '/' . trim($installDirectory, '/') . '/';
	}

	return [
			'available' => true,
			'directory' => 'bin'
					. DIRECTORY_SEPARATOR
					. $installDirectory
					. DIRECTORY_SEPARATOR
					. $version,
			'version' => $version,
			'url' => $launchPath,
	];
}

function loadVirtualHostLinks(string $root): array
{
	$baseDir = dirname($root);
	$settings = readJsonFile($baseDir . DIRECTORY_SEPARATOR . 'app' . DIRECTORY_SEPARATOR . 'config' . DIRECTORY_SEPARATOR . 'settings.json');
	$defaultPort = (int) ($settings['httpPort'] ?? 80);
	$directory = $baseDir . DIRECTORY_SEPARATOR . 'app' . DIRECTORY_SEPARATOR . 'config' . DIRECTORY_SEPARATOR . 'vhosts';
	$links = [];
	foreach (glob($directory . DIRECTORY_SEPARATOR . '*.json') ?: [] as $file) {
		$vhost = readJsonFile($file);
		if ((array_key_exists('enabled', $vhost) && !$vhost['enabled']) || empty($vhost['host']) || empty($vhost['documentRoot'])) continue;
		$documentRoot = (string) $vhost['documentRoot'];
		if (!preg_match('~^(?:[A-Za-z]:[\\\\/]|[\\\\/]{2})~', $documentRoot)) $documentRoot = $baseDir . DIRECTORY_SEPARATOR . $documentRoot;
		$resolved = realpath($documentRoot) ?: $documentRoot;
		$port = isset($vhost['port']) ? (int) $vhost['port'] : $defaultPort;
		$links[] = [
			'root' => strtolower(str_replace('\\', '/', rtrim($resolved, '\\/'))),
			'url' => 'http://' . $vhost['host'] . ($port === 80 ? '' : ':' . $port) . '/',
		];
	}
	return $links;
}

function scanProjects(string $root, ?string $phpMyAdminDirectory, array $virtualHosts): array
{
	$projects = [];
	$ignore = array_filter([
			$phpMyAdminDirectory ? strtolower($phpMyAdminDirectory) : null,
			'.git', '.github', '.idea', '.vscode',
	]);

	foreach (@scandir($root) ?: [] as $entry) {
		if ($entry === '.' || $entry === '..' || str_starts_with($entry, '.')) {
			continue;
		}

		$path = $root . DIRECTORY_SEPARATOR . $entry;
		if (!is_dir($path)) {
			continue;
		}

		if (in_array(strtolower($entry), $ignore, true)) {
			continue;
		}

		$project = detectProject($path, $entry);
		$projectPath = strtolower(str_replace('\\', '/', rtrim(realpath($path) ?: $path, '\\/')));
		foreach ($virtualHosts as $virtualHost) {
			if ($virtualHost['root'] === $projectPath || str_starts_with($virtualHost['root'], $projectPath . '/')) {
				$project['url'] = $virtualHost['url'];
				$project['vhost'] = true;
				break;
			}
		}
		$projects[] = $project;
	}

	usort($projects, static fn(array $a, array $b): int => strnatcasecmp($a['name'], $b['name']));
	return $projects;
}

$root = __DIR__;
[$webLanguageCode, $webLanguage] = loadWebLanguage(dirname($root));
$webServer = detectWebServer();
$database = detectDatabaseVersion();
$phpMyAdmin = findPhpMyAdmin($root);
$projects = scanProjects($root, $phpMyAdmin['directory'], loadVirtualHostLinks($root));
$phpVersion = PHP_VERSION;
$host = $_SERVER['HTTP_HOST'] ?? 'localhost';

$showPhpInfo = isset($_GET['phpinfo']) && $_GET['phpinfo'] === '1';

$favIconSrc = 'data:image/webp;base64,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';
$iconSrc = 'data:image/webp;base64,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';
?>
<!DOCTYPE html>
<html lang="<?= h($webLanguageCode) ?>">
<head>
	<meta charset="UTF-8">
	<meta name="viewport" content="width=device-width, initial-scale=1.0">
	<meta name="theme-color" content="#07111f">
	<title><?= h(webText('page.title')) ?></title>
	<link rel="icon" type="image/png" href="<?= $favIconSrc ?>">
	<style>
		*, *::before, *::after { box-sizing: border-box; }
		html { color-scheme: dark; }
		body, h1, h2, h3, p { margin: 0; }
		a { color: inherit; }
		button, input { font: inherit; }

		:root {
			--bg: #07111f;
			--bg-deep: #050b14;
			--panel: rgba(13, 27, 46, .78);
			--panel-solid: #0d1b2e;
			--panel-hover: rgba(19, 39, 65, .92);
			--border: rgba(148, 181, 224, .14);
			--border-strong: rgba(73, 157, 255, .32);
			--text: #eef6ff;
			--muted: #8fa5bf;
			--muted-2: #617994;
			--blue: #1689ff;
			--blue-bright: #2db5ff;
			--cyan: #18e2df;
			--green: #58d68d;
			--amber: #f5b942;
			--shadow: 0 22px 70px rgba(0, 0, 0, .30);
		}

		body {
			min-height: 100vh;
			background:
					radial-gradient(circle at 12% 0%, rgba(22,137,255,.16), transparent 34rem),
					radial-gradient(circle at 100% 35%, rgba(24,226,223,.07), transparent 30rem),
					linear-gradient(180deg, var(--bg) 0%, var(--bg-deep) 100%);
			color: var(--text);
			font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
			line-height: 1.5;
		}

		.page {
			width: min(1180px, calc(100% - 36px));
			margin: 0 auto;
			padding: 34px 0 44px;
		}

		.topbar {
			display: flex;
			align-items: center;
			justify-content: space-between;
			gap: 24px;
			margin-bottom: 36px;
		}

		.brand-logo {
			width: 170px;
			display: block;
			filter: drop-shadow(0 10px 25px rgba(22,137,255,.25));
		}

		.server-pill {
			display: inline-flex;
			align-items: center;
			gap: 9px;
			padding: 9px 13px;
			border: 1px solid var(--border);
			border-radius: 999px;
			background: rgba(11, 24, 42, .72);
			color: #b8c9dc;
			font-size: .82rem;
			box-shadow: inset 0 1px rgba(255,255,255,.02);
		}

		.status-dot {
			width: 8px;
			height: 8px;
			border-radius: 50%;
			background: var(--green);
			box-shadow: 0 0 0 4px rgba(88,214,141,.10), 0 0 14px rgba(88,214,141,.45);
			flex: none;
		}

		.hero {
			display: grid;
			grid-template-columns: minmax(0, 1.25fr) minmax(320px, .75fr);
			gap: 26px;
			align-items: stretch;
			margin-bottom: 30px;
		}

		.hero-main,
		.quick-card,
		.stat-card,
		.project-card,
		.empty-state {
			border: 1px solid var(--border);
			background: linear-gradient(145deg, rgba(17, 34, 57, .86), rgba(9, 20, 35, .78));
			box-shadow: var(--shadow), inset 0 1px rgba(255,255,255,.025);
			backdrop-filter: blur(15px);
		}

		.hero-main {
			overflow: hidden;
		}
		.hero-main,
		.quick-card {
			border-radius: 14px;
			padding: 20px;
			display: flex;
			flex-direction: column;
			gap: 14px;
		}
		.hero-main::after {
			content: "";
			position: absolute;
			width: 260px;
			height: 260px;
			right: -80px;
			top: -100px;
			border-radius: 50%;
			background: radial-gradient(circle, rgba(22,137,255,.20), transparent 68%);
			pointer-events: none;
		}

		.eyebrow {
			display: inline-flex;
			align-items: center;
			gap: 8px;
			margin-bottom: 8px;
			color: #8ec9ff;
			font-size: .78rem;
			font-weight: 750;
			letter-spacing: .13em;
			text-transform: uppercase;
		}

		.eyebrow::before {
			content: "";
			width: 24px;
			height: 1px;
			background: linear-gradient(90deg, var(--cyan), var(--blue));
		}

		.hero-meta {
			display: flex;
			flex-wrap: wrap;
			gap: 10px;
		}

		.meta-chip {
			padding: 7px 10px;
			border-radius: 8px;
			border: 1px solid var(--border);
			background: rgba(255,255,255,.025);
			color: #a9bbcf;
			font-size: .78rem;
		}

		.quick-actions { display: grid; gap: 11px; }

		.action {
			display: flex;
			align-items: center;
			justify-content: space-between;
			gap: 14px;
			min-height: 64px;
			padding: 10px 16px;
			border-radius: 8px;
			border: 1px solid var(--border);
			background: rgba(255,255,255,.025);
			color: var(--text);
			text-decoration: none;
			transition: .18s ease;
		}

		.action:hover {
			transform: translateY(-1px);
			border-color: var(--border-strong);
			background: rgba(22,137,255,.075);
		}

		.action.disabled { opacity: .48; pointer-events: none; }
		.action-main { display: flex; align-items: center; gap: 12px; min-width: 0; }
		.action-icon {
			width: 38px; height: 38px; border-radius: 50%;
			display: grid; place-items: center;
			background: linear-gradient(145deg, rgba(22,137,255,.18), rgba(24,226,223,.08));
			color: #80c8ff;
			border: 1px solid rgba(70,156,255,.14);
			flex: none;
		}
		.action-title { font-weight: 750; font-size: .94rem; }
		.action-sub { margin-top: 2px; color: var(--muted-2); font-size: .74rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
		.arrow { color: #7cbcff; font-size: 1.2rem; }

		.stats {
			display: grid;
			grid-template-columns: repeat(3, 1fr);
			gap: 14px;
		}

		.stat-card {
			border-radius: 14px;
			padding: 18px 20px;
		}

		.stat-top { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
		.stat-name { color: var(--muted); font-size: .78rem; font-weight: 750; letter-spacing: .08em; text-transform: uppercase; }
		.mini-dot { width: 7px; height: 7px; border-radius: 50%; background: var(--green); box-shadow: 0 0 10px rgba(88,214,141,.45); }
		.mini-dot.warn { background: var(--amber); box-shadow: 0 0 10px rgba(245,185,66,.35); }
		.stat-value { margin-top: 10px; font-size: 1.45rem; font-weight: 820; letter-spacing: -.025em; }
		.stat-note { margin-top: 3px; color: var(--muted-2); font-size: .74rem; }

		.projects-section { margin-top: 4px; }
		.section-head {
			display: flex;
			align-items: end;
			justify-content: space-between;
			gap: 18px;
			margin-bottom: 16px;
		}

		.section-title-wrap { display: flex; align-items: baseline; gap: 11px; }
		h2 { font-size: 1.35rem; letter-spacing: -.025em; }
		.count { color: var(--muted-2); font-size: .8rem; }

		.search {
			width: min(320px, 100%);
			position: relative;
		}

		.search input {
			width: 100%;
			height: 42px;
			border-radius: 8px;
			border: 1px solid var(--border);
			background: rgba(10, 23, 39, .72);
			color: var(--text);
			outline: none;
			padding: 0 14px 0 39px;
			transition: .18s ease;
		}

		.search input:focus { border-color: rgba(49,155,255,.55); box-shadow: 0 0 0 3px rgba(22,137,255,.08); }
		.search::before {
			content: "";
			position: absolute;
			width: 12px; height: 12px;
			left: 15px; top: 13px;
			border: 2px solid #68809a;
			border-radius: 50%;
		}
		.search::after {
			content: "";
			position: absolute;
			width: 6px; height: 2px;
			left: 25px; top: 26px;
			background: #68809a;
			transform: rotate(45deg);
			border-radius: 2px;
		}

		.projects-grid {
			display: grid;
			grid-template-columns: repeat(4, minmax(0, 1fr));
			gap: 14px;
		}

		.project-card {
			border-radius: 14px;
			padding: 14px 18px;
			text-decoration: none;
			display: flex;
			flex-direction: column;
			position: relative;
			overflow: hidden;
			transition: transform .18s ease, border-color .18s ease, background .18s ease;
		}
		.project-card[hidden] {
			display: none !important;
		}
		.project-card::after {
			content: "";
			position: absolute;
			width: 120px;
			height: 120px;
			right: -48px;
			bottom: -58px;
			border-radius: 50%;
			background: radial-gradient(circle, rgba(22,137,255,.12), transparent 70%);
			transition: .18s ease;
		}
		.project-card:hover {
			transform: translateY(-3px);
			border-color: var(--border-strong);
			background: var(--panel-hover);
		}
		.project-card:hover::after { transform: scale(1.18); }
		.project-open { position: absolute; top: 11px; right: 14px; color: #6887a7; font-size: 1.12rem; transition: .18s ease; }
		.project-card:hover .project-open { color: #72c7ff; transform: translate(2px, -2px); }
		.project-name { font-weight: 800; letter-spacing: -.02em; font-size: 1.04rem; margin-right: 14px; overflow-wrap: anywhere; }
		.project-description { margin-top: 4px; color: var(--muted); font-size: .79rem; }
		.project-bottom { margin-top: auto; padding-top: 15px; display: flex; align-items: center; justify-content: space-between; gap: 8px; }
		.tags { display: flex; gap: 6px; flex-wrap: wrap; }
		.tag {
			padding: 3px 7px;
			border-radius: 8px;
			background: rgba(255,255,255,.035);
			border: 1px solid rgba(255,255,255,.045);
			color: #87a0ba;
			font-size: .67rem;
			font-weight: 700;
		}
		.folder { color: #536b84; font-size: .68rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }

		.empty-state {
			grid-column: 1 / -1;
			border-radius: 14px;
			padding: 34px;
			text-align: center;
			color: var(--muted);
		}
		.empty-state strong { display: block; margin-bottom: 5px; color: var(--text); }

		footer {
			display: flex;
			justify-content: space-between;
			gap: 14px;
			margin-top: 30px;
			font-size: .8rem;
			color: var(--muted);
			line-height: 1.8;
			border-top: 1px solid var(--border);
			padding-top: 20px;
		}
		footer .footer-head {
			font-size: .9rem;
			color: #fff;
		}
		footer .support {
			text-align: right;
		}
		footer .support a {
			text-decoration: none;
		}
		footer .support a:hover {
			text-decoration: underline;
		}

		.modal-backdrop {
			position: fixed;
			inset: 0;
			z-index: 100;
			overflow: auto;
			padding: 24px;
			background: rgba(2, 7, 13, .86);
			backdrop-filter: blur(8px);
		}
		.modal {
			width: min(1100px, 100%);
			height: calc(100vh - 48px);
			margin: 0 auto;
			border-radius: 14px;
			background: #0c1726;
			color: #fff;
			box-shadow: 0 30px 100px rgba(0,0,0,.55);
			overflow: hidden;
			display: flex;
			flex-direction: column;
		}
		.modal-bar {
			position: sticky;
			top: 0;
			z-index: 2;
			display: flex;
			align-items: center;
			justify-content: space-between;
			gap: 16px;
			padding: 12px 16px;
			background: #0c1726;
			color: #fff;
		}
		.modal-bar strong { font-size: .9rem; }
		.modal-close {
			display: inline-grid;
			place-items: center;
			width: 34px; height: 34px;
			border-radius: 8px;
			background: rgba(255,255,255,.08);
			color: #fff;
			text-decoration: none;
			font-size: 1.2rem;
		}
		.phpinfo-frame {
			display: block;
			width: 100%;
			min-height: 0;
			flex: 1;
			border: 0;
			background: #fff;
		}

		@media (max-width: 1024px) {
			.projects-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); }
		}

		@media (max-width: 900px) {
			.hero { grid-template-columns: 1fr; }
			.stats { grid-template-columns: 1fr; }
			.projects-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
		}

		@media (max-width: 620px) {
			.page { width: min(100% - 24px, 1180px); padding-top: 20px; }
			.topbar { margin-bottom: 26px; }
			.server-pill { display: none; }
			.brand-logo { width: 160px; }
			.section-head { align-items: stretch; flex-direction: column; }
			.search { width: 100%; }
			.projects-grid { grid-template-columns: 1fr; }
			footer { flex-direction: column; }
			footer .support { text-align: left; }
		}
	</style>
</head>
<body>
<div class="page">
	<header class="topbar">
		<img class="brand-logo" src="<?= $iconSrc ?>" alt="Drageon">
		<div class="server-pill"><span class="status-dot"></span><?= h(webText('server.ready', ['{host}' => $host])) ?></div>
	</header>

	<main>
		<section class="hero">
			<div class="hero-main">
				<div class="eyebrow"><?= h(webText('server.environment')) ?></div>
				<section class="stats" aria-label="<?= h(webText('server.versions')) ?>">
					<div class="stat-card" title="<?= h($webServer['raw']) ?>">
						<div class="stat-top"><span class="stat-name"><?= h($webServer['name']) ?></span><span class="mini-dot<?= $webServer['online'] ? '' : ' warn' ?>"></span></div>
						<div class="stat-value"><?= h($webServer['version']) ?></div>
						<div class="stat-note"><?= h($webServer['online'] ? webText('server.active') : webText('detection.versionUnknown')) ?></div>
					</div>
					<div class="stat-card" title="<?= h($database['raw']) ?>">
						<div class="stat-top"><span class="stat-name"><?= h($database['name']) ?></span><span class="mini-dot<?= $database['online'] ? '' : ' warn' ?>"></span></div>
						<div class="stat-value"><?= h($database['version']) ?></div>
						<div class="stat-note"><?= h($database['online'] ? webText('database.reachable') : ($database['version'] !== webText('detection.notRecognized') ? webText('detection.installedOffline') : webText('detection.notAutomatic'))) ?></div>
					</div>
					<div class="stat-card">
						<div class="stat-top"><span class="stat-name">PHP</span><span class="mini-dot"></span></div>
						<div class="stat-value"><?= h($phpVersion) ?></div>
						<div class="stat-note">PHP <?= PHP_INT_SIZE * 8 ?> Bit · <?= h(PHP_SAPI) ?></div>
					</div>
				</section>
				<div class="hero-meta">
                    <span class="meta-chip">HTTP · <?= h($webServer['httpPort']) ?></span>
                    <?php if ($webServer['fastCgi'] !== null): ?>
                    <span class="meta-chip">PHP FastCGI · <?= h($webServer['fastCgi']) ?></span>
                    <?php endif ?>
					<span class="meta-chip"><?= h(PHP_OS_FAMILY) ?> · <?= PHP_INT_SIZE * 8 ?> Bit</span>
					<span class="meta-chip"><?= h(webText('server.documentRoot')) ?> · <?= h(basename($root)) ?></span>
				</div>
			</div>

			<aside class="quick-card">
				<div class="eyebrow"><?= h(webText('quick.title')) ?></div>
				<div class="quick-actions">
					<a class="action<?= $phpMyAdmin['available'] ? '' : ' disabled' ?>" href="<?= h($phpMyAdmin['url']) ?>" target="_blank">
                        <span class="action-main">
                            <span class="action-icon" aria-hidden="true">
                                <svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><ellipse cx="12" cy="5" rx="7.5" ry="3"/><path d="M4.5 5v7c0 1.7 3.4 3 7.5 3s7.5-1.3 7.5-3V5"/><path d="M4.5 12v7c0 1.7 3.4 3 7.5 3s7.5-1.3 7.5-3v-7"/></svg>
                            </span>
                            <span>
                                <span class="action-title">phpMyAdmin</span><br>
                                <span class="action-sub"><?= $phpMyAdmin['available'] ? h($phpMyAdmin['version']) : h(webText('quick.phpmyadminMissing')) ?></span>
                            </span>
                        </span>
						<span class="arrow">↗</span>
					</a>

					<a class="action" href="?phpinfo=1">
                        <span class="action-main">
                            <span class="action-icon" aria-hidden="true">
                                <svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M8 9l-3 3 3 3M16 9l3 3-3 3M14 6l-4 12"/></svg>
                            </span>
                            <span>
                                <span class="action-title">PHP Info</span><br>
                                <span class="action-sub"><?= h(webText('quick.phpinfoSubtitle')) ?></span>
                            </span>
                        </span>
						<span class="arrow">↗</span>
					</a>
				</div>
			</aside>
		</section>

		<section class="projects-section">
			<div class="section-head">
				<div class="section-title-wrap">
					<h2><?= h(webText('projects.title')) ?></h2>
					<span class="count"><?= h(webText('projects.count', ['{count}' => (string) count($projects)])) ?></span>
				</div>
				<?php if (count($projects) > 4): ?>
					<label class="search">
						<input id="projectSearch" type="search" placeholder="<?= h(webText('projects.filterPlaceholder')) ?>" autocomplete="off" aria-label="<?= h(webText('projects.filterLabel')) ?>">
					</label>
				<?php endif ?>
			</div>

			<div class="projects-grid" id="projectsGrid">
				<?php if (!$projects): ?>
					<div class="empty-state">
						<strong><?= h(webText('projects.emptyTitle')) ?></strong>
						<?= h(webText('projects.emptyText')) ?>
					</div>
				<?php else: ?>
					<?php foreach ($projects as $project): ?>
						<a class="project-card" href="<?= h($project['url']) ?>" data-project="<?= h(strtolower($project['name'] . ' ' . $project['type'] . ' ' . implode(' ', $project['tags']) . ($project['vhost'] ?? false ? ' vhost' : ''))) ?>" target="_blank">
							<span class="project-open">↗</span>
							<div class="project-name"><?= h($project['name']) ?></div>
							<div class="project-description"><?= h($project['description']) ?></div>
							<span class="folder"><?= h($project['folder']) ?></span>
							<div class="project-bottom">
                                <span class="tags">
                                    <span class="tag"><?= h($project['type']) ?></span>
                                    <?php foreach (array_slice($project['tags'], 0, 2) as $tag): ?>
	                                    <?php if ($tag !== $project['type']): ?><span class="tag"><?= h($tag) ?></span><?php endif ?>
                                    <?php endforeach ?>
                                    <?php if ($project['vhost'] ?? false): ?>
	                                    <span class="tag">vHost</span>
									<?php endif ?>
                                </span>
							</div>
						</a>
					<?php endforeach ?>
				<?php endif ?>
			</div>
		</section>
	</main>

	<footer>
		<span>
			<strong class="footer-head">Drageon</strong><br>
			<?= h(webText('brand.subtitle')) ?><br>
			© 2026 Tim Sydekum · LIVID Media<br>
			Open Source <a href="https://www.mozilla.org/en-US/MPL/2.0/" target="_blank" rel="noopener">Mozilla Public License 2.0 (MPL-2.0)</a><br>
		</span>
		<span class="support">
			<strong class="footer-head"><?= h(webText('footer.supportProject')) ?></strong><br>
			<a href="https://ko-fi.com/livid_media" target="_blank" rel="noopener">Ko-fi ☕︎</a><br>
			<a href="https://www.paypal.com/donate/?hosted_button_id=93Z5AXMPT25EE" target="_blank" rel="noopener">PayPal ♥</a><br>
			<a href="https://github.com/LIVID-Media/Drageon" target="_blank" rel="noopener">GitHub <svg xmlns="http://www.w3.org/2000/svg" width="12" height="12" viewBox="0 0 20 20"><path fill="currentColor" d="M10 0c5.52 0 10 4.59 10 10.25 0 4.53-2.86 8.37-6.83 9.73-.51.1-.69-.22-.69-.5l.01-2.8c0-.96-.32-1.58-.68-1.9 2.23-.26 4.57-1.12 4.57-5.06 0-1.12-.39-2.04-1.03-2.75.1-.26.45-1.3-.1-2.72 0 0-.84-.27-2.75 1.05a9.4 9.4 0 0 0-5 0C5.59 3.98 4.75 4.25 4.75 4.25a3.8 3.8 0 0 0-.1 2.72 4 4 0 0 0-1.03 2.75c0 3.93 2.33 4.8 4.55 5.06-.28.26-.54.71-.63 1.38-.57.26-2.02.71-2.91-.86 0 0-.53-.98-1.53-1.05 0 0-.98-.02-.07.62 0 0 .65.32 1.1 1.5 0 0 .6 1.83 3.38 1.21l.01 1.9c0 .28-.18.6-.68.5A10.2 10.2 0 0 1 0 10.25C0 4.6 4.48 0 10 0"/></svg></a>
		</span>
	</footer>
</div>

<?php if ($showPhpInfo): ?>
	<div class="modal-backdrop">
		<div class="modal">
			<div class="modal-bar">
				<strong>PHP <?= h($phpVersion) ?> · phpinfo()</strong>
				<a class="modal-close" href="./" aria-label="<?= h(webText('quick.closePhpInfo')) ?>">×</a>
			</div>
			<iframe class="phpinfo-frame" src="?phpinfo=frame" title="<?= h(webText('quick.phpinfoFrameTitle')) ?>"></iframe>
		</div>
	</div>
<?php endif ?>

<script>
	const search = document.getElementById('projectSearch');
	if (search) {
		const cards = [...document.querySelectorAll('.project-card')];
		search.addEventListener('input', () => {
			const query = search.value.trim().toLowerCase();
			cards.forEach(card => {
				card.hidden = query !== '' && !card.dataset.project.includes(query);
			});
		});
	}
</script>
</body>
</html>
