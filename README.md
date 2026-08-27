# AirCode
> Share code. Share files. Stay local.

A fully offline, peer-to-peer classroom communication and file-sharing application for developers and students. Operates entirely over a local Wi-Fi network — no internet, no cloud, no accounts.

## Quick Start

### Host (the computer creating the network)
1. Run `AirCode.exe`
2. Click **Start Network** on the Home page
3. Enter a network name and password
4. AirCode starts a local Wi-Fi hotspot (if your adapter supports it) and begins listening
5. Other computers connect to that Wi-Fi network

### Client (everyone else)
1. Connect your PC to the AirCode Wi-Fi network
2. Run `AirCode.exe`
3. Click **Connect** — AirCode automatically finds the host via UDP discovery
4. If auto-discovery fails, click "Enter manually" and type the host IP (shown on the host's Home page)
5. You're in — all members appear in the Members list

## Features
| Feature | Details |
|---|---|
| Group Chat | Real-time classroom chat through the host |
| Direct Messages | Private 1:1 chat with any connected member |
| File Sharing | Drag-and-drop, any file type up to 2 GB, direct TCP transfer |
| Code Sharing | Send syntax-highlighted snippets in 12 languages |
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
- **Windows netsh** — Wi-Fi hotspot management

## Building from source
```
cd src
dotnet build
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ../dist
```

## Requirements
- Windows 10 (19041+) or Windows 11
- .NET 10 runtime (bundled in the self-contained exe)
- Wi-Fi adapter that supports Windows Hosted Network (for hotspot creation)
