# Backup, Snapshot und lokales Staging

Die Erweiterung lässt die Projekt-Erkennung unverändert dateisystembasiert: Nur direkte Ordner unter `www/` sind Projekte. `app/config/projects.json` enthält ausschließlich explizit gespeicherte Metadaten und wird beim Einlesen um verwaiste Einträge bereinigt.

## Aufbau

- `ProjectMetadataStore` verwaltet portable Projektmetadaten wie Favorit, Datenbank, URL, VHost und Staging-Beziehung.
- `DatabaseBackupService` ermittelt MySQL oder MariaDB samt Version und CLI-Werkzeugen dynamisch aus `packages.active.json`. SQL-Dumps liegen einzeln unter `data/backups/databases/`.
- `ProjectSnapshotService` sichert Projektbaum, Link-Manifest, optionale Datenbank, Projektmetadaten und VHost direkt unter `data/snapshots/<projekt>/`.
- `StagingService` kopiert einen Projektbaum link-sicher, klont optional die Datenbank mit Quellzeichensatz und -kollation und speichert nur die neuen Metadaten. Projektdateien werden nicht angepasst.
- `BackupsView` stellt ausschließlich Auswahl, Sicherung, Restore und Löschen von SQL-Backups bereit. Projekt-Snapshots und Staging werden in einem eigenen projektspezifischen Dialog verwaltet. `VHostsView` ergänzt die optionale Projektzuordnung.

CLI-Passwörter werden nicht als Prozessargument übergeben. Nur wenn ein Passwort gesetzt ist, wird kurzzeitig eine versteckte Client-Datei unter `app/var/tmp/` erzeugt und im `finally`-Pfad entfernt.

Benutzername und Passwort werden in den Einstellungen der jeweiligen aktiven MySQL- oder MariaDB-Version gepflegt. Das Passwort wird mit AES-GCM verschlüsselt; der zufällige Schlüssel liegt unter `app/config/database-credentials.key` und gehört zum portablen Drageon-Verzeichnis. Beim vollständigen Kopieren oder Synchronisieren des Drageon-Ordners bleiben die Zugangsdaten deshalb nutzbar und sind weder an ein Windows-Konto noch an einen Rechner gebunden. Wer Zugriff auf den vollständigen Ordner einschließlich Schlüsseldatei hat, kann technisch auch die gespeicherten Zugangsdaten entschlüsseln; ohne zusätzliches Master-Passwort lässt sich Portabilität nicht mit einer Benutzer- oder Rechnerbindung kombinieren. Backup-Seite, Datenbankzuordnung, datenbankgestützte Snapshots und Staging verwenden diese Komponenten-Einstellungen automatisch. Ein leeres Passwort bleibt möglich und erzeugt keine unnötige temporäre Client-Datei.

## Link-Verhalten

Dateien, Verzeichnisse, leere Verzeichnisse, Datei-Symlinks, Verzeichnis-Symlinks und Windows-Junctions werden einzeln erfasst. Links werden nicht verfolgt. Beim Restore wird das gespeicherte Ziel erneut als Link angelegt. Relative Ziele bleiben relativ; absolute Ziele innerhalb des Quellprojekts werden auf das neue Restore- oder Staging-Projekt umgebogen. Externe absolute Ziele bleiben unverändert. Schlägt die Link-Erstellung wegen fehlender Windows-Rechte fehl, bricht der Restore mit dem konkreten Linkpfad ab und schreibt einen Fehler ins App-Log.

## Tests

`tests/Drageon.BackupTests` ist ein isolierter Smoke-Test. Er prüft die MySQL- und MariaDB-CLI-Verträge mit lokalen Test-Doubles, verschlüsselte Komponenten-Zugangsdaten, Backup/Restore/Löschen, Projekt-Snapshot/Restore einschließlich Datenbank, Dotfile, leerem Ordner und Junction, interne Link-Ziele beim Staging, die Bereinigung fehlgeschlagener Snapshots sowie verwaister Metadaten. Für einen Freigabetest sollten zusätzlich die tatsächlich ausgelieferten MySQL- und MariaDB-Versionen gestartet und mit denselben Abläufen geprüft werden.
