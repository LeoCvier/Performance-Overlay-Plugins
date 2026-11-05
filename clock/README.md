# Clock Plugin

Adds the local time in 24-hour format to the PerformanceOverlay. The `TIME` label follows the overlay's current accent color; the time value uses the same white text style as the built-in metrics. PerformanceOverlay adds the separator automatically and aligns the plugin with the overlay content.

## Install

1. Download `ClockPlugin.dll` from this folder or from the plugin's GitHub release.
2. In PerformanceOverlay's system tray menu, choose **Plugins → Load plugin…**.
3. Select `ClockPlugin.dll` and confirm the trust prompt.

The app copies the plugin into its local Plugins folder and loads it immediately. It will load automatically on the next start.

## Build from source

The source project and the matching v1.1.0 plugin API reference are included in this folder. On Windows with the .NET 8 SDK, build `ClockPlugin.csproj`; the installable assembly is written to `bin/Release/net8.0-windows/ClockPlugin.dll`.

The checked-in `ClockPlugin.dll` was built from the same source revision (`d0de7c6`) and is version 1.0.0.0.
