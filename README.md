# PowerToys Extensions

[![CI](https://github.com/Delido/powertoys-extensions/actions/workflows/ci.yml/badge.svg)](https://github.com/Delido/powertoys-extensions/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Erweiterungen für die **Command Palette** von [Microsoft PowerToys](https://learn.microsoft.com/windows/powertoys/command-palette/overview). Sie bieten denselben Funktionsumfang wie meine [Raycast-Extensions](https://github.com/Delido/raycast-extensions), nativ in C# geschrieben und an die Möglichkeiten von PowerToys angepasst.

## Installation

Voraussetzung: [PowerToys](https://learn.microsoft.com/windows/powertoys/install) mit aktivierter Command Palette.

In PowerShell:

```powershell
irm https://raw.githubusercontent.com/Delido/powertoys-extensions/main/install.ps1 | iex
```

Das Skript lädt das aktuelle Release, installiert es und lässt die Command Palette die Erweiterungen neu laden. Beim ersten Mal fragt Windows einmal nach Administratorrechten (siehe [Warum eine UAC-Abfrage?](#warum-eine-uac-abfrage)). Updates installierst du mit demselben Befehl.

Alle Erweiterungen stecken in **einem** Paket. In der Command Palette erscheint jede einzeln unter **Einstellungen → Erweiterungen**, dort lässt sie sich abschalten und konfigurieren.

Deinstallieren: **Windows-Einstellungen → Apps → Installierte Apps → Delido PowerToys Extensions**.

### Warum eine UAC-Abfrage?

Die Command Palette lädt Erweiterungen nur aus MSIX-Paketen, und Windows installiert MSIX-Pakete nur von vertrauenswürdigen Herausgebern. Die Pakete sind mit einem eigenen Zertifikat signiert (`CN=Delido`, öffentlicher Teil in [certificate/](certificate/)). Das Skript trägt es einmalig unter *Vertrauenswürdige Personen* des Computers ein, dafür braucht es Administratorrechte. Danach laufen Installation und Updates ohne Abfrage.

## Erweiterungen

| Erweiterung | Wofür | Stand |
|---|---|---|
| **Keep Awake** | Rechner wach halten, optional Teams-Status auf Verfügbar, Umschalter im Dock | fertig |
| **Launcher Key** | Command Palette mit kurzem Antippen der **Win-Taste** öffnen | fertig |
| **OneDev** | Projekte, Issues, Pull Requests und Builds suchen, mit Filtern wie in OneDev, Beschreibung im Detailbereich | fertig |
| **Checkmk** | Probleme, Hosts und Services, bestätigen und Downtimes setzen, **Problem-Monitor** mit Windows-Benachrichtigung | fertig |
| **Snipe-IT** | Assets, Benutzer, Lizenzen, Zubehör und Verbrauchsmaterial, Check-in/-out, Asset per Tag oder Seriennummer (Barcode-Scanner), fällige Audits | fertig |
| **CS2 Nades** | Smoke-, Molotov-, Flash- und HE-Lineups der Premier-Maps mit Lineup-Bild, Video direkt in der Palette (per ffmpeg), live von [csnades.gg](https://csnades.gg) | fertig |
| **AMP** | CubeCoders-AMP-Game-Server: Start/Stop, Live-Konsole mit Befehlen, Spieler, Backups, Einstellungen, Zeitpläne, Server anlegen | fertig |
| **Power Plan** | Energiesparplan wechseln, automatisch Höchstleistung beim Spielen (Spielerkennung über Bibliotheken, eigene Liste, Vollbild), Aktivitätsprotokoll, Dock-Element | fertig |
| **MTR** | Traceroute wie WinMTR mit Statistik und Graphen, Ziele im Hintergrund überwachen (Ausfall, Verlust, Latenz, Routenwechsel) mit Benachrichtigung, HTML-Bericht | fertig |
| **SignalRGB** | PC-Sensoren live, Beleuchtung und Effekte, Headset-Mute und -Akku, Tastatur-Game-Mode, CS2-Integration, TeamSpeak-Mute mit Einrichtungsassistent, Audioausgabe, Mikrofon im Dock | fertig |
| **Paperless-ngx** | Dokumente suchen (Volltext, Filter, ähnliche), Posteingang, bearbeiten, herunterladen und hochladen; Token verschlüsselt, Entsperren per **Passwort oder Windows Hello** | fertig |
| **SSH Sessions** | SSH-Hosts finden und verbinden (Windows Terminal), Team-Host-Listen (OneDev, HTTPS, Freigabe), SOCKS-Tunnel mit Browser, SFTP-Dateibrowser über Windows-OpenSSH | fertig |
| **LAPS Connect** | WireGuard-IP eines Rechners finden, Windows-LAPS-Passwort aus Entra holen (Anmeldung per **Windows-SSO**), per SSH oder RDP verbinden; Passwort in der Zwischenablage ohne Verlauf mit Auto-Löschen; UniFi-Key im Tresor | fertig |
| **BookStack** | Wiki durchsuchen (mit Typ-Filter), zuletzt geänderte Seiten, Bücher und Regale durchblättern, Seiten als Markdown lesen | fertig |
| **SharePoint** | Dokumente, Seiten und Sites durchsuchen (KQL, Filter nach Typ), Vorschau, in Office-Desktop-App öffnen; Anmeldung per **Windows-SSO** | fertig |
| **Microsoft Teams** | Personen mit Status, Chats, Teams & Kanäle, Nachrichten suchen, Nachricht senden, Status und Statusnachricht setzen, Besprechungen, nächstem Meeting beitreten; Anmeldung per **Windows-SSO** | fertig |
| **Defender Schwachstellen** | Schwachstellen-Report (HTML) aus Microsoft Defender, CVEs, Software und Geräte samt Fundstellen durchsuchen, Top-Maßnahmen nach Gerätegruppe; Client-Secret im Tresor, Demodaten zum Ausprobieren | fertig |
| **GPT4Free** | Ask AI, AI Chat mit Verlauf, Textbefehle (Zusammenfassen, Übersetzen, Verbessern …), Bildschirm und Webseiten fragen, Bilder erzeugen, eigene AI-Befehle mit Platzhaltern, Presets; mit eigenen APIs (OpenAI, Anthropic, Gemini, OpenRouter, Ollama …) | fertig |

*Window Drag* entfällt, das kann PowerToys mit **Alt+Drag** bzw. **FancyZones** selbst.

## Win-Taste als Launcher-Taste

Die Command Palette lässt sich in PowerToys nur auf Tastenkombinationen legen (Standard **Win+Alt+Space**), nicht auf die Win-Taste allein. Auch der Tastatur-Manager hilft nicht: Er kann Win nur komplett umbelegen, dann gehen Win+E, Win+L usw. nicht mehr.

**Launcher Key** löst das wie Raycast: Ein **kurzes Antippen** von Win allein öffnet die Command Palette, ein zweites Antippen schließt sie wieder. Win zusammen mit einer anderen Taste funktioniert weiter normal, das Startmenü öffnest du mit **Ctrl+Esc**. Ist ein Administrator-Fenster im Vordergrund, verhält sich Win ganz normal, weil Windows dort keine eingeschleusten Tasten erlaubt. Einschalten unter **Einstellungen → Erweiterungen → Launcher Key** oder mit dem Befehl *Toggle Windows Key for Command Palette*.

Technisch läuft der Tastatur-Hook als eigener kleiner Prozess (`launcher-key.exe`). Er hängt damit nicht an den übrigen Erweiterungen und öffnet die Palette auch, wenn sie gerade neu startet. Win-Loslassen wird nie abgefangen, die Taste kann also nicht „hängen bleiben“.

Nutzt du parallel Raycast mit der Win-Taste, stell dort ein anderes Kürzel ein (z. B. Alt+Space). Die Win-Taste kann immer nur ein Launcher bekommen.

### Microsoft-Anmeldung (SharePoint, Teams, LAPS Connect)

Die Anmeldung läuft über den Windows-Kontomanager: Single Sign-on mit dem an Windows angemeldeten Arbeitskonto, MFA im Windows-Dialog, kein Browser. Die App-Registrierung in Entra braucht dafür unter **Authentifizierung → Mobile und Desktopanwendungen** die Umleitungs-URI

```
ms-appx-web://microsoft.aad.brokerplugin/<Client-ID>
```

### GPT4Free

Statt der kostenlosen Fremd-Anbieter (die in meiner Raycast-Kopie ohnehin ausgeblendet sind) nutzt die Erweiterung eigene APIs: **GPT4Free Preferences → Custom APIs → Add API**, Anbieter wählen, Schlüssel eintragen, die Modelle werden automatisch geladen. Schlüssel liegen DPAPI-verschlüsselt im Datenordner. Die Textbefehle starten mit dem Text aus der Zwischenablage, weil die Command Palette markierten Text nicht lesen kann: Text kopieren, Befehl aufrufen, Enter. *Check for Updates* und das Entwickler-Panel entfallen, Updates kommen über das Paket.

## Hotkeys und Links

- Jedem Befehl kannst du in der Command Palette unter **Einstellungen → Erweiterungen → \<Erweiterung\>** einen eigenen globalen Hotkey geben.
- Befehle lassen sich per Link starten, z. B. aus Makros oder Skripten: `x-cmdpal://commands/<provider-id>/<command-id>`. Ein Rechtsklick auf einen Befehl in der Command Palette bietet **Copy command link**.

## Sicherheit

- Zugangsdaten werden nie im Code oder Repo gespeichert, nur lokal im Datenordner des Pakets, verschlüsselt mit Windows DPAPI.
- **Paperless-ngx**, **LAPS Connect** und **Defender** legen ihr Geheimnis zusätzlich in einem Tresor ab: verschlüsselt mit einem eigenen Passwort (PBKDF2, AES-256-GCM) und optional mit **Windows Hello** (der Schlüssel wird aus einer Hello-Signatur abgeleitet, ohne Hello oder Passwort lässt er sich nicht öffnen). Entsperrt liegt es nur im Arbeitsspeicher und sperrt sich nach der eingestellten Zeit selbst.
- Die Hilfsprogramme lauschen nur auf `127.0.0.1`.

## Entwicklung

Voraussetzungen: .NET 10 SDK, Windows SDK 10.0.26100 (`winget install Microsoft.DotNet.SDK.10 Microsoft.WindowsSDK.10.0.26100`), für die Hilfsprogramme Go 1.26+.

```powershell
.\scripts\new-certificate.ps1                      # einmalig: Signaturzertifikat CN=Delido anlegen
.\scripts\build.ps1                                # baut und signiert artifacts\PowerToysExtensions.msix
.\install.ps1 -Package artifacts\PowerToysExtensions.msix
```

- Jede Erweiterung liegt in `src/PowerToysExtensions/Extensions/<Name>/` und ist eine eigene COM-Klasse (GUID in [Package.appxmanifest](src/PowerToysExtensions/Package.appxmanifest) und in der Klasse).
- Protokoll: `%LOCALAPPDATA%\Packages\Delido.PowerToysExtensions_*\LocalState\log.txt`.
- Ein Tag `v*` baut über den [Release-Workflow](.github/workflows/release.yml) ein signiertes Release. Das Zertifikat liegt dafür als Secret im Repo (`scripts\new-certificate.ps1 -GitHubSecret`).
