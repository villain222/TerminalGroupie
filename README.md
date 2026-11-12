markdown
# Terminal Groupie 🐘

A Windows desktop application that lets you run multiple terminal sessions and keep them alive while minimizing to the system tray. Perfect for long-running build processes, background tasks, or multi-window workflows without cluttering your taskbar.

## Features

- **Live Terminals**: Each tab runs its own persistent shell (default `cmd.exe`, switchable to PowerShell).
- **Tray Management**: Minimize any terminal session to the system tray with a single click. The tray icon manages all running terminals in one place.
- **Right‑Click Context Menu**: From the tray, right-click to see every minimized terminal, open it back into a window, or kill that specific process (and its child commands).
- **Tabbed Interface**: Multiple sessions live side-by-side with easy tab switching and minimize controls per session.

## Building & Running

### Prerequisites

- Windows 10/11
- [.NET SDK](https://dotnet.microsoft.com/download) version 8 or later (tested on .NET 8, targets `net10.0-windows` for future-proofing).

```powershell
cd "D:\AI stuff\TerminalGroupie"
dotnet restore --force
dotnet build -c Release
dotnet publish -r win-x64 --self-contained true -o "./publish/Release"
start .\publish\Release\TerminalGroupie.exe
```
Tip: In Visual Studio, set the startup project to TerminalGroupie.csproj and press F5.
### Project Structure
.csproj, .sln: Build configuration.

src/MainWindow.xaml/.cs: Main window with tabbed terminal sessions.

src/TerminalWindow.xaml/.cs: Individual terminal windows (title only; content rendered via WPF console).

src/TrayIcon.cs: System tray icon and context menu for minimized terminals.

src/Native.cs, Utilities.cs, SessionManager.cs, etc.: Core logic, P/Invoke wrappers, session tracking, and process management.
