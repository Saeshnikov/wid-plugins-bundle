/*
Third-party icon path data: GitHub Octicons and Microsoft Fluent UI System Icons.
MIT License

Copyright (c) 2026 GitHub Inc.
Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Markup;
using WidBar.SDK;

namespace Personal.GitHub.ExtensionApp;

public sealed class MainPlugin : WidgetPluginBase, IConfigurableWidgetPlugin
{
    public override string Id => "com.personal.github";
    public override string Name => "GitHub Activity";
    public override int PreviewLogicalWidth => _settings.PreviewWidth;
    public override int FlyoutWidth => 400;
    public override int FlyoutHeight => 300;

    private readonly CancellationTokenSource _lifetime = new();
    private DispatcherTimer? _timer;
    private StackPanel? _preview;
    private TextBlock? _mineCount, _reviewCount, _failedCount, _previewState, _status;
    private StackPanel? _failedPreview;
    private readonly Button[] _tabs = new Button[3];
    private readonly Border[] _tabIndicators = new Border[3];
    private Viewbox? _reviewIcon, _failedIcon;
    private Style? _cautionText, _criticalText, _cautionIcon, _criticalIcon, _neutralIcon;
    private readonly TextBlock[] _tabCounts = new TextBlock[3];
    private int _selectedCategory;
    private StackPanel? _items;
    private Button? _refresh;
    private Snapshot? _snapshot;
    private Settings? _lastSettings;
    private Settings _settings = new();
    // Presentation-only settings must never invalidate GitHub results or polling.
    private Settings DataSettings => _settings with { PreviewWidth = Settings.DefaultPreviewWidth };
    private string? _observedContextJson;
    private Microsoft.UI.Dispatching.DispatcherQueue? _ui;
    private bool _refreshError;
    private string _message = "Waiting for first refresh";
    private bool _busy;
    private volatile bool _disposed;
    private DateTimeOffset _next = DateTimeOffset.MinValue;
    private Task _inflight = Task.CompletedTask;

    public override async Task InitializeAsync(IWidgetContext context)
    {
        _observedContextJson = context.SettingsJson;
        _settings = Settings.Read(_observedContextJson);
        await base.InitializeAsync(context);
    }

    // The SDK passes the new draft here, including the old settings on Cancel.
    // Context.SettingsJson alone is not a reliable live-settings notification.
    public override void OnSettingsDraftChanged(string json)
    {
        var queue = _ui;
        if (queue != null && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => OnSettingsDraftChanged(json));
            return;
        }
        if (_disposed) return;
        var next = Settings.Read(json);
        if (next == _settings) return;
        var previous = _settings;
        _settings = next;
        if (DataSettings != (previous with { PreviewWidth = Settings.DefaultPreviewWidth }))
        {
            _snapshot = null;
            _refreshError = false;
            _message = next.Demo ? "Loading demo…" : "Connecting to GitHub…";
            _next = DateTimeOffset.MinValue;
            Render(); // The existing timer coalesces edits before the next request.
        }
        // The host owns the taskbar slot. Also reapply on Cancel (old JSON).
        if (next.PreviewWidth != previous.PreviewWidth)
            Context?.RequestPreviewRefresh();
    }

    private Task OnUiAsync(Action action)
    {
        var queue = _ui;
        if (queue == null || queue.HasThreadAccess) { action(); return Task.CompletedTask; }
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() => {
            try { action(); done.TrySetResult(true); }
            catch (Exception ex) { done.TrySetException(ex); }
        })) done.TrySetResult(false);
        return done.Task;
    }

    // Upstream SVG path data, not custom-drawn icons. See THIRD-PARTY-NOTICES.txt.
    // primer/octicons/icons/git-pull-request-16.svg (blob a1cb1ef9e5623189ddd0fd16374b4fa2e7eb9737)
    private const string PullRequestPath = "M1.5 3.25a2.25 2.25 0 1 1 3 2.122v5.256a2.251 2.251 0 1 1-1.5 0V5.372A2.25 2.25 0 0 1 1.5 3.25Zm5.677-.177L9.573.677A.25.25 0 0 1 10 .854V2.5h1A2.5 2.5 0 0 1 13.5 5v5.628a2.251 2.251 0 1 1-1.5 0V5a1 1 0 0 0-1-1h-1v1.646a.25.25 0 0 1-.427.177L7.177 3.427a.25.25 0 0 1 0-.354ZM3.75 2.5a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Zm0 9.5a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Zm8.25.75a.75.75 0 1 0 1.5 0 .75.75 0 0 0-1.5 0Z";
    // primer/octicons/icons/person-16.svg (blob a58e1860b31916b85fe1168797b27ef401257b21)
    private const string ReviewPath = "M10.561 8.073a6.005 6.005 0 0 1 3.432 5.142.75.75 0 1 1-1.498.07 4.5 4.5 0 0 0-8.99 0 .75.75 0 0 1-1.498-.07 6.004 6.004 0 0 1 3.431-5.142 3.999 3.999 0 1 1 5.123 0ZM10.5 5a2.5 2.5 0 1 0-5 0 2.5 2.5 0 0 0 5 0Z";
    // primer/octicons/icons/x-circle-16.svg (blob 837bf4faab18b8ddb2a7feeca39cad24e1b53f76)
    private const string FailedPath = "M2.344 2.343h-.001a8 8 0 0 1 11.314 11.314A8.002 8.002 0 0 1 .234 10.089a8 8 0 0 1 2.11-7.746Zm1.06 10.253a6.5 6.5 0 1 0 9.108-9.275 6.5 6.5 0 0 0-9.108 9.275ZM6.03 4.97 8 6.94l1.97-1.97a.749.749 0 0 1 1.275.326.749.749 0 0 1-.215.734L9.06 8l1.97 1.97a.749.749 0 0 1-.326 1.275.749.749 0 0 1-.734-.215L8 9.06l-1.97 1.97a.749.749 0 0 1-1.275-.326.749.749 0 0 1 .215-.734L6.94 8 4.97 6.03a.751.751 0 0 1 .018-1.042.751.751 0 0 1 1.042-.018Z";
    // primer/octicons/icons/mark-github-16.svg (blob 0e55a8ef2db1913cb35f81ae09ffdb58b5422319)
    private const string GitHubPath = "M6.766 11.328c-2.063-.25-3.516-1.734-3.516-3.656 0-.781.281-1.625.75-2.188-.203-.515-.172-1.609.063-2.062.625-.078 1.468.25 1.968.703.594-.187 1.219-.281 1.985-.281.765 0 1.39.094 1.953.265.484-.437 1.344-.765 1.969-.687.218.422.25 1.515.046 2.047.5.593.766 1.39.766 2.203 0 1.922-1.453 3.375-3.547 3.64.531.344.89 1.094.89 1.954v1.625c0 .468.391.734.86.547C13.781 14.359 16 11.53 16 8.03 16 3.61 12.406 0 7.984 0 3.563 0 0 3.61 0 8.031a7.88 7.88 0 0 0 5.172 7.422c.422.156.828-.125.828-.547v-1.25c-.219.094-.5.156-.75.156-1.031 0-1.64-.562-2.078-1.609-.172-.422-.36-.672-.719-.719-.187-.015-.25-.093-.25-.187 0-.188.313-.328.625-.328.453 0 .844.281 1.25.86.313.452.64.655 1.031.655s.641-.14 1-.5c.266-.265.47-.5.657-.656";
    // microsoft/fluentui-system-icons/assets/Arrow Clockwise/SVG/ic_fluent_arrow_clockwise_16_regular.svg (blob 98676eff95282e341c4d1a6002f5a33a047b97b5)
    private const string RefreshPath = "M3 8C3 5.23858 5.23858 3 8 3C9.63527 3 11.0878 3.78495 12.0005 5H10C9.72386 5 9.5 5.22386 9.5 5.5C9.5 5.77614 9.72386 6 10 6H12.8904C12.8973 6.00014 12.9041 6.00014 12.911 6H13C13.2761 6 13.5 5.77614 13.5 5.5V2.5C13.5 2.22386 13.2761 2 13 2C12.7239 2 12.5 2.22386 12.5 2.5V4.03138C11.4009 2.78613 9.79253 2 8 2C4.68629 2 2 4.68629 2 8C2 11.3137 4.68629 14 8 14C11.1301 14 13.6999 11.6035 13.9756 8.54488C14.0003 8.26985 13.7975 8.0268 13.5225 8.00202C13.2474 7.97723 13.0044 8.1801 12.9796 8.45512C12.75 11.003 10.6079 13 8 13C5.23858 13 3 10.7614 3 8Z";
    // microsoft/fluentui-system-icons/assets/Open/SVG/ic_fluent_open_16_regular.svg (blob 4b7f3a64a2e2d19b4acac8aff0ac1ed57745ef26)
    private const string ExternalPath = "M4.49999 3C3.67157 3 3 3.67157 3 4.5V11.5C3 12.3284 3.67157 13 4.49999 13H11.5C12.3284 13 12.9999 12.3284 12.9999 11.5V9.26923C12.9999 8.99309 13.2238 8.76923 13.4999 8.76923C13.7761 8.76923 13.9999 8.99309 13.9999 9.26923V11.5C13.9999 12.8807 12.8807 14 11.5 14H4.49999C3.11928 14 2 12.8807 2 11.5V4.5C2 3.11929 3.11928 2 4.49999 2H6.73075C7.00689 2 7.23074 2.22386 7.23074 2.5C7.23074 2.77614 7.00689 3 6.73075 3H4.49999ZM8.76926 2.5C8.76926 2.22386 8.99311 2 9.26925 2H13.5C13.7761 2 14 2.22386 14 2.5V6.73077C14 7.00691 13.7761 7.23077 13.5 7.23077C13.2239 7.23077 13 7.00691 13 6.73077V3.70711L9.6228 7.08433C9.42754 7.27959 9.11096 7.27959 8.9157 7.08433C8.72044 6.88906 8.72044 6.57248 8.9157 6.37722L12.2929 3H9.26925C8.99311 3 8.76926 2.77614 8.76926 2.5Z";

    private Viewbox OfficialIcon(string path, double size = 16)
    {
        // Keep the upstream SVG viewBox (0 0 16 16). PathIcon can crop
        // geometry when its requested size is smaller than the source.
        var shape = (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load($"<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"F1 {path}\" Width=\"16\" Height=\"16\" Stretch=\"None\" />");
        shape.Style = _neutralIcon;
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(shape);
        return new Viewbox { Width = size, Height = size, Child = canvas,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false };
    }

    private static Style ToneStyle(string targetType, string brush, string property = "Foreground") =>
        (Style)XamlReader.Load($"<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"{targetType}\"><Setter Property=\"{property}\" Value=\"{{ThemeResource {brush}}}\"/></Style>");

    private void EnsureToneStyles()
    {
        _cautionText ??= ToneStyle("TextBlock", "SystemFillColorCautionBrush");
        _criticalText ??= ToneStyle("TextBlock", "SystemFillColorCriticalBrush");
        _neutralIcon ??= ToneStyle("Path", "TextFillColorPrimaryBrush", "Fill");
        _cautionIcon ??= ToneStyle("Path", "SystemFillColorCautionBrush", "Fill");
        _criticalIcon ??= ToneStyle("Path", "SystemFillColorCriticalBrush", "Fill");
    }

    private void SetTone(FrameworkElement element, Style? style)
    {
        if (element is Viewbox { Child: Canvas canvas } && canvas.Children[0] is Microsoft.UI.Xaml.Shapes.Path shape)
        {
            if (shape.Style != (style ?? _neutralIcon)) shape.Style = style ?? _neutralIcon;
        }
        else if (style == null) element.ClearValue(FrameworkElement.StyleProperty);
        else if (element.Style != style) element.Style = style;
    }

    private static Border SelectionIndicator()
    {
        // A neutral indicator, not the user's saturated AccentColor.
        return (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Height=\"2\" Width=\"22\" CornerRadius=\"1\" HorizontalAlignment=\"Center\" Background=\"{ThemeResource TextFillColorPrimaryBrush}\" Opacity=\"0\" />");
    }

    private static TextBlock CountText() => new() { Text = "–", FontSize = 13,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Metric(UIElement icon, TextBlock count, string label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(icon); panel.Children.Add(count);
        AutomationProperties.SetName(panel, label);
        return panel;
    }

    public override UIElement CreatePreviewContent()
    {
        EnsureToneStyles();
        var root = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        _ui = root.DispatcherQueue;
        _preview = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        // Keep icons and text at their natural size unless the slot is too narrow.
        // Uniform downscaling prevents clipping when all three counters are visible.
        root.Children.Add(new Viewbox
        {
            Child = _preview,
            Margin = new Thickness(4, 0, 4, 0),
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        _mineCount = CountText(); _reviewCount = CountText(); _failedCount = CountText();
        _reviewIcon = OfficialIcon(ReviewPath, 16); _failedIcon = OfficialIcon(FailedPath, 16);
        _preview.Children.Add(Metric(OfficialIcon(PullRequestPath, 16), _mineCount, "My pull requests"));
        _preview.Children.Add(Metric(_reviewIcon, _reviewCount, "Review requests"));
        _failedPreview = Metric(_failedIcon, _failedCount, "Failed commit status");
        _preview.Children.Add(_failedPreview);
        _previewState = new TextBlock { FontSize = 10, Opacity = .7, VerticalAlignment = VerticalAlignment.Center };
        _preview.Children.Add(_previewState);
        Render(); StartTimer(); return root;
    }

    public override UIElement CreateFlyoutContent()
    {
        EnsureToneStyles();
        var root = new Grid { Padding = new Thickness(14, 10, 14, 10), RowSpacing = 8 };
        _ui ??= root.DispatcherQueue;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // WidBar overlays its settings affordance in the top-right corner.
        // Reserve that region and put our refresh action in the footer.
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Margin = new Thickness(0, 0, 40, 0) };
        header.Children.Add(OfficialIcon(GitHubPath, 18));
        header.Children.Add(new TextBlock { Text = "Pull requests", FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(header);

        var tabs = new Grid { ColumnSpacing = 6 };
        var labels = new[] { "My PRs", "To review", "Failed" };
        for (int i = 0; i < labels.Length; i++)
        {
            tabs.ColumnDefinitions.Add(new ColumnDefinition());
            var count = new TextBlock { Text = "–", FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            _tabCounts[i] = count;
            var content = new StackPanel { Spacing = 1 };
            content.Children.Add(count);
            content.Children.Add(new TextBlock { Text = labels[i], FontSize = 12, Opacity = .8 });
            var indicator = SelectionIndicator();
            indicator.Margin = new Thickness(0, 2, 0, 0);
            _tabIndicators[i] = indicator;
            content.Children.Add(indicator);
            // Native Button retains hover, focus and keyboard activation without
            // the accent-filled checked state of the default ToggleButton.
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 4, 10, 3) };
            _tabs[i] = button;
            AutomationProperties.SetName(button, labels[i]);
            var index = i;
            button.Click += (_, _) => { _selectedCategory = index; Render(); };
            Grid.SetColumn(button, i); tabs.Children.Add(button);
        }
        Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        _items = new StackPanel { Spacing = 2 };
        var scroll = new ScrollViewer { Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);

        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .72, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        footer.Children.Add(_status);
        _refresh = new Button { Content = OfficialIcon(RefreshPath), Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_refresh, "Refresh"); ToolTipService.SetToolTip(_refresh, "Refresh GitHub data");
        _refresh.Click += (_, _) => { if (!_busy && !_disposed) _inflight = RefreshAsync(); };
        Grid.SetColumn(_refresh, 1); footer.Children.Add(_refresh);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        Render(); StartTimer(); return root;
    }

    private void StartTimer()
    {
        if (_timer != null || _disposed) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += Tick;
        _timer.Start();
        Tick(null, null);
    }

    private void Tick(object? sender, object? args)
    {
        if (_disposed) return;
        // Also observe saved context changes, but do not overwrite an active
        // draft with the same old context value on every timer tick.
        var json = Context?.SettingsJson;
        if (json != _observedContextJson)
        {
            _observedContextJson = json;
            OnSettingsDraftChanged(json ?? "{}");
        }
        if (_busy) return;
        var current = DataSettings;
        if (_lastSettings != current || DateTimeOffset.UtcNow >= _next)
            _inflight = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_disposed || _busy) return;
        _busy = true;
        var settings = DataSettings;
        if (_lastSettings != settings) { _snapshot = null; _lastSettings = settings; }
        _refreshError = false;
        _message = settings.Demo ? "Loading demo…" : "Connecting to GitHub…";
        Render();
        Snapshot? result = null;
        string? error = null;
        try
        {
            result = settings.Demo ? Snapshot.Demo() : await GitHubClient.FetchAsync(settings, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            error = ex is WidgetException ? ex.Message : "Unexpected refresh error (" + ex.GetType().Name + ").";
        }
        finally
        {
            await OnUiAsync(() => {
                _busy = false;
                if (_disposed) return;
                if (DataSettings != settings) { _next = DateTimeOffset.MinValue; Render(); return; }
                if (result != null)
                {
                    _snapshot = result;
                    _message = result.IsDemo ? "DEMO · sample data, no network" : $"@{result.Login} · updated {DateTime.Now:HH:mm}";
                    var repoCount = Settings.SplitRepositories(settings.Repositories).Length;
                    if (!result.IsDemo && repoCount > 0) _message += $" · {repoCount} repos";
                    if (result.Incomplete) _message += " · partial data";
                    _next = DateTimeOffset.UtcNow.AddSeconds(settings.IntervalSeconds);
                }
                else if (error != null)
                {
                    _refreshError = true;
                    _message = error;
                    if (_snapshot != null) _message += $" · stale since {_snapshot.Updated.LocalDateTime:HH:mm}";
                    _next = DateTimeOffset.UtcNow.AddSeconds(Math.Max(120, settings.IntervalSeconds));
                }
                Render();
            });
        }
    }

    private static string FullCount(Group? group) => group is { Error: null } ? group.Total.ToString() : "–";
    private static string ShortCount(Group? group) => group is { Error: null, Total: > 99 } ? "99+" : FullCount(group);

    private void Render()
    {
        var s = _snapshot;
        if (_preview != null)
        {
            _mineCount!.Text = ShortCount(s?.Mine);
            _reviewCount!.Text = ShortCount(s?.Review);
            _failedCount!.Text = ShortCount(s?.Failed);
            _failedPreview!.Visibility = s?.Failed.Total > 0 || s?.Failed.Error != null ? Visibility.Visible : Visibility.Collapsed;
            SetTone(_reviewCount!, s?.Review.Total > 0 ? _cautionText : null);
            SetTone(_reviewIcon!, s?.Review.Total > 0 ? _cautionIcon : null);
            _reviewIcon!.Opacity = s?.Review.Total > 0 ? .78 : 1;
            _reviewCount!.Opacity = s?.Review.Total > 0 ? .82 : 1;
            SetTone(_failedCount!, s?.Failed.Total > 0 ? _criticalText : null);
            SetTone(_failedIcon!, s?.Failed.Total > 0 ? _criticalIcon : null);
            var state = "";
            if (_busy) state += "…";
            else if (s == null || _refreshError || s.Incomplete) state += "!";
            _previewState!.Text = state;
            _previewState.Visibility = state.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(_preview, $"My PRs: {FullCount(s?.Mine)} · Review: {FullCount(s?.Review)} · Failed: {FullCount(s?.Failed)}\n{_message}");
        }
        if (_status != null) { _status.Text = _message; ToolTipService.SetToolTip(_status, _message); }
        if (_refresh != null) _refresh.IsEnabled = !_busy;
        if (_items == null) return;
        var groups = new Group?[] { s?.Mine, s?.Review, s?.Failed };
        for (int i = 0; i < _tabs.Length; i++)
        {
            if (_tabs[i] == null) continue;
            _tabIndicators[i].Opacity = i == _selectedCategory ? 1 : 0;
            _tabCounts[i].Text = FullCount(groups[i]);
            SetTone(_tabCounts[i], groups[i]?.Total > 0 ? (i == 1 ? _cautionText : i == 2 ? _criticalText : null) : null);
            _tabCounts[i].Opacity = i == 1 && groups[i]?.Total > 0 ? .82 : 1;
            var categoryLabel = i == 0 ? "My PRs" : i == 1 ? "To review" : "Failed";
            AutomationProperties.SetName(_tabs[i], $"{categoryLabel}: {_tabCounts[i].Text}{(i == _selectedCategory ? ", selected" : "")}");
        }
        _items.Children.Clear();
        if (s == null)
        {
            var title = new TextBlock { Text = _busy ? "Loading pull requests…" : _refreshError ? "GitHub unavailable" : "Waiting for GitHub", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14 };
            _items.Children.Add(title);
            _items.Children.Add(new TextBlock { Text = _busy ? "Checking account and open pull requests." : _refreshError ? _message : "Check connection in widget settings.", TextWrapping = TextWrapping.Wrap, Opacity = .85, FontSize = 12 });
            return;
        }
        var group = _selectedCategory switch { 1 => s.Review, 2 => s.Failed, _ => s.Mine };
        if (group.Error != null)
        {
            _items.Children.Add(new TextBlock { Text = "This category is unavailable", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14 });
            _items.Children.Add(new TextBlock { Text = group.Error, TextWrapping = TextWrapping.Wrap, Opacity = .85, FontSize = 12 });
            return;
        }
        if (group.Entries.Length == 0)
            _items.Children.Add(new TextBlock { Text = _selectedCategory switch { 1 => "No direct review requests", 2 => "No PRs with a failed commit status", _ => "No open pull requests authored by you" }, Opacity = .75, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        foreach (var pr in group.Entries)
        {
            bool failed = s.Failed.Entries.Any(p => string.Equals(p.Repository, pr.Repository, StringComparison.OrdinalIgnoreCase) && p.Number == pr.Number);
            var row = new Grid { ColumnSpacing = 9, Padding = new Thickness(7, 5, 7, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            var rowIcon = OfficialIcon(failed ? FailedPath : _selectedCategory == 1 ? ReviewPath : PullRequestPath);
            SetTone(rowIcon, failed ? _criticalIcon : _selectedCategory == 1 ? _cautionIcon : null);
            row.Children.Add(rowIcon);
            var details = new StackPanel { Spacing = 2 };
            var prTitle = new TextBlock { Text = pr.Title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(prTitle, pr.Title);
            details.Children.Add(prTitle);
            var metadata = new TextBlock { Text = $"{pr.Repository} · #{pr.Number}{(failed ? " · Failed status" : "")}",
                FontSize = 11, Opacity = failed ? 1 : .72, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            if (failed) SetTone(metadata, _criticalText);
            ToolTipService.SetToolTip(metadata, metadata.Text);
            details.Children.Add(metadata);
            Grid.SetColumn(details, 1); row.Children.Add(details);
            if (!s.IsDemo && Uri.TryCreate(pr.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com")
            {
                var external = OfficialIcon(ExternalPath, 16); external.Opacity = .72;
                Grid.SetColumn(external, 2); row.Children.Add(external);
                // Native button gives the whole row hover/focus/pressed feedback.
                var link = new Button { Content = row, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                link.Click += async (_, _) => {
                    try { await Windows.System.Launcher.LaunchUriAsync(uri); }
                    catch (Exception) { /* Browser launch failure must not crash the widget. */ }
                };
                AutomationProperties.SetName(link, $"Open {pr.Title} on GitHub");
                _items.Children.Add(link);
            }
            else _items.Children.Add(row);
        }
        if (group.Total > group.Entries.Length)
            _items.Children.Add(new TextBlock { Text = $"Showing {group.Entries.Length} of {group.Total}", Opacity = .7, FontSize = 12 });
    }

    public UIElement CreateSettingsContent(IWidgetSettingsContext context)
    {
        var draft = Settings.Read(context.SettingsJson);
        // One native settings row: label on the left, slider and value on the right.
        var previewWidth = new Slider
        {
            Minimum = Settings.MinPreviewWidth, Maximum = Settings.MaxPreviewWidth,
            StepFrequency = 2, Value = draft.PreviewWidth,
            Width = 136, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(previewWidth, "Preview width");
        var widthValue = new TextBlock { Text = $"{draft.PreviewWidth} px", FontSize = 12,
            Opacity = .8, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Width = 48 };
        var widthGrid = new Grid { ColumnSpacing = 8, Padding = new Thickness(12, 8, 12, 8) };
        widthGrid.ColumnDefinitions.Add(new ColumnDefinition());
        widthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        widthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        widthGrid.Children.Add(new TextBlock { Text = "Preview width", TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(previewWidth, 1); widthGrid.Children.Add(previewWidth);
        Grid.SetColumn(widthValue, 2); widthGrid.Children.Add(widthValue);
        var widthCard = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" CornerRadius=\"4\" Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\" BorderBrush=\"{ThemeResource CardStrokeColorDefaultBrush}\" BorderThickness=\"1\" />");
        widthCard.Child = widthGrid;
        var demo = new ToggleSwitch { Header = "Demo mode (no network)", IsOn = draft.Demo };
        var org = new TextBox { Header = "GitHub organization (optional)", Text = draft.Organization, PlaceholderText = "my-org" };
        var path = new TextBox { Header = "Path to gh.exe", Text = draft.GhPath, PlaceholderText = Settings.DefaultGhPath };
        var repositories = new TextBox { Header = "Tracked repositories (owner/name)", Text = draft.Repositories,
            PlaceholderText = "owner/repo, owner/another-repo", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 65 };
        var repoHint = new TextBlock { Text = "Empty = all accessible PRs (or organization filter). A nonempty list takes precedence over organization. Use commas or new lines.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .8 };
        var loadRepos = new Button { Content = "Load my repositories" };
        var clearRepos = new Button { Content = "Track all" };
        var searchRepos = new TextBox { PlaceholderText = "Search repositories", IsEnabled = false };
        var repoStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .8 };
        var repoRows = new StackPanel { Spacing = 2 };
        var repoScroll = new ScrollViewer { Content = repoRows, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        string[] available = Array.Empty<string>();
        bool syncingPicker = false;
        void RenderRepositories()
        {
            if (syncingPicker) return;
            syncingPicker = true;
            try
            {
                repoRows.Children.Clear();
                var selected = Settings.SplitRepositories(repositories.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var filtered = available.Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(r => r.Contains(searchRepos.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => selected.Contains(r)).ThenBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray();
                repoScroll.Visibility = available.Length > 0 || selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                foreach (var name in filtered.Take(100))
                {
                    var check = new CheckBox { Content = name, IsChecked = selected.Contains(name) };
                    ToolTipService.SetToolTip(check, name);
                    void Change(bool isChecked)
                    {
                        if (syncingPicker) return;
                        var names = Settings.SplitRepositories(repositories.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (isChecked) names.Add(name); else names.Remove(name);
                        syncingPicker = true;
                        try { repositories.Text = string.Join(", ", names.OrderBy(r => r, StringComparer.OrdinalIgnoreCase)); }
                        finally { syncingPicker = false; }
                        repoStatus.Text = names.Count == 0 ? "All repositories" : $"{names.Count} selected";
                    }
                    check.Checked += (_, _) => Change(true);
                    check.Unchecked += (_, _) => Change(false);
                    repoRows.Children.Add(check);
                }
                repoStatus.Text = $"{selected.Count} selected · {available.Length} loaded" + (filtered.Length > 100 ? " · Showing 100; narrow the search" : "");
            }
            finally { syncingPicker = false; }
        }
        searchRepos.TextChanged += (_, _) => RenderRepositories();
        repositories.TextChanged += (_, _) => RenderRepositories();
        clearRepos.Click += (_, _) => { repositories.Text = ""; RenderRepositories(); };
        loadRepos.Click += async (_, _) => {
            loadRepos.IsEnabled = false; repoStatus.Text = "Loading repositories…";
            var queue = loadRepos.DispatcherQueue;
            string[]? result = null; string? error = null;
            try { result = await GitHubClient.ListRepositoriesAsync(path.Text.Trim(), _lifetime.Token).ConfigureAwait(false); }
            catch (Exception ex) { error = ex is WidgetException ? ex.Message : "Could not load repositories (" + ex.GetType().Name + ")."; }
            void Finish() { if (_disposed) return; loadRepos.IsEnabled = true;
                if (result == null) { repoStatus.Text = error ?? "Repository lookup failed"; return; }
                available = result; searchRepos.IsEnabled = true; RenderRepositories(); }
            if (queue.HasThreadAccess) Finish(); else queue.TryEnqueue(Finish);
        };
        RenderRepositories();
        var interval = new Slider { Header = "Refresh interval (seconds)", Minimum = 60, Maximum = 600, StepFrequency = 30, Value = draft.IntervalSeconds };
        var hint = new TextBlock { Text = "Sign in locally with gh auth login. This widget uses only GET requests and does not store tokens. A blank path uses the standard installation location. Save settings after changing Demo mode.", TextWrapping = TextWrapping.Wrap };
        var test = new Button { Content = "Test GitHub connection" };
        var testResult = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .85 };
        test.Click += async (_, _) => {
            test.IsEnabled = false;
            testResult.Text = "Checking account and PR searches…";
            var queue = test.DispatcherQueue;
            string message;
            try
            {
                var result = await GitHubClient.FetchAsync(new Settings(false, org.Text.Trim(), path.Text.Trim(), (int)interval.Value, repositories.Text, (int)previewWidth.Value), _lifetime.Token).ConfigureAwait(false);
                message = $"@{result.Login} · My PRs: {FullCount(result.Mine)} · Review: {FullCount(result.Review)} · Failed: {FullCount(result.Failed)}";
                var errors = new[] { result.Mine.Error, result.Review.Error, result.Failed.Error }.Where(e => e != null);
                foreach (var error in errors) message += "\n" + error;
                if (result.Incomplete) message += "\nPartial results";
            }
            catch (Exception ex) { message = ex is WidgetException ? ex.Message : "Connection test failed (" + ex.GetType().Name + ")."; }
            void ShowResult() { if (!_disposed) { testResult.Text = message; test.IsEnabled = true; } }
            if (queue.HasThreadAccess) ShowResult(); else queue.TryEnqueue(ShowResult);
        };
        void Save() => context.SaveSettings(JsonSerializer.Serialize(new Settings(demo.IsOn, org.Text.Trim(), path.Text.Trim(), (int)interval.Value, Settings.NormalizeRepositories(repositories.Text), (int)previewWidth.Value)));
        previewWidth.ValueChanged += (_, _) => { widthValue.Text = $"{(int)previewWidth.Value} px"; Save(); };
        demo.Toggled += (_, _) => Save(); org.TextChanged += (_, _) => Save(); path.TextChanged += (_, _) => Save(); interval.ValueChanged += (_, _) => Save(); repositories.TextChanged += (_, _) => Save();
        return new ScrollViewer { Content = new StackPanel { Spacing = 12, Children = { widthCard, demo, org, repositories, repoHint, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { loadRepos, clearRepos } }, searchRepos, repoStatus, repoScroll, path, interval, test, testResult, hint } } };
    }

    public override async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_timer != null) { _timer.Stop(); _timer.Tick -= Tick; }
        _lifetime.Cancel();
        try { await _inflight; } catch (OperationCanceledException) { }
        _lifetime.Dispose();
        await base.DisposeAsync();
    }
}

internal sealed record Settings(bool Demo = true, string Organization = "", string GhPath = "", int IntervalSeconds = 120, string Repositories = "", int PreviewWidth = 156)
{
    public const int DefaultPreviewWidth = 156;
    public const int MinPreviewWidth = 100;
    public const int MaxPreviewWidth = 240;
    public static string[] SplitRepositories(string? text) => (text ?? "").Split(new[] { '\r', '\n', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static string NormalizeRepositories(string? text) => string.Join(", ", SplitRepositories(text).OrderBy(r => r, StringComparer.OrdinalIgnoreCase));
    public static string DefaultGhPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe");
    public static Settings Read(string? json)
    {
        try
        {
            var s = string.IsNullOrWhiteSpace(json) || json.Trim() == "{}" ? new Settings() : JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            return s with { Organization = s.Organization ?? "", GhPath = s.GhPath ?? "", Repositories = NormalizeRepositories(s.Repositories), IntervalSeconds = Math.Clamp(s.IntervalSeconds, 60, 600),
                PreviewWidth = s.PreviewWidth <= 0 ? DefaultPreviewWidth : Math.Clamp(s.PreviewWidth, MinPreviewWidth, MaxPreviewWidth) };
        }
        catch (JsonException) { return new Settings(); }
    }
}
internal sealed record PullRequest(string Title, string Repository, int Number, string Url, DateTimeOffset Updated = default);
internal sealed record Group(int Total, PullRequest[] Entries, bool Incomplete = false, string? Error = null);
internal sealed record Snapshot(string Login, Group Mine, Group Review, Group Failed, DateTimeOffset Updated, bool IsDemo = false)
{
    public bool Incomplete => Mine.Incomplete || Review.Incomplete || Failed.Incomplete;
    public static Snapshot Demo()
    {
        var p = new PullRequest("Refactor authentication", "example/backend", 142, "");
        var q = new PullRequest("Fix cache invalidation", "example/backend", 135, "");
        var r = new PullRequest("Update API contracts", "example/gateway", 138, "");
        return new("demo", new(2, new[] { p, q }), new(1, new[] { r }), new(1, new[] { q }), DateTimeOffset.UtcNow, true);
    }
}
internal sealed class WidgetException(string message) : Exception(message) { }
internal static class GitHubClient
{
    public static async Task<Snapshot> FetchAsync(Settings settings, CancellationToken ct)
    {
        var path = ResolveGhPath(settings.GhPath);
        var repos = Settings.SplitRepositories(settings.Repositories);
        if (repos.Any(r => !ValidRepository(r)))
            throw new WidgetException("Invalid repository filter. Use owner/name, separated by commas.");
        if (repos.Length == 0 && settings.Organization.Length > 0 && !ValidName(settings.Organization))
            throw new WidgetException("Invalid GitHub organization name.");
        using var user = await Api(path, "user", null, ct).ConfigureAwait(false);
        var login = user.RootElement.GetProperty("login").GetString() ?? "";
        if (!ValidName(login)) throw new WidgetException("GitHub account could not be identified.");
        // Legacy REST issue search treats repeated repo qualifiers as a union.
        // Bound query size; disjoint chunks keep totals additive.
        var filters = repos.Length == 0
            ? new[] { "is:pr is:open" + (settings.Organization.Length == 0 ? "" : " org:" + settings.Organization) }
            : repos.Chunk(8).Select(batch => "is:pr is:open " + string.Join(" ", batch.Select(r => "repo:" + r))).ToArray();
        var mine = await SearchAcross(path, filters, $"author:{login}", "My PRs", ct).ConfigureAwait(false);
        var review = await SearchAcross(path, filters, $"review-requested:{login}", "Review requests", ct).ConfigureAwait(false);
        var failed = await SearchAcross(path, filters, $"author:{login} status:failure", "Failed status", ct).ConfigureAwait(false);
        return new(login, mine, review, failed, DateTimeOffset.UtcNow);
    }
    private static string ResolveGhPath(string configured)
    {
        var path = string.IsNullOrWhiteSpace(configured) ? Settings.DefaultGhPath : configured;
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFileName(path), "gh.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new WidgetException("GitHub CLI not found. Set the full path to gh.exe in settings.");
        return path;
    }
    private static bool ValidRepository(string name) => Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$") && !name.EndsWith("/.") && !name.EndsWith("/..");
    public static async Task<string[]> ListRepositoriesAsync(string configuredPath, CancellationToken ct)
    {
        using var doc = await Api(ResolveGhPath(configuredPath), "user/repos", null, ct, repositoryList: true).ConfigureAwait(false);
        return doc.RootElement.EnumerateArray().SelectMany(page => page.EnumerateArray())
            .Select(repo => repo.GetProperty("full_name").GetString() ?? "")
            .Where(ValidRepository).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static async Task<Group> SearchAcross(string path, string[] filters, string condition, string label, CancellationToken ct)
    {
        var entries = new System.Collections.Generic.List<PullRequest>();
        var total = 0; var incomplete = false;
        foreach (var filter in filters)
        {
            var group = await TrySearch(path, filter + " " + condition, label, ct).ConfigureAwait(false);
            if (group.Error != null) return group; // Don't present a misleading total for a partially failed category.
            total += group.Total; incomplete |= group.Incomplete; entries.AddRange(group.Entries);
        }
        return new Group(total, entries.OrderByDescending(p => p.Updated).ThenByDescending(p => p.Number).Take(20).ToArray(), incomplete);
    }
    private static bool ValidName(string name) => Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9-]{0,38}$");
    private static async Task<Group> TrySearch(string path, string query, string label, CancellationToken ct)
    {
        try { return await Search(path, query, ct).ConfigureAwait(false); }
        catch (WidgetException ex) { return new Group(0, Array.Empty<PullRequest>(), true, $"{label}: {ex.Message}"); }
        catch (JsonException) { return new Group(0, Array.Empty<PullRequest>(), true, $"{label}: unexpected GitHub response."); }
        catch (InvalidOperationException) { return new Group(0, Array.Empty<PullRequest>(), true, $"{label}: unexpected GitHub response or process error."); }
        catch (System.Collections.Generic.KeyNotFoundException) { return new Group(0, Array.Empty<PullRequest>(), true, $"{label}: incomplete GitHub response."); }
    }
    private static async Task<Group> Search(string path, string query, CancellationToken ct)
    {
        using var doc = await Api(path, "search/issues", query, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var items = root.GetProperty("items").EnumerateArray().Select(p => {
            var url = p.GetProperty("html_url").GetString() ?? "";
            var segments = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath.Trim('/').Split('/') : Array.Empty<string>();
            var repo = segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : "Unknown repository";
            return new PullRequest(p.GetProperty("title").GetString() ?? "Untitled", repo, p.GetProperty("number").GetInt32(), url, p.GetProperty("updated_at").GetDateTimeOffset());
        }).ToArray();
        return new(root.GetProperty("total_count").GetInt32(), items, root.GetProperty("incomplete_results").GetBoolean());
    }
    private static string ClassifyError(string diagnostic, int exitCode)
    {
        var e = diagnostic.ToLowerInvariant();
        if (e.Contains("rate limit") || e.Contains("http 429")) return "GitHub rate limit reached. Retry later.";
        if (e.Contains("http 401") || e.Contains("gh auth login") || e.Contains("not logged in") || e.Contains("bad credentials") || e.Contains("authentication required"))
            return "GitHub authentication required. Run gh auth login --hostname github.com.";
        if (e.Contains("http 403") || e.Contains("saml") || e.Contains("sso")) return "GitHub access denied. Check organization SSO and token permissions.";
        if (e.Contains("http 422")) return "GitHub rejected the search (HTTP 422). Check repository access, organization and filters.";
        if (e.Contains("http 404")) return "GitHub resource unavailable (HTTP 404). Check account access.";
        if (e.Contains("timeout") || e.Contains("no such host") || e.Contains("connection") || e.Contains("tls") || e.Contains("proxy")) return "GitHub network request failed. Check network, proxy or VPN.";
        return $"GitHub CLI failed (exit {exitCode}). Run gh api user --jq .login in PowerShell.";
    }
    private static async Task<JsonDocument> Api(string path, string endpoint, string? query, CancellationToken ct, bool repositoryList = false)
    {
        var start = new ProcessStartInfo { FileName = path, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "api", "--hostname", "github.com", "--method", "GET", endpoint }) start.ArgumentList.Add(arg);
        if (query != null)
            foreach (var arg in new[] { "-f", "q=" + query, "-f", "per_page=20", "-f", "sort=updated", "-f", "order=desc" }) start.ArgumentList.Add(arg);
        if (repositoryList)
            foreach (var arg in new[] { "--paginate", "--slurp", "-f", "per_page=100", "-f", "affiliation=owner,collaborator,organization_member", "-f", "sort=full_name" }) start.ArgumentList.Add(arg);
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        start.Environment["GH_NO_EXTENSION_UPDATE_NOTIFIER"] = "1";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(repositoryList ? 60 : 20));
        Process process;
        try { process = Process.Start(start) ?? throw new WidgetException("Could not start GitHub CLI."); }
        catch (System.ComponentModel.Win32Exception ex) { throw new WidgetException($"Could not start gh.exe (Windows error {ex.NativeErrorCode})."); }
        using var processLifetime = process;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var diagnostic = await stderr.ConfigureAwait(false); // Only classify known errors; never display raw CLI output.
            if (process.ExitCode != 0) throw new WidgetException(ClassifyError(diagnostic, process.ExitCode));
            return JsonDocument.Parse(output);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            if (ct.IsCancellationRequested) throw;
            throw new WidgetException("GitHub request timed out.");
        }
    }
}
