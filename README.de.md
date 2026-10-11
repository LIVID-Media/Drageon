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

Ordner direkt unter `www/` sind die einzige Quelle für tatsächlich vorhandene Projekte und werden automatisch erkannt. Drageon erkennt typische Projektstrukturen unter anderem für Contao/Symfony, Laravel, WordPress, Composer-/PHP-Projekte, Node-/Frontend-Projekte und statische HTML-Seiten. Das Anlegen eines Ordners erzeugt keinen Konfigurationseintrag.

Ohne zusätzliche Konfiguration ist beispielsweise

```text
www/mein-projekt/
```

direkt erreichbar unter

```text
http://localhost/mein-projekt/
```

`app/config/projects.json` enthält ausschließlich optionale Einstellungen für Projekte, die ausdrücklich konfiguriert wurden. Dazu können Anzeigename, Favoritenstatus, zugeordnete Datenbank, eigene URL, verknüpfter Virtual Host oder eine Staging-Beziehung gehören. Metadaten fehlender Projektordner bleiben erhalten, damit Scans einen vorübergehend fehlenden Ordner bei Austausch oder Wiederherstellung nicht als Löschung interpretieren. Explizite Staging-Löschungen entfernen ihre Metadaten.

Einem Projekt kann eine vorhandene Benutzerdatenbank des aktiven MySQL- oder MariaDB-Servers zugeordnet werden. Diese Zuordnung ermöglicht datenbankgestützte Snapshots und Staging-Kopien; sie macht Drageon nicht zu einer allgemeinen Datenbankverwaltung. Ist eine zugeordnete Datenbank nicht mehr verfügbar, bleibt die Zuordnung sichtbar, bis sie bewusst geändert oder entfernt wird.

Optional können Virtual Hosts direkt in Drageon angelegt werden:

```json
{
  "host": "website.localhost",
  "documentRoot": "www/website/public",
  "enabled": true,
  "port": 80
}
```

Dieselbe Virtual-Host-Definition kann von Apache und Nginx verwendet werden; serverspezifische Zusatzregeln bleiben getrennt konfigurierbar. Ein Virtual Host kann optional einem erkannten Projekt zugeordnet werden. Umbenennen, Neuzuordnen oder Löschen des Hosts aktualisiert die Projektmetadaten, ohne das Projekt selbst anzulegen oder zu löschen.

Über die Projektwerkzeuge lassen sich vollständige Snapshots unter `data/snapshots/` erstellen. Dateien, Dotfiles, leere Ordner, Symlinks, Verzeichnislinks und Junctions bleiben erhalten, ohne externe Link-Ziele zu kopieren. Eine zugeordnete Datenbank, Projektmetadaten und die relevante Virtual-Host-Definition werden einbezogen, sofern vorhanden. Vor einer Wiederherstellung kann der aktuelle Zustand automatisch als Sicherheitssnapshot gespeichert werden.

Von Hauptprojekten können lokale Staging-Kopien erzeugt werden. Drageon kopiert den Projektbaum mit angepassten internen Link-Zielen und kann optional die zugeordnete Datenbank sowie einen Virtual Host kopieren. Für mehrere Instanzen werden nummerierte Vorschläge erzeugt. Projektdateien werden nicht verändert, um CMS-spezifische Datenbankzugänge einzutragen; nach einer Datenbankkopie muss die Verbindung im Staging deshalb manuell angepasst werden. Beim Löschen einer Staging-Instanz prüft Drageon einen separaten Erzeugungsnachweis einschließlich Ordneridentität, VHost-Prüfsumme, Projektzugehörigkeit und weiterer Referenzen. Unklare, fremde, umgewidmete oder gemeinsam genutzte Ressourcen führen vor Änderungen zum Abbruch. Die eigene Staging-Datenbank wird ebenfalls gelöscht, sofern die ursprüngliche Zuordnung und die bei exklusiver Erstellung erfassten Windows-Identitäten von Server-Datenverzeichnis, Systemdatenbank und Staging-Datenbank unverändert sind. Vor der Löschung wird ein SQL-Rückfalldump erstellt; gemeinsame Referenzen, andere Server und ersetzte Datenbanken führen vor Änderungen zum Abbruch. Dies wird nur für lokale Server mit Datenverzeichnissen innerhalb dieser Drageon-Instanz und Datenbanknamen aus ASCII-Buchstaben, Ziffern und Unterstrichen unterstützt. Ältere Instanzen ohne diesen Datenbank-Erzeugungsnachweis werden nicht automatisch gelöscht. Nach reversibler Verschiebung des Projektordners werden Dateien bereinigt, der nachweislich eigene VHost und Metadaten entfernt und Snapshots in den Rückfallordner verschoben. Während der Löschung bleiben Dateien, SQL-Dump, VHost, Metadaten und Snapshots für eine mögliche Wiederherstellung erhalten. Nach erfolgreicher Löschung werden diese temporären Rückfallkopien, Snapshots, Erzeugungsnachweise und abgeschlossenen Operationsdateien entfernt. Die Bestätigung weist auf die endgültige Löschung hin; bei Fehlern bleiben nötige Wiederherstellungsdaten erhalten. Erfolgreiche Staging-Erstellung entfernt ebenfalls ihre Operationsdateien.

### Sicherheit und Wiederherstellung

Neue Datenbank-Backups und Snapshot-Ordner verwenden einen Zeitstempel mit Sekunden und Millisekunden, durch Unterstriche getrennt und ohne Zufallskennung. Bestehende Sicherungen behalten ihre Namen.

Neue Snapshots verwenden Format 3: `files.zip` enthält reguläre Dateien und Linkeinträge mit Zieltext (ohne Zielinhalte), `files.index.json` einen kompakten Dateiindex und `snapshot.json` nur den kleinen Kopfdatensatz für die Liste. Größen und SHA-256-Prüfsummen schützen Dateien, ZIP, Index und SQL-Dump. Inhalte werden mit einem 128-KiB-Puffer verarbeitet; ZIP-Verzeichnis und Dateiindex benötigen Speicher proportional zur Anzahl der Einträge, nicht zur Größe der Dateiinhalte. Die Kompression nutzt die schnelle Stufe. Symlinks, Verzeichnislinks und Junctions werden mit Typ und Ziel im Index gespeichert und ausdrücklich neu angelegt; gewöhnliches ZIP-Entpacken stellt Windows-Junctions und Symlinks nicht zuverlässig wieder her. Frühere Format-3-Archive mit Links ausschließlich im Index bleiben lesbar. Staging-Erstellung nutzt denselben Mechanismus mit einem unkomprimierten temporären ZIP; neue Rückfalldaten bei Staging-Löschung werden komprimiert gespeichert. Format 2 mit lose gespeicherten Dateien bleibt lesbar. Der tatsächlich gelesene Snapshot-Ordner bestimmt seine Adresse; Manifestadressen werden nicht für Löschungen verwendet. Absolute Eintragspfade, Traversal, ungültige Namen, unbekannte Typen, fehlende Linkziele, doppelte Pfade und Konflikte zwischen Links und Unterpfaden werden abgewiesen. Snapshot-Quellen und ihre Eltern dürfen keine unerwarteten Reparse Points enthalten. Alte Snapshots ohne Formatversion bleiben nach Pfad- und Vollständigkeitsprüfung lesbar; ohne gespeicherte Prüfsummen ist keine nachträgliche Integritätsgarantie möglich. Leere Projekte werden unterstützt. Alte Staging-Metadaten ohne separaten Erzeugungsnachweis erlauben keine automatische Löschung; Drageon übernimmt Eigentum nicht nachträglich.

Datei-Restores bereiten den vollständigen Baum in einem eindeutigen Geschwisterordner vor. Vor dem ersten Umbenennen wird ein Operationsjournal mit `WriteThrough` und `Flush(true)` geschrieben. Erst danach wird der bisherige Ordner nach `<Projekt>.previous-<ID>` verschoben und der vorbereitete Baum eingesetzt. Interne Links zeigen auf den endgültigen Projektpfad. Externe Linkziele werden weder kopiert noch bereinigt. Vorbereitungsfehler lassen den Bestand unverändert; Austauschfehler versuchen einen Rollback unabhängig vom abgebrochenen Benutzertoken. Bei fehlgeschlagenem Rollback bleiben alter Baum und Journal erhalten, und die Fehlermeldung nennt beide Pfade. Diese Sicherung gilt auch ohne optionalen Sicherheitssnapshot. Nach erfolgreichem Restore werden alter Dateiordner und beide Journale nach `data/recovery/restore-<ID>/` verschoben; in `www` bleiben keine Operationsartefakte zurück. Bei einfacher Staging-Erstellung eines zuvor fehlenden Ziels wird das abgeschlossene Datei-Journal entfernt; kombinierte Restores behalten ihren Wiederherstellungszustand.

Der kombinierte Restore stellt Dateien, Snapshot-Datenbank, zum Projekt gehörende VHost-Definition und Metadaten wieder her. Sämtliche Konflikte und erwarteten SQL-Dumps werden vor Bestandsänderungen geprüft. Der neue Dateibaum wird vollständig vorbereitet; eine vorhandene Zieldatenbank wird unabhängig vom Projektordner und der aktuellen Datenbankzuordnung als SQL-Rückfalldump gesichert. Eine zusätzliche Sicherung des aktuellen Projekts ist optional und ersetzt diesen Rückweg nicht. Neue Snapshots erhalten einen getrennten Erzeugungsnachweis, der auch eine frühere Datenbankzuordnung bestätigt; bei älteren Snapshots ohne diesen Nachweis muss die Zieldatenbank noch dem Projekt zugeordnet sein. Fremde, umgewidmete oder gemeinsam verwendete VHosts und gemeinsam verwendete Zieldatenbanken führen vor Änderungen zum Abbruch. Automatischer kombinierter DB-Restore benötigt einen lokalen Server mit unverlinktem Datenverzeichnis innerhalb Drageons und einen Datenbanknamen aus ASCII-Buchstaben, Ziffern und Unterstrichen, damit die tatsächliche Server- und Schema-Verzeichnisidentität für eine sichere Kompensation geprüft werden kann. Andere Kombinationen werden vor Mutationen mit konkretem Grund abgelehnt.

Bei Import-, Austausch- oder späteren Fehlern wird die Rückkehr von Datenbank, Dateien, VHost und Metadaten ohne abgebrochenes Benutzertoken versucht. Es gibt keine Gesamtatomarität. Der dauerhafte Zustand unter `<Projekt>.restore-operation-<ID>.json` und die Sicherungen unter `data/recovery/restore-<ID>/` ermöglichen eine fortsetzbare Rückkehr; während der Operation liegt der alte Dateiordner unter `<Projekt>.previous-<ID>`, nach erfolgreichem Abschluss unter `data/recovery/restore-<ID>/files.before`. Der archivierte Koordinator heißt `restore.operation.json`, das Datei-Journal `files.operation.json`. Auch bei unterbrochener Archivierung kann `RecoverProjectRestoreAsync` den Rückweg anhand eines der Koordinatorjournale ausführen. Bereinigungsfehler nach abgeschlossenem Restore werden separat als erfolgreich abgeschlossen mit Rückständen gemeldet. Fehlgeschlagene Kompensation meldet ausdrücklich erforderliche manuelle Wiederherstellung und Sicherungspfade. Andere SQL-Clients und externe Dateiänderungen müssen während der Operation ruhen. Datenbankkopien verwenden exklusives `CREATE DATABASE` ohne vorheriges `DROP`; unvollständige Kopien bleiben mit Quelldump und Journal zur Prüfung erhalten.

Snapshot-/Staging-Änderungen, Metadatenzuordnungen und VHost-Änderungen werden im Service serialisiert; Datenbank-Serviceinstanzen teilen eine eigene Sperre. Bestätigungsdialoge zeigen einen frisch ermittelten Plan mit Pfad, Zieldatenbank, VHosts, Information und Snapshot-Liste. Der Service prüft den Plan erneut; Änderungen erfordern erneute Bestätigung. Fehler beim Webserver-Neustart werden getrennt von einer bereits abgeschlossenen Operation gemeldet. Die App verhindert bereits mehrere gleichzeitige Instanzen desselben portablen Verzeichnisses. Andere SQL-Clients und externe Dateisystemänderungen sind keine Teilnehmer dieser Sperren: Wiederherstellung sollte in einem ruhenden Projekt erfolgen. Es gibt keine Gesamtatomarität über Dateien, SQL, VHosts und Metadaten.

Nach einem Prozessabbruch zuerst das gemeldete Journal prüfen und Rückfallartefakte bewahren. Die Service-APIs `RecoverFileExchangeAsync(journal, expectedProjectPath)`, `RecoverDeletionAsync(journal, expectedProjectPath)` sowie `RecoverProjectRestoreAsync(journal, expectedProjectPath, connection)` und `RecoverDatabaseAsync(journal, expectedDatabase, connection)` prüfen erwartete Ressourcen und führen den jeweiligen Rückweg aus. Vorhandene fremde Ziele und VHost-Konflikte werden erhalten und führen zum Abbruch. Bei einer teilweise bereinigten Staging-Instanz liegen Dateien unter `<Projekt>.recovery-<ID>/files.zip` (ältere Rückfalldaten: `files/`), Eintragsmanifest, Metadaten und VHost daneben sowie bereits verschobene Snapshots unter `snapshots/`; der Restbestand kann unter `<Projekt>.delete-<ID>` liegen. Eine fehlgeschlagene Wiederherstellung erfordert manuelle Prüfung dieser Pfade. Rückfallartefakte erst nach Überprüfung des Ergebnisses ausdrücklich entfernen. Ein echter Stromausfall, volle Datenträger und parallel arbeitende Fremdprogramme werden nicht durch eine Gesamttransaktion abgesichert.
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
