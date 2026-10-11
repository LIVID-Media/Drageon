<div align="center">
<img src="docs/images/drageon-cover.png" alt="Drageon Logo" />

# Drageon
<p><strong>Eine portable lokale Entwicklungsumgebung für Windows</strong></p>

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows)
![License](https://img.shields.io/badge/license-MPL--2.0-green)
![Version](https://img.shields.io/github/v/release/LIVID-Media/Drageon?color=f59e0b&label=version)
</div>

[English](README.md) · **Deutsch**

Drageon bündelt den kompletten lokalen Webentwicklungs-Stack in einer schlanken Windows-App. **Apache oder Nginx, PHP, MariaDB oder MySQL, phpMyAdmin, Composer, Node.js, cURL, Projekte, Virtual Hosts, Backups, Snapshots, Staging, Cronjobs, Testmails und Logs** lassen sich über eine gemeinsame Oberfläche verwalten.

Programme, Projekte, Datenbanken und Einstellungen bleiben vollständig im Drageon-Ordner. Drageon benötigt keine Windows-Dienste, schreibt keine festen Installationspfade in die Registry und verändert den systemweiten `PATH` nicht.

Damit lässt sich die komplette Entwicklungsumgebung einfach kopieren, verschieben oder auf einem externen Laufwerk mitnehmen.

![Drageon Dashboard](docs/images/dashboard-de.png)

## Warum Drageon?

- **Wirklich portabel** – alle Pfade werden relativ zur `Drageon.exe` aufgelöst.
- **Eine App für den gesamten lokalen Stack** – Webserver, PHP, Datenbank, Projekte und Tools an einem Ort.
- **Mehrere Versionen parallel** – Komponenten lassen sich in verschiedenen Versionen installieren und bei Bedarf wechseln.
- **Apache oder Nginx** – der Webserver kann passend zum Projekt gewählt werden.
- **MariaDB oder MySQL** – beide Datenbank-Provider werden mit getrennten Datenverzeichnissen unterstützt.
- **Backups und Snapshots** – SQL-Backups erstellen, Datenbanken wiederherstellen und vollständige Projektzustände vor riskanten Änderungen sichern.
- **Portable Entwicklungs-Shell** – PowerShell erhält automatisch PHP und ausgewählte CLI-Werkzeuge.
- **Projektfreundlich** – Ordner unter `www/` werden automatisch erkannt; Virtual Hosts sind optional.
- **Praktische Entwicklerwerkzeuge** – Cron-Scheduler, SMTP-Catcher, zentrale Logs und Tray-Steuerung sind direkt integriert.
- **Erweiterbar** – eigene Paketmanifeste können zusätzliche Werkzeuge und Releases bereitstellen.
- **Mehrsprachig** – Deutsch, Englisch, Französisch, Spanisch, Italienisch und brasilianisches Portugiesisch sind bereits enthalten.

Zur Projekt-Webseite: [drageon.livid-media.de](https://drageon.livid-media.de/de/)

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

Drageon erkennt Projekte in `www/` automatisch. Beispielsweise ist `www/mein-projekt/` unter `http://localhost/mein-projekt/` erreichbar. Projekte können einen Anzeigenamen, Favoritenstatus und eine zugeordnete Datenbank erhalten.

Virtual Hosts ermöglichen lokale Domains wie `website.localhost` und können auf den öffentlichen Ordner eines Projekts verweisen. Dieselben Definitionen funktionieren mit Apache und Nginx.

Projekt-Snapshots sichern Dateien und optional die zugeordnete Datenbank sowie den Virtual Host für eine spätere Wiederherstellung. Symlinks und Junctions bleiben erhalten. Vor einer Wiederherstellung kann der aktuelle Zustand als zusätzlicher Snapshot gesichert werden.

Staging erstellt eine separate lokale Kopie zum Testen, optional mit eigener Datenbank und eigenem Virtual Host. Die Datenbankverbindung der Anwendung muss manuell angepasst werden. Beim Löschen einer Staging-Instanz werden ihre Dateien, Snapshots und die für diese Instanz erzeugten Ressourcen entfernt.

![Drageon Projektverwaltung](docs/images/projects-de.png)

## Datenbank-Backups

Die Seite **Datenbank-Backups** zeigt ausschließlich Benutzerdatenbanken der aktuell aktiven MySQL- oder MariaDB-Version. Eine, mehrere oder alle Datenbanken können gesichert werden; jede Datenbank erhält einen eigenen SQL-Dump unter `data/backups/databases/`. Vorhandene Backups lassen sich nach Bestätigung in eine ausdrücklich gewählte Zieldatenbank wiederherstellen oder löschen.

Die Zugangsdaten werden verschlüsselt in den versionsbezogenen Komponenteneinstellungen gespeichert und bleiben zusammen mit dem Drageon-Ordner portabel. Drageon verwendet die Client-Werkzeuge der aktiven Datenbankversion. Tabellen, SQL-Abfragen, Benutzer, Rechte, Kollationen und Strukturänderungen bleiben Aufgabe von phpMyAdmin.

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
│  ├─ backups/databases/
│  ├─ cron/
│  ├─ mail/
│  ├─ mariadb/
│  ├─ mysql/
│  └─ snapshots/
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

Bei laufenden Snapshot- und Staging-Operationen bleibt eine Fortschrittsanzeige im Hauptfenster sichtbar, auch wenn das Projektwerkzeug geschlossen oder die Seite gewechselt wird. Bei Dateiverarbeitung wird zusätzlich die Zahl der verarbeiteten Einträge angezeigt.

Der Webserver wird bei der Snapshot-Wiederherstellung weder angehalten noch neu gestartet. Der Dateiaustausch erfolgt vor dem SQL-Import; ein blockierter Austausch verändert die Datenbank nicht. Nach erfolgreichem Rollback werden eigene Vorbereitungsordner anhand ihrer gespeicherten Windows-Identität bereinigt und Journale nach data/recovery archiviert. Bereinigungsfehler melden Restpfade; fremde Ersatzordner bleiben erhalten.

Bei vorübergehend verweigertem Ordner-Rename nutzt der Service begrenzte native Wiederholungsversuche mit erneuter Identitätsprüfung. Vorhandene Ziele werden nicht überschrieben; anhaltende Sperren brechen sicher ab und nennen den Windows-Fehlercode.
