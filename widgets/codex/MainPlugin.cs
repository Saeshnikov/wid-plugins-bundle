using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WidBar.SDK;
using Windows.Foundation;
using Windows.Management.Deployment;
using RingPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Personal.Codex.ExtensionApp;

public sealed class MainPlugin : WidgetPluginBase
{
    public override string Id => "com.personal.codex";
    public override string Name => "Codex Limits";
    public override int PreviewLogicalWidth => 108;

    private readonly CancellationTokenSource _lifetime = new();
    private DispatcherQueueTimer? _timer;
    private StackPanel? _preview;
    private Gauge? _first, _second;
    private IReadOnlyList<QuotaWindow> _windows = Array.Empty<QuotaWindow>();
    private DateTimeOffset _nextPollAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _nextReset;
    private Task? _poll;
    private bool _reading;
    private volatile bool _disposed;
    private string _status = "Checking Codex limits…";

    public override UIElement CreatePreviewContent()
    {
        _preview = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        _preview.Children.Add(CreateChatGptIcon());
        _first = new Gauge();
        _second = new Gauge();
        _preview.Children.Add(_first.Root);
        _preview.Children.Add(_second.Root);
        RenderSnapshot();
        if (!_disposed && _timer is null)
        {
            _timer = _preview.DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Tick;
            _timer.Start();
            Tick(_timer, null);
        }
        return _preview;
    }

    private static Viewbox CreateChatGptIcon()
    {
        // OpenAI Blossom, OpenAI-black-monoblossom.svg from the official OpenAI brand kit.
        const string data = """
            M304.246 294.611V249.028C304.246 245.189 305.687 242.309 309.044 240.392L400.692 187.612C413.167 180.415 428.042 177.058 443.394 177.058C500.971 177.058 537.44 221.682 537.44 269.182C537.44 272.54 537.44 276.379 536.959 280.218L441.954 224.558C436.197 221.201 430.437 221.201 424.68 224.558L304.246 294.611Z
            M518.245 472.145V363.224C518.245 356.505 515.364 351.707 509.608 348.349L389.174 278.296L428.519 255.743C431.877 253.826 434.757 253.826 438.115 255.743L529.762 308.523C556.154 323.879 573.905 356.505 573.905 388.171C573.905 424.636 552.315 458.225 518.245 472.141V472.145Z
            M275.937 376.182L236.592 353.152C233.235 351.235 231.794 348.354 231.794 344.515V238.956C231.794 187.617 271.139 148.749 324.4 148.749C344.555 148.749 363.264 155.468 379.102 167.463L284.578 222.164C278.822 225.521 275.942 230.319 275.942 237.039V376.186L275.937 376.182Z
            M360.626 425.122L304.246 393.455V326.283L360.626 294.616L417.002 326.283V393.455L360.626 425.122Z
            M396.852 570.989C376.698 570.989 357.989 564.27 342.151 552.276L436.674 497.574C442.431 494.217 445.311 489.419 445.311 482.699V343.552L485.138 366.582C488.495 368.499 489.936 371.379 489.936 375.219V480.778C489.936 532.117 450.109 570.985 396.852 570.985V570.989Z
            M283.134 463.99L191.486 411.211C165.094 395.854 147.343 363.229 147.343 331.562C147.343 294.616 169.415 261.509 203.48 247.593V356.991C203.48 363.71 206.361 368.508 212.117 371.866L332.074 441.437L292.729 463.99C289.372 465.907 286.491 465.907 283.134 463.99Z
            M277.859 542.68C223.639 542.68 183.813 501.895 183.813 451.514C183.813 447.675 184.294 443.836 184.771 439.997L279.295 494.698C285.051 498.056 290.812 498.056 296.568 494.698L417.002 425.127V470.71C417.002 474.549 415.562 477.429 412.204 479.346L320.557 532.126C308.081 539.323 293.206 542.68 277.854 542.68H277.859Z
            M396.852 599.776C454.911 599.776 503.37 558.513 514.41 503.812C568.149 489.896 602.696 439.515 602.696 388.176C602.696 354.587 588.303 321.962 562.392 298.45C564.791 288.373 566.231 278.296 566.231 268.224C566.231 199.611 510.571 148.267 446.274 148.267C433.322 148.267 420.846 150.184 408.37 154.505C386.775 133.392 357.026 119.958 324.4 119.958C266.342 119.958 217.883 161.22 206.843 215.921C153.104 229.837 118.557 280.218 118.557 331.557C118.557 365.146 132.95 397.771 158.861 421.283C156.462 431.36 155.022 441.437 155.022 451.51C155.022 520.123 210.682 571.466 274.978 571.466C287.931 571.466 300.407 569.549 312.883 565.228C334.473 586.341 364.222 599.776 396.852 599.776Z
            """;
        var path = (RingPath)XamlReader.Load(
            $$"""<Path xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Data="F1 {{data}}" Stretch="None" Fill="{ThemeResource TextFillColorPrimaryBrush}"/>""");
        var canvas = new Canvas { Width = 721, Height = 721, Children = { path } };
        var icon = new Viewbox { Width = 24, Height = 24, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(icon, "ChatGPT");
        return icon;
    }

    private void Tick(DispatcherQueueTimer sender, object? args)
    {
        if (_disposed) return;
        var now = DateTimeOffset.UtcNow;
        UpdateRings(now);
        if (_reading || (now < _nextPollAt && !(_nextReset is { } reset && now >= reset))) return;

        _reading = true;
        _nextPollAt = now.AddMinutes(1);
        var dispatcher = _preview!.DispatcherQueue;
        var token = _lifetime.Token;
        _poll = Task.Run(async () =>
        {
            IReadOnlyList<QuotaWindow> windows = Array.Empty<QuotaWindow>();
            string status;
            try
            {
                var executable = FindCodex();
                if (executable is null)
                    status = "Codex not found. Install Codex Desktop or put codex.exe on PATH.";
                else
                {
                    windows = await CodexUsage.ReadAsync(executable, token).ConfigureAwait(false);
                    status = windows.Count > 0 ? "Codex quota remaining" : "No Codex quota data. Check your ChatGPT sign-in in Codex.";
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException
                or UnauthorizedAccessException or TimeoutException or JsonException)
            {
                status = "Could not read Codex limits. Check your connection and sign-in in Codex.";
            }

            dispatcher.TryEnqueue(() =>
            {
                if (_disposed) return;
                _reading = false;
                _nextReset = CodexUsage.NextResetRefresh(_windows, windows, now, DateTimeOffset.UtcNow);
                _windows = windows;
                _status = status;
                RenderSnapshot();
            });
        });
    }

    private static string? FindCodex()
    {
        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser("", "OpenAI.Codex_2p2nqsd0c76g0"))
            {
                var executable = System.IO.Path.Combine(package.InstalledLocation.Path, "app", "resources", "codex.exe");
                if (File.Exists(executable)) return executable;
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { }

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (!System.IO.Path.IsPathFullyQualified(directory)) continue;
            var executable = System.IO.Path.Combine(directory, "codex.exe");
            if (File.Exists(executable)) return executable;
        }
        return null;
    }

    private void RenderSnapshot()
    {
        ToolTipService.SetToolTip(_preview!, _status);
        _first!.SetWindow(_windows.Count > 0 ? _windows[0] : null);
        _second!.Root.Visibility = _windows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _second.SetWindow(_windows.Count > 1 ? _windows[1] : null);
        UpdateRings(DateTimeOffset.UtcNow);
    }

    private void UpdateRings(DateTimeOffset now)
    {
        _first?.UpdateRing(now);
        _second?.UpdateRing(now);
    }

    public override async ValueTask DisposeAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        if (_timer is { } timer && _preview is { } preview)
        {
            void StopTimer()
            {
                timer.Stop();
                timer.Tick -= Tick;
            }
            if (preview.DispatcherQueue.HasThreadAccess) StopTimer();
            else preview.DispatcherQueue.TryEnqueue(StopTimer);
        }
        if (_poll is { } poll) await poll;
        _lifetime.Dispose();
        await base.DisposeAsync();
    }

    private sealed class Gauge
    {
        private const double Size = 34, Center = Size / 2, Radius = 15.25, Stroke = 1.5;
        public Grid Root { get; } = new() { Width = Size, Height = Size, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _digits = new()
        {
            FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2),
        };
        private readonly Ellipse _track = new() { Width = 2 * Radius + Stroke, Height = 2 * Radius + Stroke, StrokeThickness = Stroke, Opacity = 0.24 };
        private readonly Ellipse _full = new() { Width = 2 * Radius + Stroke, Height = 2 * Radius + Stroke, StrokeThickness = Stroke };
        private readonly RingPath _arc = new() { StrokeThickness = Stroke, Stretch = Stretch.None, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly PathFigure _figure = new() { StartPoint = new Point(Center, Center - Radius), IsFilled = false };
        private readonly ArcSegment _segment = new() { Size = new Size(Radius, Radius), SweepDirection = SweepDirection.Clockwise };
        private QuotaWindow? _window;

        public Gauge()
        {
            _track.HorizontalAlignment = _full.HorizontalAlignment = HorizontalAlignment.Center;
            _track.VerticalAlignment = _full.VerticalAlignment = VerticalAlignment.Center;
            foreach (Shape shape in new Shape[] { _track, _full, _arc })
            {
                shape.SetBinding(Shape.StrokeProperty, new Binding { Source = _digits, Path = new PropertyPath("Foreground") });
                shape.IsHitTestVisible = false;
                Root.Children.Add(shape);
            }
            _figure.Segments.Add(_segment);
            _arc.Data = new PathGeometry { Figures = { _figure } };
            Root.Children.Add(_digits);
        }

        public void SetWindow(QuotaWindow? window)
        {
            _window = window;
            _digits.Text = window is null ? "—" : $"{window.RemainingPercent}";
            var name = window?.WindowMinutes switch
            {
                300 => "5-hour Codex limit",
                10080 => "Weekly Codex limit",
                int minutes => $"Codex limit ({minutes}-minute window)",
                _ => "Codex limit",
            };
            var description = window is null ? "Codex limits unavailable" : $"{name}: {window.RemainingPercent}% remaining";
            AutomationProperties.SetName(Root, description);
            ToolTipService.SetToolTip(Root, window is null ? null
                : description + (window.RemainingTimeFraction(DateTimeOffset.UtcNow) is > 0
                    ? ". Ring shows time left until reset." : ". Next reset time unavailable."));
        }

        public void UpdateRing(DateTimeOffset now)
        {
            var fraction = _window?.RemainingTimeFraction(now);
            _track.Visibility = fraction is > 0 ? Visibility.Visible : Visibility.Collapsed;
            _full.Visibility = fraction is >= 1 ? Visibility.Visible : Visibility.Collapsed;
            _arc.Visibility = fraction is > 0 and < 1 ? Visibility.Visible : Visibility.Collapsed;
            if (fraction is > 0 and < 1)
            {
                var radians = 2 * Math.PI * fraction.Value;
                _segment.Point = new Point(Center + Radius * Math.Sin(radians), Center - Radius * Math.Cos(radians));
                _segment.IsLargeArc = fraction > 0.5;
            }
        }
    }
}
