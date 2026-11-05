# PerformanceOverlay Plugins

This repository is the home for official PerformanceOverlay plugins and a guide for creating compatible plugins. Each plugin lives in its own folder with its source, project file, documentation, and installable `.dll`.

## Create a plugin

PerformanceOverlay loads trusted .NET plugins that implement `IOverlayPlugin` from the [`PerformanceOverlay.PluginApi`](https://github.com/LeoCvier/Performance_Overlay/tree/main/PluginApi). The current API targets **.NET 8 for Windows** and uses WPF.

### Requirements

- Build a Windows class library targeting `net8.0-windows` with WPF enabled.
- Reference `PerformanceOverlay.PluginApi` from the app's source repository. For a standalone project, the `clock/PluginApi` folder contains the API snapshot used by the Clock example.
- Include exactly one public, concrete class with a public parameterless constructor that implements `IOverlayPlugin` in the plugin assembly.
- Return a WPF `FrameworkElement` from `CreateView()`. Keep it compact and vertically centered so it fits the horizontal overlay.
- Stop timers, event handlers, and other resources in `Dispose()`.

### Minimal plugin

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PerformanceOverlay.PluginApi;

public sealed class ExamplePlugin : IOverlayPlugin
{
    public string Name => "Example";
    public string Description => "A short description shown in the plugin menu.";

    public FrameworkElement CreateView()
    {
        var label = new TextBlock
        {
            Text = "EXAMPLE ",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(
            TextBlock.ForegroundProperty,
            OverlayTheme.AccentBrushKey);
        return label;
    }

    public void Dispose() { }
}
```

Use `OverlayTheme.AccentBrushKey` for accent-colored labels so they follow the user's selected overlay color. The overlay adds the separator and aligns the plugin view automatically; plugins should return only their own content.

### Build and install

1. Create a folder named after the plugin, for example `example/`, and put the project, source, README, and built plugin assembly there. Follow [`clock/`](clock/) as a working example.
2. Build the project in Release configuration on Windows with the .NET 8 SDK. Select the plugin's main `.dll` from its build output; do not select `PerformanceOverlay.PluginApi.dll`.
3. In PerformanceOverlay's notification-area menu, choose **Plugins → Load plugin…**, select the plugin DLL, and confirm the trust prompt.

The app copies the selected assembly and nearby `.dll`, `.json`, and `.config` dependencies to `%LOCALAPPDATA%\PerformanceOverlay\Plugins`, then loads it. Installed plugins load again when the app starts. Keep any required dependency files beside the main plugin DLL.

## Security

Plugins run inside PerformanceOverlay with the same permissions as the app, which currently runs with administrator privileges. Only load plugins whose source you trust.

## Official plugins

- [Clock](clock/README.md) — adds the local time in 24-hour format.
