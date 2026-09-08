<div align="center">
<img src="docs/images/drageon-cover.png" alt="Drageon Logo" />

# Drageon
<p><strong>Eine portable lokale Entwicklungsumgebung für Windows</strong></p>

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows)
![License](https://img.shields.io/badge/license-MPL--2.0-green)
![Version](https://img.shields.io/github/v/release/LIVID-Media/Drageon?color=f59e0b&label=version)
</div>

[English](README.md) · **Deutsch**

Drageon bündelt den kompletten lokalen Webentwicklungs-Stack in einer schlanken Windows-App. **Apache oder Nginx, PHP, MariaDB oder MySQL, phpMyAdmin, Composer, Node.js, cURL, Projekte, Virtual Hosts, Cronjobs, Testmails und Logs** lassen sich über eine gemeinsame Oberfläche verwalten.

Programme, Projekte, Datenbanken und Einstellungen bleiben vollständig im Drageon-Ordner. Drageon benötigt keine Windows-Dienste, schreibt keine festen Installationspfade in die Registry und verändert den systemweiten `PATH` nicht.

Damit lässt sich die komplette Entwicklungsumgebung einfach kopieren, verschieben oder auf einem externen Laufwerk mitnehmen.

![Drageon Dashboard](docs/images/dashboard-de.png)

## Warum Drageon?

- **Wirklich portabel** – alle Pfade werden relativ zur `Drageon.exe` aufgelöst.
- **Eine App für den gesamten lokalen Stack** – Webserver, PHP, Datenbank, Projekte und Tools an einem Ort.
- **Mehrere Versionen parallel** – Komponenten lassen sich in verschiedenen Versionen installieren und bei Bedarf wechseln.
- **Apache oder Nginx** – der Webserver kann passend zum Projekt gewählt werden.
- **MariaDB oder MySQL** – beide Datenbank-Provider werden mit getrennten Datenverzeichnissen unterstützt.
- **Portable Entwicklungs-Shell** – PowerShell erhält automatisch PHP und ausgewählte CLI-Werkzeuge.
- **Projektfreundlich** – Ordner unter `www/` werden automatisch erkannt; Virtual Hosts sind optional.
- **Praktische Entwicklerwerkzeuge** – Cron-Scheduler, SMTP-Catcher, zentrale Logs und Tray-Steuerung sind direkt integriert.
- **Erweiterbar** – eigene Paketmanifeste können zusätzliche Werkzeuge und Releases bereitstellen.
- **Mehrsprachig** – Deutsch, Englisch, Französisch, Spanisch und Italienisch sind bereits enthalten.

## Schnellstart

1. Drageon-Release herunterladen und entpacken.
2. `Drageon.exe` starten.
3. Unter **Komponenten** die gewünschten Versionen von Webserver, PHP und Datenbank installieren.
4. Projekte unter `www/` ablegen.
5. Server starten und `http://localhost/` öffnen.

Komponenten werden erst bei ihrer Installation heruntergeladen. So enthält jede Drageon-Umgebung nur die Laufzeiten und Werkzeuge, die tatsächlich benötigt werden.

> Veröffentlichte Builds sind selbstenthaltend. Auf dem Zielrechner ist keine separate .NET-Installation erforderlich.

![Drageon localhost Startseite](docs/images/localhost-de.png)

## Komponenten

| Bereich | Unterstützung |
| --- | --- |
| Webserver | Apache HTTP Server, Nginx |
| PHP | mehrere Versionen, INI-Einstellungen, Erweiterungsverwaltung, OPcache |
| Datenbanken | MariaDB, MySQL |
| Datenbankoberfläche | phpMyAdmin |
| PHP-Werkzeuge | Composer |
| Frontend-Werkzeuge | Node.js, npm, npx |
| CLI / Diagnose | cURL |
| Lokale Dienste | Cron-Scheduler, SMTP-Catcher |

Installierte Versionen liegen unter `bin/` und können direkt in der App aktiviert, gewechselt oder entfernt werden. MariaDB und MySQL speichern ihre Daten getrennt unter `data/`, sodass beim Löschen einer Serverversion nicht automatisch die Datenbanken entfernt werden.

![Drageon Komponentenverwaltung](docs/images/components-de.png)

## Projekte und Virtual Hosts

Ordner direkt unter `www/` werden automatisch erkannt. Drageon erkennt typische Projektstrukturen unter anderem für Contao/Symfony, Laravel, WordPress, Composer-/PHP-Projekte, Node-/Frontend-Projekte und statische HTML-Seiten.

Ohne zusätzliche Konfiguration ist beispielsweise

```text
www/mein-projekt/
```

direkt erreichbar unter

```text
http://localhost/mein-projekt/
```

Optional können Virtual Hosts direkt in Drageon angelegt werden:

```json
{
  "host": "website.localhost",
  "documentRoot": "www/website/public",
  "enabled": true,
  "port": 80
}
```

Dieselbe Virtual-Host-Definition kann von Apache und Nginx verwendet werden; serverspezifische Zusatzregeln bleiben getrennt konfigurierbar.

![Drageon Projektverwaltung](docs/images/projects-de.png)

## Portable PowerShell

Eine aus Drageon gestartete PowerShell erhält temporär einen Entwicklungs-`PATH` mit der aktiven PHP-Version und allen dafür freigegebenen CLI-Komponenten.

Dadurch funktionieren Befehle wie

```powershell
php -v
composer install
npm run dev
curl --version
```

ohne globale Installation.

Wird PowerShell aus einem Projekt heraus geöffnet, startet sie direkt im jeweiligen Projektordner.

Drageon erlaubt die Skriptausführung ausschließlich für den gestarteten PowerShell-Prozess. Dadurch funktionieren auch Node.js-Starter wie `npm.ps1` bei einer restriktiven Windows-Ausführungsrichtlinie, ohne die systemweite Richtlinie zu ändern.

## PHP-Konfiguration

PHP-Einstellungen werden für jede installierte Version getrennt verwaltet. Einstellbar sind unter anderem:

- `memory_limit`
- maximale Upload- und POST-Größe
- maximale Ausführungszeit
- Fehleranzeige
- OPcache mit getrenntem Cache pro PHP-Version
- automatisch erkannte PHP-Erweiterungen
- zusätzliche eigene INI-Direktiven

Die erzeugte PHP-Konfiguration wird einheitlich von Apache, Nginx/PHP-FastCGI, PHP-Cronjobs und der portablen PowerShell verwendet.

## Cronjobs und Testmails

Der integrierte Scheduler läuft, solange Drageon geöffnet ist. Cronjobs können unter anderem folgende Ziele ausführen:

- PHP-Skripte
- HTTP-URLs
- cURL-Aufrufe
- lokale Programme, Batchdateien und Befehle

Verpasste Ausführungen können beim nächsten App-Start optional nachgeholt werden.

Der integrierte SMTP-Catcher lauscht standardmäßig auf `127.0.0.1:1025` und benötigt weder TLS noch Anmeldung. Testmails und Anhänge werden portabel unter `data/mail/` gespeichert und können direkt in Drageon betrachtet werden.

![Drageon SMTP-Catcher](docs/images/mail-de.png)

## Paketmanifeste

Die Komponentenverwaltung basiert auf Paketmanifesten. Über den integrierten Manifest-Editor können zusätzliche Werkzeuge und Releases hinzugefügt werden.

Definierbar sind beispielsweise:

- Anzeigename und Kategorie
- Hersteller-Downloadseite
- Installationsverzeichnis
- ZIP-Archiv oder Einzeldatei
- Download-URL
- optionale SHA256-Prüfsumme
- optionaler Archiv-Stammordner
- Browser-Startpfad
- Einbindung in den portablen `PATH`

Dadurch bleibt Drageon flexibel, ohne jedes zusätzliche Werkzeug fest im Programmcode hinterlegen zu müssen.

<details>
<summary><strong>Portable Ordnerstruktur</strong></summary>

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
│  ├─ cron/
│  ├─ mail/
│  ├─ mariadb/
│  └─ mysql/
└─ www/
```

Alle Pfade werden vom Speicherort der `Drageon.exe` abgeleitet. Vor dem Verschieben oder Kopieren sollten alle von Drageon gestarteten Prozesse beendet werden.

</details>

<details>
<summary><strong>Selbst kompilieren</strong></summary>

Voraussetzungen:

- Windows 10 oder 11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- keine zusätzlichen NuGet- oder Node-Pakete

Self-contained Windows-x64-Build erstellen:

```text
.\scripts\publish-drageon-win-x64.cmd
```

oder:

```powershell
.\scripts\publish-drageon-win-x64.ps1
```

Die portable Ausgabe wird unter folgendem Pfad erzeugt:

```text
publish/win-x64/
```

</details>

## Sprachen

Drageon enthält derzeit:

- Deutsch
- Englisch
- Französisch
- Spanisch
- Italienisch
- portugiesisch (Brasilien)

Die Oberflächentexte liegen in JSON-Sprachdateien und können um weitere Übersetzungen ergänzt werden.

## Lizenz

Drageon ist freie Open-Source-Software unter der [Mozilla Public License 2.0](LICENSE).

## Unterstützen

Wenn Drageon für dich nützlich ist und du die Weiterentwicklung unterstützen möchtest:

- [Ko-fi](https://ko-fi.com/livid_media)
- [PayPal](https://www.paypal.com/donate/?hosted_button_id=93Z5AXMPT25EE)

## Haftungsausschluss

Drageon wird ohne Gewährleistung bereitgestellt. Die Nutzung erfolgt auf eigene Verantwortung. Erstellen Sie vor größeren Änderungen stets Sicherungskopien wichtiger Projekte und Datenbanken.