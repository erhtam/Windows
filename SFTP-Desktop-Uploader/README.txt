SFTP Desktop Uploader (SSH) – Windows / C# / .NET 8

1. Öffne SftpDesktopUploader.csproj in Visual Studio 2022 oder neuer.
2. Stelle sicher, dass das .NET-Desktopentwicklung-Workload installiert ist.
3. NuGet-Pakete wiederherstellen und das Projekt erstellen.
4. Starte das Programm, öffne die SSH/SFTP-Einstellungen und trage ein:
   - Host (ohne sftp://)
   - Port (normalerweise 22)
   - Benutzername und Passwort
   - Remote-Verzeichnis (existierendes Zielverzeichnis)
   - SSH-Host-Key-Fingerabdruck (SHA256:...)
5. Erstelle eine Desktop-Verknüpfung zur SftpDesktopUploader.exe.
6. Ziehe eine oder mehrere Dateien auf die Verknüpfung oder auf das geöffnete Fenster.

Bestehende Dateien gleichen Namens im Zielverzeichnis werden per SFTP überschrieben.
Das Konto benötigt Schreibrechte. Das Zielverzeichnis muss bereits existieren.

SICHERHEIT:
- SFTP läuft über SSH, nicht über FTP/FTPS.
- Der Host-Key-Fingerabdruck MUSS mit dem echten, separat geprüften Fingerabdruck
  des Servers übereinstimmen; sonst wird die Verbindung abgelehnt.
- Frage den Serveradministrator nach dem SHA256-SSH-Host-Key-Fingerabdruck.
  Alternativ kann er auf dem Server z.B. Folgendes ausführen:
  ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
  Nutze den Fingerabdruck der Host-Key-Art, die der Server beim Verbindungsaufbau anbietet.
- Das Passwort wird im Benutzerprofil via Windows DPAPI verschlüsselt gespeichert.
- Diese Version unterstützt Passwortanmeldung (keine privaten SSH-Keys).

HINWEIS: Das Paket ist als Quellprojekt geliefert, nicht als vorkompilierte EXE.
