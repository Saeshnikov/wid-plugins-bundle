using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using WidBar.SDK;

namespace Personal.Happ.ExtensionApp;

public sealed class MainPlugin : WidgetPluginBase, IConfigurableWidgetPlugin
{
    public override string Id => "com.personal.happ";
    public override string Name => "Happ Control";
    public override int PreviewLogicalWidth => _settings.PreviewWidth;
    private Settings _settings = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Microsoft.UI.Dispatching.DispatcherQueue? _ui;
    private Button? _open, _power;
    private Viewbox? _shieldOn, _shieldOff;
    private FontIcon? _shieldUnknown;
    private ProgressRing? _powerSpinner;
    private CoreState _state = CoreState.Unknown;
    private Task? _monitor;
    private int _operationVersion;
    private enum CoreState { Unknown, Stopped, Running }
    private bool _busy, _switching, _disposed;
    private Task _inflight = Task.CompletedTask;
    private string _message = "Ready. Monitoring Happ Xray process (not remote connectivity).";
    private static readonly SemaphoreSlim OperationLock = new(1, 1);

    public override async Task InitializeAsync(IWidgetContext context)
    {
        _settings = Settings.Read(context.SettingsJson);
        await base.InitializeAsync(context);
    }
    public override void OnSettingsDraftChanged(string json)
    {
        if (_ui != null && !_ui.HasThreadAccess) { _ui.TryEnqueue(() => OnSettingsDraftChanged(json)); return; }
        if (_disposed) return;
        var previous = _settings;
        _settings = Settings.Read(json);
        if (previous.PreviewWidth != _settings.PreviewWidth) Context?.RequestPreviewRefresh();
    }
    private static Button CompactButton(string text, string name)
    {
        var button = new Button { Content = text, FontSize = 12, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(5, 3, 5, 3), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, name);
        return button;
    }
    // Original Microsoft Fluent UI System Icons, MIT. See THIRD-PARTY-NOTICES.txt.
    // Shield Checkmark 16 Regular, blob 437d9dc12cf9f2b43afa51f6be9cdfff6a2036d9.
    private const string ShieldOnPath = "M7.64728 2.14638C7.84268 1.95105 8.15946 1.95123 8.35464 2.14678C9.5947 3.38913 10.9689 4 12.5 4C12.7761 4 13 4.22386 13 4.5V5.59971C12.6832 5.43777 12.3486 5.30564 12 5.20703V4.98121C10.5195 4.86895 9.18521 4.25528 8.00042 3.18917C6.81393 4.25491 5.47941 4.86879 4 4.98118V7.50126C4 8.93206 4.36125 10.061 5.01735 10.9401C5.06968 11.601 5.23884 12.229 5.50418 12.8035C3.84481 11.5888 3 9.81023 3 7.50126V4.5C3 4.22386 3.22386 4 3.5 4C5.02923 4 6.40416 3.38902 7.64728 2.14638ZM15 10.5C15 12.9853 12.9853 15 10.5 15C8.01472 15 6 12.9853 6 10.5C6 8.01472 8.01472 6 10.5 6C12.9853 6 15 8.01472 15 10.5ZM12.8536 8.64645C12.6583 8.45118 12.3417 8.45118 12.1464 8.64645L9.5 11.2929L8.85355 10.6464C8.65829 10.4512 8.34171 10.4512 8.14645 10.6464C7.95118 10.8417 7.95118 11.1583 8.14645 11.3536L9.14645 12.3536C9.34171 12.5488 9.65829 12.5488 9.85355 12.3536L12.8536 9.35355C13.0488 9.15829 13.0488 8.84171 12.8536 8.64645Z";
    // Shield Dismiss 16 Regular, blob 68d15f0e3d8d90e36cd2688a0b3c99e73bf3bb63.
    private const string ShieldOffPath = "M7.64728 2.14638C7.84268 1.95105 8.15946 1.95123 8.35464 2.14677C9.5947 3.38913 10.9689 4 12.5 4C12.7455 4 12.9496 4.17688 12.9919 4.41012L13 4.5V7.50126C13 10.7196 11.3587 12.9075 8.15811 13.9743C8.05548 14.0086 7.94452 14.0086 7.84189 13.9743C4.73825 12.9398 3.1009 10.8512 3.00452 7.79099L3 7.50126V4.5C3 4.22386 3.22386 4 3.5 4C5.02923 4 6.40416 3.38902 7.64728 2.14638ZM8.00042 3.18918C6.87841 4.19701 5.63834 4.78791 4.29048 4.95245L4 4.98117V7.50126C4 10.1375 5.22185 11.9026 7.73537 12.8738L8 12.9715L8.26463 12.8738C10.6915 11.9361 11.9142 10.2583 11.9956 7.77087L12 7.50126V4.98121C10.538 4.87033 9.20057 4.26913 8.00042 3.18918ZM6.09019 6.21559L6.14806 6.14636C6.32167 5.97283 6.5911 5.95362 6.78594 6.08866L6.85517 6.14654L8.001 7.293L9.14806 6.14636C9.32167 5.97283 9.5911 5.95362 9.78594 6.08866L9.85517 6.14654C10.0287 6.32015 10.0479 6.58958 9.91286 6.78441L9.85499 6.85364L8.707 7.999L9.85364 9.14654C10.0272 9.32015 10.0464 9.58958 9.91134 9.78441L9.85346 9.85364C9.67985 10.0272 9.41042 10.0464 9.21559 9.91134L9.14636 9.85346L8 8.707L6.85346 9.85364C6.67985 10.0272 6.41042 10.0464 6.21559 9.91134L6.14636 9.85346C5.97283 9.67985 5.95362 9.41042 6.08866 9.21559L6.14654 9.14636L7.293 8L6.14788 6.85346C5.97436 6.67985 5.95514 6.41042 6.09019 6.21559L6.14806 6.14636L6.09019 6.21559Z";
    private static Viewbox CreateShield(string data, Button button)
    {
        var shape = (Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            $"<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"F1 {data}\" Width=\"16\" Height=\"16\" Stretch=\"None\" />");
        shape.SetBinding(Microsoft.UI.Xaml.Shapes.Shape.FillProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = button, Path = new PropertyPath("Foreground"), Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
        });
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(shape);
        return new Viewbox { Width = 16, Height = 16, Child = canvas, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    }
    public override UIElement CreatePreviewContent()
    {
        var root = new Grid(); _ui = root.DispatcherQueue;
        // The preview surface is a flat, keyboard-accessible open action, not a framed button.
        // Its hit area is separate from the power control, including when power is disabled.
        _open = new Button
        {
            Content = "Happ", FontSize = 12, MinWidth = 0, MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(2, 0, 2, 0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Template = (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Button\">" +
                "<Grid Background=\"Transparent\">" +
                "<ContentPresenter Content=\"{TemplateBinding Content}\" Foreground=\"{TemplateBinding Foreground}\" " +
                "FontSize=\"{TemplateBinding FontSize}\" Padding=\"{TemplateBinding Padding}\" " +
                "HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\" />" +
                "</Grid></ControlTemplate>")
        };
        AutomationProperties.SetName(_open, "Open Happ");
        _power = CompactButton("", "Toggle Happ");
        _shieldOn = CreateShield(ShieldOnPath, _power);
        _shieldOff = CreateShield(ShieldOffPath, _power);
        // A distinct unknown state avoids displaying stopped when no state could be verified.
        _shieldUnknown = new FontIcon { FontFamily = new FontFamily("Segoe MDL2 Assets"), Glyph = "\uE897", FontSize = 16,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _shieldUnknown.SetBinding(IconElement.ForegroundProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = _power, Path = new PropertyPath("Foreground"), Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
        });
        _powerSpinner = new ProgressRing
        {
            Width = 16, Height = 16, IsIndeterminate = true, IsActive = false,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _powerSpinner.SetBinding(Control.ForegroundProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = _power, Path = new PropertyPath("Foreground"), Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
        });
        var powerContent = new Grid { Width = 18, Height = 18 };
        powerContent.Children.Add(_shieldOn);
        powerContent.Children.Add(_shieldOff);
        powerContent.Children.Add(_shieldUnknown);
        powerContent.Children.Add(_powerSpinner);
        _power.Content = powerContent;
        _open.Click += (_,_) => Start("happ://open", false);
        _power.Click += (_,_) =>
        {
            if (_state == CoreState.Unknown) return;
            Start(_state == CoreState.Running ? "happ://disconnect_without_ui" : "happ://connect_without_ui", true);
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(_open);
        // Sibling hit targets: a click on the shield never bubbles through the open button.
        _power.Margin = new Thickness(0, 0, 2, 0);
        Grid.SetColumn(_power, 1);
        root.Children.Add(_power);
        Render();
        if (_monitor == null)
        {
            var token = _lifetime.Token;
            _monitor = Task.Run(() => MonitorAsync(token));
        }
        return root;
    }
    public override UIElement? CreateFlyoutContent() => null;
    private void Start(string uri, bool returnToTray)
    {
        if (_busy || _disposed) return;
        Interlocked.Increment(ref _operationVersion);
        _switching = uri != "happ://open";
        _busy = true; Render();
        _inflight = RunAsync(uri, returnToTray && _settings.ReturnToTray);
    }
    private async Task RunAsync(string uri, bool returnToTray)
    {
        string message;
        var observed = CoreState.Unknown;
        try
        {
            await OperationLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (uri == "happ://open")
                    message = await SendAsync(uri, false, _lifetime.Token).ConfigureAwait(false);
                else
                {
                    var target = uri == "happ://connect_without_ui" ? CoreState.Running : CoreState.Stopped;
                    observed = await Task.Run(ReadCoreState, _lifetime.Token).ConfigureAwait(false);
                    if (observed == CoreState.Unknown)
                        message = "Cannot verify Happ core state. Open Happ to control it manually.";
                    else if (observed == target)
                        message = "Happ core state already changed; no command sent.";
                    else
                    {
                        message = await SendAsync(uri, returnToTray, _lifetime.Token).ConfigureAwait(false);
                        // Observe the requested transition without touching the window again.
                        for (var i = 0; i < 9; i++)
                        {
                            observed = await Task.Run(ReadCoreState, _lifetime.Token).ConfigureAwait(false);
                            if (observed == target) break;
                            if (i < 8) await Task.Delay(1000, _lifetime.Token).ConfigureAwait(false);
                        }
                        message += observed == target ? "\nRequested core state observed." : "\nRequested core state not observed within timeout.";
                    }
                }
            }
            finally { OperationLock.Release(); }
        }
        catch (OperationCanceledException) { message = "Action cancelled; Happ may already have received the command."; }
        catch (Exception ex) { message = "Happ action failed: " + ex.Message; }
        if (!_lifetime.IsCancellationRequested)
            observed = await Task.Run(ReadCoreState).ConfigureAwait(false);
        _ui?.TryEnqueue(() => { if (_disposed) return; Interlocked.Increment(ref _operationVersion); _busy = false; _switching = false; _state = observed; _message = message; Render(); });
    }
    // Only the Xray executable shipped with this Happ installation is counted.
    // An unreadable Xray process is ambiguous, not evidence of disconnection.
    private static CoreState ReadCoreState()
    {
        try
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FlyFrogLLC", "Happ", "core", "xray.exe");
            if (File.Exists(defaultPath)) paths.Add(Path.GetFullPath(defaultPath));
            using var current = Process.GetCurrentProcess();
            var happ = Process.GetProcessesByName("Happ");
            try
            {
                foreach (var p in happ)
                {
                    try
                    {
                        if (p.SessionId != current.SessionId || p.HasExited) continue;
                        var exe = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exe))
                        {
                            var dir = Path.GetDirectoryName(exe);
                            if (dir != null) paths.Add(Path.GetFullPath(Path.Combine(dir, "core", "xray.exe")));
                        }
                    }
                    catch (Exception) { return CoreState.Unknown; }
                }
            }
            finally { foreach (var p in happ) p.Dispose(); }
            if (paths.Count == 0) return CoreState.Unknown;
            var uncertain = false;
            var cores = Process.GetProcessesByName("xray");
            try
            {
                foreach (var p in cores)
                {
                    try
                    {
                        if (p.HasExited) continue;
                        var exe = p.MainModule?.FileName;
                        if (string.IsNullOrEmpty(exe)) { uncertain = true; continue; }
                        if (paths.Contains(Path.GetFullPath(exe)))
                        {
                            // A service-owned core may be in session 0.
                            if (p.SessionId == current.SessionId || p.SessionId == 0) return CoreState.Running;
                            uncertain = true;
                        }
                    }
                    catch (Exception) { uncertain = true; }
                }
            }
            finally { foreach (var p in cores) p.Dispose(); }
            return uncertain ? CoreState.Unknown : CoreState.Stopped;
        }
        catch (Exception) { return CoreState.Unknown; }
    }
    private async Task MonitorAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var version = Volatile.Read(ref _operationVersion);
                var state = ReadCoreState();
                _ui?.TryEnqueue(() => { if (!_disposed && !_busy && version == Volatile.Read(ref _operationVersion) && _state != state) { _state = state; Render(); } });
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    // Fresh process objects avoid cached MainWindowHandle. Observe only the current session.
    private static List<(int Pid, IntPtr Handle, string Title)> VisibleMainWindows()
    {
        using var current = Process.GetCurrentProcess();
        var windows = new List<(int, IntPtr, string)>();
        var processes = Process.GetProcessesByName("Happ");
        try
        {
            foreach (var p in processes)
            {
                try
                {
                    if (p.SessionId != current.SessionId || p.HasExited) continue;
                    p.Refresh();
                    var handle = p.MainWindowHandle;
                    if (handle != IntPtr.Zero) windows.Add((p.Id, handle, p.MainWindowTitle));
                }
                catch (InvalidOperationException) { if (!p.HasExited) throw; }
            }
        }
        finally { foreach (var p in processes) p.Dispose(); }
        return windows;
    }
    private static async Task<string> SendAsync(string uri, bool returnToTray, CancellationToken ct)
    {
        // If visibility cannot be established, do not auto-close anything.
        bool wasHidden = false;
        if (returnToTray)
        {
            try { wasHidden = VisibleMainWindows().Count == 0; }
            catch { wasHidden = false; }
        }
        ct.ThrowIfCancellationRequested();
        using (var launched = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })) { }
        if (!returnToTray || !wasHidden) return "Command sent to Happ. Connection state not verified.";
        // Same two-second delay as the successful manual test. No polling, focus or window restoration.
        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        var windows = VisibleMainWindows();
        if (windows.Count == 0) return "Command sent. No visible Happ window. Connection state not verified.";
        if (windows.Count != 1 || !windows[0].Title.StartsWith("Happ ", StringComparison.OrdinalIgnoreCase))
            return "Command sent; window ambiguous. Close Happ manually if needed.";
        var window = windows[0];
        try
        {
            using var process = Process.GetProcessById(window.Pid);
            process.Refresh();
            if (process.ProcessName.Equals("Happ", StringComparison.OrdinalIgnoreCase) &&
                process.MainWindowHandle == window.Handle && process.MainWindowTitle == window.Title)
            {
                ct.ThrowIfCancellationRequested();
                return process.CloseMainWindow()
                    ? "Command sent; normal close-to-tray requested. Connection state not verified."
                    : "Command sent; close request rejected. Close Happ manually.";
            }
        }
        catch (ArgumentException) { return "Command sent; Happ window disappeared."; }
        return "Command sent; window changed. Close Happ manually if needed.";
    }
    private void Render()
    {
        if (_open != null) { _open.IsEnabled = !_busy; ToolTipService.SetToolTip(_open, "Open Happ\n" + _message); }
        if (_power != null && _shieldOn != null && _shieldOff != null && _shieldUnknown != null && _powerSpinner != null)
        {
            var status = _busy ? "Working" : _state == CoreState.Running ? "Happ core running" : _state == CoreState.Stopped ? "Happ core stopped" : "Happ core state unknown";
            _power.IsEnabled = !_busy && _state != CoreState.Unknown;
            // Same native foreground and opacity in both confirmed states.
            _shieldOn.Visibility = !_switching && _state == CoreState.Running ? Visibility.Visible : Visibility.Collapsed;
            _shieldOff.Visibility = !_switching && _state == CoreState.Stopped ? Visibility.Visible : Visibility.Collapsed;
            _shieldUnknown.Visibility = !_switching && _state == CoreState.Unknown ? Visibility.Visible : Visibility.Collapsed;
            _powerSpinner.Visibility = _switching ? Visibility.Visible : Visibility.Collapsed;
            _powerSpinner.IsActive = _switching;
            AutomationProperties.SetName(_power, _switching ? "Switching Happ connection" : _state == CoreState.Running ? "Disconnect Happ" : _state == CoreState.Stopped ? "Connect Happ" : "Happ status unknown");
            ToolTipService.SetToolTip(_power, status + "\n" + _message);
            if (_open != null) ToolTipService.SetToolTip(_open, "Open Happ\n" + status + "\n" + _message);
        }
    }
    public UIElement CreateSettingsContent(IWidgetSettingsContext context)
    {
        var draft = Settings.Read(context.SettingsJson);
        var slider = new Slider { Minimum = 80, Maximum = 240, StepFrequency = 2, Value = draft.PreviewWidth, Width = 136 };
        AutomationProperties.SetName(slider, "Preview width");
        var width = new TextBlock { Text = $"{draft.PreviewWidth} px", VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { ColumnSpacing = 8 }; grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = "Preview width", VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(slider,1); grid.Children.Add(slider); Grid.SetColumn(width,2); grid.Children.Add(width);
        var tray = new ToggleSwitch { Header = "Return to tray after switching", IsOn = draft.ReturnToTray };
        var hint = new TextBlock { Text = "Uses Happ's URI commands. If Happ was hidden, sends a normal close request after two seconds. The window may briefly appear. Power indicates the local Happ Xray process, not remote connectivity. Shield with checkmark means core running; shield with cross means core stopped. An animated ring means switching. A question mark means unknown.", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        void Save() => context.SaveSettings(JsonSerializer.Serialize(new Settings((int)slider.Value, tray.IsOn)));
        slider.ValueChanged += (_,_) => { width.Text = $"{(int)slider.Value} px"; Save(); }; tray.Toggled += (_,_) => Save();
        return new ScrollViewer { Content = new StackPanel { Spacing = 12, Children = { grid, tray, hint } } };
    }
    public override async ValueTask DisposeAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        try { await Task.WhenAll(_inflight, _monitor ?? Task.CompletedTask); } catch (OperationCanceledException) {}
        _lifetime.Dispose();
        await base.DisposeAsync();
    }
}
internal sealed record Settings(int PreviewWidth = 100, bool ReturnToTray = true)
{
    public static Settings Read(string? json)
    {
        try { var value = JsonSerializer.Deserialize<Settings>(json ?? "{}") ?? new(); return value with { PreviewWidth = Math.Clamp(value.PreviewWidth == 0 ? 100 : value.PreviewWidth, 80, 240) }; }
        catch { return new(); }
    }
}
