# MeinExplorer – stabilisierte Dateiansicht

## Änderungen
- Standardsortierung: **Geändert**, neueste zuerst (Ordner vor Dateien).
- Sortiervergleich ohne erneute Dateisystemzugriffe: Größe und Änderungsdatum werden beim Auflisten einmal erfasst.
- Schutz vor sich überschneidenden automatischen Aktualisierungen.
- Ein Leseproblem bei einem einzelnen Eintrag verhindert nicht mehr die Darstellung aller anderen Elemente.
- Fehlerprotokoll: `%APPDATA%\MeinExplorer\errors.log`.
- Favoriten, Super-Favoriten, Startordner, Mac-Zwischenablage und kompakte Designer-Aktionsleiste bleiben aus der vorherigen Version erhalten.

## Installation
- Falls du die zuletzt gelieferte kompakte Designer-Version verwendest, **nur `ExplorerForm.cs` ersetzen**.
- Ansonsten das gesamte ZIP als separates Projekt öffnen.
- Visual Studio: **Erstellen > Projektmappe neu erstellen**.
- Aktuellen Suchfilter rechts oben prüfen: Ein Filter kann Dateien ausblenden.

## Zur Frage mit den zwei 'bearbeiteten' Dateien
Diese Version schreibt Benutzer-Einstellungen und das Fehlerprotokoll nur nach `%APPDATA%\MeinExplorer`, nicht in `.cs`- oder `.Designer.cs`-Dateien. Ohne den Namen der in Visual Studio als geändert markierten Dateien ist die genaue Ursache offen. Prüfe im Git-/Änderungen-Fenster, welche Dateien betroffen sind. Visual Studios Designer kann Designer-Dateien unabhängig vom laufenden Programm neu generieren.

## Teststand
Quellcode-Prüfung durchgeführt. Ein Windows-Build und die Ausführung in Visual Studio konnten in der vorliegenden Linux-Umgebung nicht getestet werden.
