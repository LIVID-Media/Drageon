# Drageon-Branding austauschen

- `drageon-logo.png` wird in der linken Seitenleiste angezeigt.
- `drageon-icon.png` ist die hochauflösende Vorlage für das Programmsymbol.
- `drageon.ico` wird aus dem Icon-PNG erzeugt und in `Drageon.exe`, im Fenstertitel und im Tray verwendet.

Zum Austauschen die beiden PNG-Dateien unter denselben Namen ersetzen. Das Logo wird beim nächsten Build direkt übernommen. Um zusätzlich das Windows-, Fenster- und Tray-Icon zu aktualisieren, anschließend im Projektstamm ohne Administratorrechte ausführen:

```text
scripts\update-branding.cmd
```

Alternativ kann das PowerShell-Skript mit einer nur für diesen Aufruf umgangenen Ausführungsrichtlinie gestartet werden:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\update-branding.ps1
```

Ein direkter Aufruf der `.ps1`-Datei wird von einer restriktiven PowerShell-Ausführungsrichtlinie bereits vor dem Start des Skripts blockiert. Die `.cmd`-Datei startet es deshalb nur für diesen Aufruf mit `-ExecutionPolicy Bypass`; Systemeinstellungen werden nicht verändert.

Das Skript erzeugt ausschließlich `drageon.ico` aus `drageon-icon.png`; die PNG-Dateien selbst werden nicht verändert. Danach Drageon normal bauen oder veröffentlichen.
