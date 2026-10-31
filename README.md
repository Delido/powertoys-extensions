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
| **BookStack** | Wiki durchsuchen (mit Typ-Filter), zuletzt geänderte Seiten, Bücher und Regale durchblättern, Seiten als Markdown lesen | fertig |

Weitere folgen: SharePoint, Teams, SSH, LAPS Connect, Defender Schwachstellen, GPT4Free. *Window Drag* entfällt, das kann PowerToys mit **Alt+Drag** bzw. **FancyZones** selbst.

## Win-Taste als Launcher-Taste

Die Command Palette lässt sich in PowerToys nur auf Tastenkombinationen legen (Standard **Win+Alt+Space**), nicht auf die Win-Taste allein. Auch der Tastatur-Manager hilft nicht: Er kann Win nur komplett umbelegen, dann gehen Win+E, Win+L usw. nicht mehr.

**Launcher Key** löst das wie Raycast: Ein **kurzes Antippen** von Win allein öffnet die Command Palette statt des Startmenüs. Win zusammen mit einer anderen Taste funktioniert weiter normal, das Startmenü öffnest du dann mit **Ctrl+Esc**. Einschalten unter **Einstellungen → Erweiterungen → Launcher Key** oder mit dem Befehl *Toggle Windows Key for Command Palette*.

Nutzt du parallel Raycast mit der Win-Taste, stell dort ein anderes Kürzel ein (z. B. Alt+Space). Die Win-Taste kann immer nur ein Launcher bekommen.

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
