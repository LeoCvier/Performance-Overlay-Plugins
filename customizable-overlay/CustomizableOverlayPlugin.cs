using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using PerformanceOverlay.PluginApi;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfWindow = System.Windows.Window;

namespace PerformanceOverlay.CustomizableOverlay;

public sealed class CustomizableOverlayPlugin : IOverlayPlugin, IOverlayMetricVisibilityFilter, IOverlayPlacementProvider
{
    private static readonly string[] MetricKeys = ["FPS AVG", "FPS MAX", "FPS MIN", "CPU", "CPU TEMP", "RAM", "GPU", "GPU TEMP"];
    private static readonly Dictionary<string, string> ElementNames = new()
    {
        ["FPS AVG"] = "FpsAvg", ["FPS MAX"] = "FpsMax", ["FPS MIN"] = "FpsMin",
        ["CPU"] = "CpuLoad", ["CPU TEMP"] = "CpuTemp", ["RAM"] = "RamLoad",
        ["GPU"] = "GpuLoad", ["GPU TEMP"] = "GpuTemp"
    };
    private static readonly string[] SeparatorNames = ["Sep1", "Sep2", "Sep3", "Sep4", "Sep5", "Sep6", "Sep7"];
    private readonly Border _anchor = new() { Width = 1, Height = 1, Opacity = 0 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Settings _settings = Settings.Load();
    private readonly Dictionary<string, TextBlock> _metricBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UIElement> _originalChildren = new();
    private readonly List<UIElement> _extraChildren = new();
    private readonly List<TextBlock> _separators = new();
    private WpfWindow? _host;
    private StackPanel? _content;
    private Border? _background;
    private FrameworkElement? _availabilityDot;
    private SettingsWindow? _settingsWindow;
    private Forms.ToolStripMenuItem? _pluginMenu;
    private EventHandler? _pluginMenuOpening;
    private double _originalWindowOpacity;
    private Transform? _originalLayoutTransform;
    private double _originalLeft, _originalTop;
    private WpfColor _originalAccent = Colors.Cyan;
    private double _hoverTarget = -1;
    private string _appliedOrder = "";
    private bool _active, _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    public CustomizableOverlayPlugin()
    {
        _anchor.Loaded += AnchorLoaded;
        _anchor.Unloaded += AnchorUnloaded;
        _anchor.IsVisibleChanged += AnchorIsVisibleChanged;
        _timer.Tick += (_, _) => { UpdateHoverTransparency(); UpdateSeparators(); };
    }

    public string Name => "Customizable Overlay";
    public string Description => "Configure the host overlay's color, size, monitor, metrics and hover transparency.";
    public bool IsMetricEnabled(string metricKey) => _settings.Enabled.Contains(metricKey);
    public FrameworkElement CreateView() => _anchor;

    private void AnchorLoaded(object sender, RoutedEventArgs e)
    {
        ActivateFromAnchor();
    }

    private void AnchorIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is bool visible)
        {
            if (visible) ActivateFromAnchor();
            else DeactivatePluginView();
        }
    }

    private void ActivateFromAnchor()
    {
        if (_disposed || _active || !_anchor.IsVisible) return;
        _host = Window.GetWindow(_anchor);
        if (_host is null) return;
        _content = FindNamed<StackPanel>(_host, "OverlayContent");
        _background = FindNamed<Border>(_host, "OverlayBackground");
        if (_content is null || _background is null) return;

        _active = true;
        _originalWindowOpacity = _host.Opacity;
        _originalLayoutTransform = _background.LayoutTransform;
        _originalLeft = _host.Left;
        _originalTop = _host.Top;
        _originalAccent = ReadAccent(_host);
        if (string.IsNullOrWhiteSpace(_settings.Color)) _settings.Color = _originalAccent.ToString();
        CaptureOverlayElements();
        AttachTrayActions(_host);
        _host.SizeChanged += HostSizeChanged;
        _timer.Start();
        ApplyStyleSettings();
        ApplyMetricSelection();
        ApplySavedPosition();
        UpdateHoverTransparency();
    }

    private void AnchorUnloaded(object sender, RoutedEventArgs e)
    {
        DeactivatePluginView();
    }

    private void DeactivatePluginView()
    {
        if (!_active) return;
        _timer.Stop();
        RestoreHostState();
        _active = false;
    }

    private void CaptureOverlayElements()
    {
        if (_content is null) return;
        _originalChildren.Clear(); _extraChildren.Clear(); _metricBlocks.Clear(); _separators.Clear();
        _originalChildren.AddRange(_content.Children.Cast<UIElement>());
        _availabilityDot = FindNamed<FrameworkElement>(_host!, "AvailabilityDot");
        foreach (var pair in ElementNames)
        {
            var block = FindNamed<TextBlock>(_host!, pair.Value);
            if (block is not null) _metricBlocks[pair.Key] = block;
        }
        foreach (var name in SeparatorNames)
        {
            var separator = FindNamed<TextBlock>(_host!, name);
            if (separator is not null) _separators.Add(separator);
        }
        var known = new HashSet<UIElement>(_metricBlocks.Values.Cast<UIElement>().Concat(_separators));
        if (_availabilityDot is not null) known.Add(_availabilityDot);
        _extraChildren.AddRange(_originalChildren.Where(x => !known.Contains(x)));
    }

    private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T found && found.Name == name) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = FindNamed<T>(VisualTreeHelper.GetChild(root, i), name);
            if (child is not null) return child;
        }
        return null;
    }

    private static WpfColor ReadAccent(WpfWindow host)
    {
        var field = host.GetType().GetField("_accentColor", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(host) is WpfColor color ? color : WpfColor.FromRgb(53, 221, 240);
    }

    private void ApplyHostAccent(WpfColor color)
    {
        if (_host is null) return;
        var method = _host.GetType().GetMethod("SetAccentColor", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_host, [color]);
    }

    private void ApplyStyleSettings()
    {
        if (!_active || _host is null || _background is null) return;
        _background.LayoutTransform = Math.Abs(_settings.Scale - 1) < .001
            ? _originalLayoutTransform ?? Transform.Identity
            : new ScaleTransform(_settings.Scale, _settings.Scale);
        var desired = _settings.Accent;
        if (ReadAccent(_host) != desired) ApplyHostAccent(desired);
    }

    private void UpdateHoverTransparency()
    {
        if (!_active || _host is null || !GetCursorPos(out var cursor)) return;
        var topLeft = _host.PointToScreen(new WpfPoint(0, 0));
        var bottomRight = _host.PointToScreen(new WpfPoint(_host.ActualWidth, _host.ActualHeight));
        double dx = Math.Max(Math.Max(topLeft.X - cursor.X, 0), cursor.X - bottomRight.X);
        double dy = Math.Max(Math.Max(topLeft.Y - cursor.Y, 0), cursor.Y - bottomRight.Y);
        bool near = Math.Sqrt(dx * dx + dy * dy) <= Math.Max(0, _settings.HoverRadius);
        double target = _originalWindowOpacity * (near ? 1 - Math.Clamp(_settings.HoverTransparency, 0, 100) / 100 : 1);
        if (Math.Abs(target - _hoverTarget) < .001) return;
        _hoverTarget = target;
        _host.BeginAnimation(WpfWindow.OpacityProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(110))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void ApplyMetricSelection()
    {
        if (!_active || _host is null || _content is null) return;
        ApplyStyleSettings();
        InvokeHostVisibilityRefresh();
        var orderKey = string.Join("|", _settings.Order);
        if (!string.Equals(orderKey, _appliedOrder, StringComparison.Ordinal))
        {
            ReorderMetricChildren(_settings.Order);
            _appliedOrder = orderKey;
        }
        UpdateSeparators();
    }

    private void ReorderMetricChildren(IReadOnlyList<string> order)
    {
        if (_content is null) return;
        foreach (var element in _metricBlocks.Values.Cast<UIElement>().Concat(_separators).ToList())
            _content.Children.Remove(element);

        int insertAt = _availabilityDot is not null && _content.Children.Contains(_availabilityDot)
            ? _content.Children.IndexOf(_availabilityDot) + 1
            : 0;
        int separatorIndex = 0;
        foreach (var key in order.Where(_metricBlocks.ContainsKey))
        {
            _content.Children.Insert(insertAt++, _metricBlocks[key]);
            if (separatorIndex < _separators.Count)
                _content.Children.Insert(insertAt++, _separators[separatorIndex++]);
        }
    }

    private void UpdateSeparators()
    {
        if (!_active) return;
        int separatorIndex = 0;
        foreach (var key in _settings.Order.Where(_metricBlocks.ContainsKey))
        {
            if (separatorIndex >= _separators.Count) break;
            var currentVisible = _settings.Enabled.Contains(key) && _metricBlocks[key].Visibility == Visibility.Visible;
            var index = _settings.Order.IndexOf(key);
            var nextVisible = _settings.Order.Skip(index + 1).Any(next => _settings.Enabled.Contains(next) &&
                _metricBlocks.TryGetValue(next, out var nextBlock) && nextBlock.Visibility == Visibility.Visible);
            _separators[separatorIndex++].Visibility = currentVisible && nextVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void InvokeHostVisibilityRefresh()
    {
        if (_host is null) return;
        _host.GetType().GetMethod("RefreshMetricVisibility", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_host, null);
    }

    private void AttachTrayActions(WpfWindow host)
    {
        var field = host.GetType().GetField("_pluginMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        _pluginMenu = field?.GetValue(host) as Forms.ToolStripMenuItem;
        if (_pluginMenu is null) return;
        _pluginMenuOpening = (_, _) => AppendTrayActions();
        _pluginMenu.DropDownOpening += _pluginMenuOpening;
    }

    private void AppendTrayActions()
    {
        if (_pluginMenu is null) return;
        _pluginMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        var configure = new Forms.ToolStripMenuItem("Configure Customizable Overlay…") { Enabled = _active };
        configure.Click += (_, _) => OpenSettings();
        _pluginMenu.DropDownItems.Add(configure);
    }

    private void OpenSettings()
    {
        if (!_active) return;
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_settings, () =>
        {
            _settings.Save();
            ApplyStyleSettings();
            ApplyMetricSelection();
            ApplySavedPosition();
            UpdateHoverTransparency();
        });
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void ApplySavedPosition()
    {
        if (!_active || _host is null) return;
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0) return;
        var screen = screens.FirstOrDefault(x => x.DeviceName.Equals(_settings.Screen, StringComparison.OrdinalIgnoreCase))
            ?? Forms.Screen.PrimaryScreen ?? screens[0];
        _settings.Screen = screen.DeviceName;
        string placement = _host.GetType().GetField("_overlayPosition", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_host) as string ?? "Top Left";
        const double margin = 8;
        _host.Left = placement switch
        {
            "Top Center" => screen.WorkingArea.Left + (screen.WorkingArea.Width - _host.ActualWidth) / 2,
            "Top Right" => screen.WorkingArea.Right - _host.ActualWidth - margin,
            _ => screen.WorkingArea.Left + margin
        };
        _host.Top = screen.WorkingArea.Top + margin;
    }

    public bool TryGetPosition(double overlayWidth, double overlayHeight, out double left, out double top)
    {
        left = top = 0;
        if (!_active) return false;
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0) return false;
        var screen = screens.FirstOrDefault(x => x.DeviceName.Equals(_settings.Screen, StringComparison.OrdinalIgnoreCase))
            ?? Forms.Screen.PrimaryScreen ?? screens[0];
        string placement = _host?.GetType().GetField("_overlayPosition", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_host) as string ?? "Top Left";
        const double margin = 8;
        left = placement switch
        {
            "Top Center" => screen.WorkingArea.Left + (screen.WorkingArea.Width - overlayWidth) / 2,
            "Top Right" => screen.WorkingArea.Right - overlayWidth - margin,
            _ => screen.WorkingArea.Left + margin
        };
        top = screen.WorkingArea.Top + margin;
        return true;
    }

    private void HostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_active || _host is null) return;
        _host.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplySavedPosition));
    }

    private void AnchorUnloadedCleanup()
    {
        if (!_active || _host is null) return;
        _active = false;
        if (_pluginMenu is not null && _pluginMenuOpening is not null) _pluginMenu.DropDownOpening -= _pluginMenuOpening;
        _host.SizeChanged -= HostSizeChanged;
        _host.BeginAnimation(WpfWindow.OpacityProperty, null);
        _host.Opacity = _originalWindowOpacity;
        _host.Left = _originalLeft; _host.Top = _originalTop;
        if (_background is not null)
        {
            _background.LayoutTransform = _originalLayoutTransform ?? Transform.Identity;
        }
        ReorderMetricChildren(MetricKeys);
        foreach (var block in _metricBlocks.Values) block.Visibility = Visibility.Collapsed;
        ApplyHostAccent(_originalAccent);
        InvokeHostVisibilityRefresh();
        _hoverTarget = -1;
        _appliedOrder = "";
        _pluginMenu = null; _pluginMenuOpening = null;
        _settingsWindow?.Close();
    }

    private void RestoreHostState() => AnchorUnloadedCleanup();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _anchor.Loaded -= AnchorLoaded;
        _anchor.Unloaded -= AnchorUnloaded;
        _anchor.IsVisibleChanged -= AnchorIsVisibleChanged;
        _settingsWindow?.Close();
        if (_active) RestoreHostState();
    }

    private sealed class Settings
    {
        public int ConfigVersion { get; set; }
        public string Color { get; set; } = "";
        public double Scale { get; set; } = 1;
        public double HoverTransparency { get; set; } = 65;
        public double HoverRadius { get; set; } = 56;
        public string Screen { get; set; } = "Primary";
        public List<string> Order { get; set; } = MetricKeys.ToList();
        public HashSet<string> Enabled { get; set; } = new(MetricKeys.Where(x => x is "FPS AVG" or "FPS MAX" or "FPS MIN" or "CPU" or "RAM" or "GPU"));
        private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerformanceOverlay", "customizable-overlay.json");
        public WpfColor Accent => string.IsNullOrWhiteSpace(Color)
            ? WpfColor.FromRgb(53, 221, 240)
            : (WpfColor)ColorConverter.ConvertFromString(Color);
        public static Settings Load()
        {
            Settings settings;
            try { settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathName)) ?? new(); }
            catch { settings = new(); }
            if (settings.ConfigVersion < 1)
            {
                if (string.Equals(settings.Color, "#35DDF0", StringComparison.OrdinalIgnoreCase)) settings.Color = "";
                settings.HoverTransparency = 65;
                settings.HoverRadius = 56;
                settings.ConfigVersion = 1;
            }
            var screens = Forms.Screen.AllScreens;
            if (screens.Length > 0 && !screens.Any(x => x.DeviceName.Equals(settings.Screen, StringComparison.OrdinalIgnoreCase)))
                settings.Screen = (Forms.Screen.PrimaryScreen ?? screens[0]).DeviceName;
            settings.Order = settings.Order.Where(k => MetricKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Concat(MetricKeys.Where(k => !settings.Order.Contains(k, StringComparer.OrdinalIgnoreCase))).ToList();
            settings.Enabled.IntersectWith(MetricKeys);
            return settings;
        }
        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private sealed class SettingsWindow : WpfWindow
    {
        private readonly Settings _settings;
        private readonly Action _changed;
        private readonly StackPanel _metricRows = new();
        private readonly Border _colorPreview = new() { Width = 30, Height = 20, CornerRadius = new CornerRadius(4), Margin = new Thickness(8, 0, 0, 0) };
        private readonly HueWheel _wheel = new() { Width = 160, Height = 160, Margin = new Thickness(0, 8, 0, 8) };
        public SettingsWindow(Settings settings, Action changed)
        {
            _settings = settings; _changed = changed;
            Title = "Customizable Overlay"; Width = 420; Height = 680; ResizeMode = ResizeMode.CanResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(WpfColor.FromRgb(29, 33, 41)); Foreground = Brushes.White;
            var root = new StackPanel { Margin = new Thickness(18) };
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.Children.Add(new TextBlock { Text = "Overlay settings", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12) });
            var colorRow = new StackPanel { Orientation = Orientation.Horizontal };
            colorRow.Children.Add(_wheel); colorRow.Children.Add(_colorPreview); root.Children.Add(colorRow);
            _wheel.ColorChanged += c => { _settings.Color = c.ToString(); _colorPreview.Background = new SolidColorBrush(c); _changed(); };
            _wheel.SetColor(_settings.Accent); _colorPreview.Background = new SolidColorBrush(_settings.Accent);
            root.Children.Add(new TextBlock { Text = "Overlay size", Foreground = Brushes.White });
            AddSlider(root, .65, 2.2, _settings.Scale, v => _settings.Scale = v, "Scale");
            root.Children.Add(new TextBlock { Text = "Hover transparency (100% hides the overlay)", Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 0) });
            AddSlider(root, 0, 100, _settings.HoverTransparency, v => _settings.HoverTransparency = v, "Fade %");
            root.Children.Add(new TextBlock { Text = "Hover starts within this distance of the overlay", Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 0) });
            AddSlider(root, 0, 500, _settings.HoverRadius, v => _settings.HoverRadius = v, "Radius px");
            root.Children.Add(new TextBlock { Text = "Display", Foreground = Brushes.White, FontSize = 16, Margin = new Thickness(0, 12, 0, 6) });
            foreach (var key in MetricKeys)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var check = new CheckBox { Content = key, IsChecked = _settings.Enabled.Contains(key), Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, MinWidth = 105 };
                check.Checked += (_, _) => { _settings.Enabled.Add(key); _changed(); };
                check.Unchecked += (_, _) => { _settings.Enabled.Remove(key); _changed(); };
                row.Children.Add(check);
                var up = new Button { Content = "↑", Width = 34, Margin = new Thickness(4, 0, 0, 0) };
                var down = new Button { Content = "↓", Width = 34, Margin = new Thickness(4, 0, 0, 0) };
                up.Click += (_, _) => MoveMetric(key, -1); down.Click += (_, _) => MoveMetric(key, 1);
                DockPanel.SetDock(down, Dock.Right); DockPanel.SetDock(up, Dock.Right); row.Children.Add(down); row.Children.Add(up); _metricRows.Children.Add(row);
            }
            root.Children.Add(_metricRows);
            root.Children.Add(new TextBlock { Text = "Monitor", Foreground = Brushes.White, FontSize = 16, Margin = new Thickness(0, 12, 0, 4) });
            var screens = Forms.Screen.AllScreens;
            var combo = new ComboBox { ItemsSource = screens.Select((s, i) => new ScreenEntry(s, i)).ToList(), DisplayMemberPath = nameof(ScreenEntry.Label), SelectedValuePath = nameof(ScreenEntry.DeviceName), SelectedValue = _settings.Screen };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedValue is string device) _settings.Screen = device; _changed(); };
            root.Children.Add(combo);
            root.Children.Add(new TextBlock { Text = "The overlay remains click-through and keeps its original style. Hover fading changes only when the pointer enters the configured distance. Placement follows the host's corner setting on the selected monitor.", Foreground = new SolidColorBrush(WpfColor.FromRgb(180, 185, 195)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) });
        }
        private void AddSlider(System.Windows.Controls.Panel parent, double min, double max, double value, Action<double> setter, string label)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = label, Width = 62, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 260, TickFrequency = .05, IsSnapToTickEnabled = false };
            var labelValue = new TextBlock { Width = 54, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            string Format(double number) => label.Contains("%", StringComparison.Ordinal) ? $"{number:0}%" : label.Contains("px", StringComparison.Ordinal) ? $"{number:0} px" : number.ToString("0.00");
            void Update() { setter(slider.Value); labelValue.Text = Format(slider.Value); _changed(); }
            slider.ValueChanged += (_, _) => Update(); labelValue.Text = Format(value); row.Children.Add(slider); row.Children.Add(labelValue); parent.Children.Add(row);
        }
        private void MoveMetric(string key, int direction)
        {
            int index = _settings.Order.IndexOf(key), next = index + direction;
            if (index < 0 || next < 0 || next >= _settings.Order.Count) return;
            (_settings.Order[index], _settings.Order[next]) = (_settings.Order[next], _settings.Order[index]);
            _changed();
            var replacement = new SettingsWindow(_settings, _changed) { Owner = this };
            replacement.Show(); Close();
        }
        private sealed record ScreenEntry(Forms.Screen Screen, int Index)
        {
            public string DeviceName => Screen.DeviceName;
            public string Label => $"{(Screen.Primary ? "Primary" : "Screen")} {Index + 1} ({Screen.Bounds.Width}×{Screen.Bounds.Height})";
        }
    }

    private sealed class HueWheel : FrameworkElement
    {
        private WriteableBitmap? _bitmap;
        private WpfColor _color = Colors.Cyan;
        public event Action<WpfColor>? ColorChanged;
        public void SetColor(WpfColor c) { _color = c; InvalidateVisual(); }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            int size = Math.Max(1, (int)Math.Min(ActualWidth, ActualHeight));
            if (_bitmap is null || _bitmap.PixelWidth != size) { _bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null); DrawWheel(size); }
            dc.DrawImage(_bitmap, new Rect(0, 0, size, size));
            dc.DrawEllipse(null, new Pen(Brushes.White, 2), new WpfPoint(size / 2, size / 2), size * .48, size * .48);
        }
        private void DrawWheel(int size)
        {
            if (_bitmap is null) return;
            var bytes = new byte[size * size * 4]; double radius = size / 2d;
            for (int y=0;y<size;y++) for (int x=0;x<size;x++)
            {
                double dx=(x+.5-radius)/radius, dy=(y+.5-radius)/radius, sat=Math.Sqrt(dx*dx+dy*dy); int p=(y*size+x)*4;
                if (sat>1) { bytes[p+3]=0; continue; }
                double hue=(Math.Atan2(dy,dx)*180/Math.PI+360)%360; var c=HSV(hue, sat, 1);
                bytes[p]=c.B; bytes[p+1]=c.G; bytes[p+2]=c.R; bytes[p+3]=255;
            }
            _bitmap.WritePixels(new Int32Rect(0,0,size,size),bytes,size*4,0);
        }
        protected override void OnMouseDown(MouseButtonEventArgs e) { base.OnMouseDown(e); CaptureMouse(); Pick(e.GetPosition(this)); }
        protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e) { base.OnMouseMove(e); if (IsMouseCaptured) Pick(e.GetPosition(this)); }
        protected override void OnMouseUp(MouseButtonEventArgs e) { base.OnMouseUp(e); ReleaseMouseCapture(); }
        private void Pick(WpfPoint p)
        {
            double n=Math.Min(ActualWidth,ActualHeight), dx=(p.X-n/2)/(n/2),dy=(p.Y-n/2)/(n/2); if(dx*dx+dy*dy>1)return;
            _color=HSV((Math.Atan2(dy,dx)*180/Math.PI+360)%360,Math.Sqrt(dx*dx+dy*dy),1); ColorChanged?.Invoke(_color);
        }
        private static WpfColor HSV(double h,double s,double v)
        {
            double c=v*s,x=c*(1-Math.Abs((h/60)%2-1)),m=v-c;
            (double r,double g,double b)=h switch { <60=>(c,x,0d), <120=>(x,c,0d), <180=>(0d,c,x), <240=>(0d,x,c), <300=>(x,0d,c), _=>(c,0d,x) };
            return WpfColor.FromRgb((byte)((r+m)*255),(byte)((g+m)*255),(byte)((b+m)*255));
        }
    }
}