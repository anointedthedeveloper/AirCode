# AirCode
> Share code. Share files. Stay local.

A fully offline, peer-to-peer classroom communication and file-sharing application for developers and students. Operates entirely over a local Wi-Fi network — no internet, no cloud, no accounts.

## Quick Start

### How the network works
```
 Internet / school Wi-Fi  ──▶  Host PC  ══ Windows Mobile Hotspot ══▶  phones & laptops
                              (AirCode)                               (AirCode clients)
```
The host PC joins a Wi-Fi network, then shares it as a hotspot. Every other device joins that
hotspot and opens AirCode — chat, files and code snippets all flow through the host.

### Host (the computer creating the network)
1. Connect the PC to Wi-Fi
2. Run `AirCode.exe` (it asks for Administrator — needed for the hotspot and firewall rule)
3. Click **Start Network**, pick a network name and password (8+ characters)
4. AirCode turns on the Windows hotspot and shows the name/password on the Home page
   (untick the hotspot option to host on the Wi-Fi you're already on instead)

### Client (everyone else)
1. Join the host's hotspot from the device's Wi-Fi settings
2. Run `AirCode.exe` and click **Connect** — the host is found automatically (UDP broadcast,
   falling back to the hotspot gateway)
3. If that fails, click "Enter manually" and type the host IP (shown on the host's Home page)
4. If the connection drops (out of range, host restarts) AirCode reconnects on its own

## Features
| Feature | Details |
|---|---|
| Group Chat | Real-time classroom chat through the host, saved history, unread badges |
| Direct Messages | Private 1:1 chat with any member (including the host); history kept across restarts |
| File Sharing | Drag-and-drop, any file type up to 2 GB, direct TCP transfer |
| Code Sharing | Send snippets in 12 languages; received snippets are kept in an inbox |
| Member List | See everyone connected, send files or messages from the list |
| Transfer History | Track all active, completed, and failed transfers |
| Dark Mode | Toggle in Settings |
| Offline-only | Zero internet dependency — works in a basement with no router |

## Technology
- **C# / WPF** (.NET 10, Windows-native)
- **WebSocket** (System.Net.WebSockets) — real-time messaging
- **UDP broadcast** — automatic host discovery (no IP config needed)
- **TCP** — direct peer-to-peer file transfers
- **SQLite** (Microsoft.Data.Sqlite) — local settings, chat history, transfer history
- **Windows Mobile Hotspot** (WinRT `NetworkOperatorTetheringManager`) — hotspot management

## Building from source
```
cd src
dotnet build
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ../dist
```

## Requirements
- Windows 10 (19041+) or Windows 11
- .NET 10 runtime (bundled in the self-contained exe)
- Wi-Fi adapter that supports Windows Mobile Hotspot (host only)
