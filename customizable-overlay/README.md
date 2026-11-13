# Customizable Overlay

Configure the core overlay's color, scale, monitor, visible metrics, metric order, hover transparency, and hover radius.

## Build

Build with the .NET 8 SDK on Windows:

```powershell
dotnet build .\CustomizableOverlayPlugin.csproj -c Release
```

Copy `bin\Release\net8.0-windows\CustomizableOverlayPlugin.dll` into a folder named `CustomizableOverlayPlugin` under `%LOCALAPPDATA%\PerformanceOverlay\Plugins`, then reload the overlay.

The plugin uses the host's metric-visibility and placement APIs so the core refresh loop does not briefly show disabled values and monitor/corner placement remains consistent.
