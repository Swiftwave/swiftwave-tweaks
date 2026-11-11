using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SwiftwaveTweaks.Core;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks;

/// <summary>
/// Reconstructed main window: sidebar navigation with a moving highlight, crossfading
/// pages backed by the surviving services, caption buttons, drag/snap via WindowChrome,
/// welcome overlay, tray support and the shared Detect → Display → Apply → Verify →
/// Revert state architecture. Page detection always runs off the UI thread behind a
/// navigation token, so stale results (the old Network freeze) cannot be applied.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly (string Label, string Key)[] PageDefs =
    [
        ("Dashboard", "dashboard"), ("Recommended", "recommended"), ("Windows", "windows"),
        ("GPU", "gpu"), ("Power", "power"), ("Storage", "storage"), ("Network", "network"),
        ("Startup", "startup"), ("Cleanup", "cleanup"), ("Diagnostics", "diagnostics"),
        ("History & Restore", "history"), ("Settings", "settings"),
    ];

    /// <summary>
    /// Fixed display order for the Windows optimizations: the general FPS/input-delay profile,
    /// then debloat, then the device interrupt work (affinity → MSI → steering).
    /// </summary>
    private static readonly string[] V3WindowsIds =
    [
        "v3-fps-input-lag", "debloat", "v3-device-affinity", "v3-msi", "v3-dynamic-interrupt-steering"
    ];

    private UserPreferences _prefs;
    private TrayIcon? _tray;
    private bool _allowClose;
    private int _navToken;
    private CaptionButton? _maxButton;
    private readonly List<CaptionButton> _captionButtons = [];

    /// <summary>
    /// Set by App before the window is created: true only when this process was launched by the
    /// Windows startup registration (the Run command carries the marker argument). "Start
    /// minimized" (tray-only startup) applies exclusively to that case — manual launches always
    /// open normally in the foreground.
    /// </summary>
    internal static bool LaunchFromStartup;

    /// <summary>
    /// "Start minimized" means *start in the notification area*: on a device-startup launch the
    /// window is created hidden with no taskbar button and only the tray icon represents the app.
    /// It is never a minimized window, and it only applies to startup launches — manual launches
    /// always open normally in the foreground. Applying this after the intro is what used to make
    /// the window appear and then minimize itself.
    /// </summary>
    private readonly bool _startedInTray;

    private Task<SystemSnapshot>? _snapshotTask;
    private Task<NvidiaInfo>? _nvidiaTask;
    private List<Optimization>? _catalog;

    /// <summary>Live CPU/memory/uptime poller owned by the dashboard; disposed when the page changes.</summary>
    private LiveTelemetry? _telemetry;
    private bool _telemetrySuspended;

    public MainWindow()
    {
        InitializeComponent();
        _prefs = PreferencesStore.Read();

        // "Start minimized" = run in the notification area (tray icon only). Set before the window
        // is first shown: no taskbar button, no minimized window waiting in the taskbar, and the
        // window is hidden in OnLoaded as soon as the tray icon exists. Manual launches are
        // unaffected (LaunchFromStartup is false).
        _startedInTray = _prefs.MinimizeOnStartup && LaunchFromStartup;
        if (_startedInTray)
        {
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }

        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = "v" + MajorMinorVersion(info);

        BuildCaptionButtons();
        UpdateAdminStatus();

        // Repaint the caption glyphs and rebuild the current page with the new palette.
        ThemeService.Changed += OnThemeChanged;

        Loaded += OnLoaded;
        StateChanged += (_, _) => _maxButton?.SetAlt(WindowState == WindowState.Maximized);
        IsVisibleChanged += (_, _) =>
        {
            // Tray-hidden windows keep no monitor running; rebuilding the dashboard on return
            // gives the gauges a fresh sampler instead of a frozen reading.
            if (!IsVisible)
            {
                if (_telemetry is null) return;
                _telemetry.Dispose();
                _telemetry = null;
                _telemetrySuspended = true;
            }
            else if (_telemetrySuspended)
            {
                _telemetrySuspended = false;
                if (Navigation.SelectedIndex >= 0 && PageDefs[Navigation.SelectedIndex].Key == "dashboard")
                    ReloadCurrentPage();
            }
        };
    }

    /// <summary>"3.1.0" → "3.1" — the visible footer reads v3.1, derived from the project version.</summary>
    internal static string MajorMinorVersion(string? informational)
    {
        if (informational is not null && Version.TryParse(informational.Split('+')[0], out var v))
            return $"{v.Major}.{v.Minor}";
        return informational ?? "3.1";
    }

    /// <summary>"3.1.0" → "3.1" — short form used by the About card.</summary>
    internal static string ShortVersion(string? informational)
    {
        if (informational is not null && Version.TryParse(informational.Split('+')[0], out var v))
            return v.Minor == 0 && v.Build <= 0 ? v.Major.ToString() : $"{v.Major}.{v.Minor}";
        return informational ?? "3.1";
    }

    internal static string FullVersion()
        => Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "3.1.0";

    // ───────────────────────────── window chrome ─────────────────────────────

    private void BuildCaptionButtons()
    {
        var min = new CaptionButton("Minimize", "M 1 5 H 9");
        min.Clicked += (_, _) => WindowState = WindowState.Minimized;

        _maxButton = new CaptionButton("Maximize", "M 1 1 H 9 V 9 H 1 Z", "M 3 1 H 9 V 7 H 7 M 1 3 H 7 V 9 H 1 Z");
        _maxButton.Clicked += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        var close = new CaptionButton("Close", "M 1 1 L 9 9 M 9 1 L 1 9", danger: true);
        close.Clicked += (_, _) => Close();

        _captionButtons.Add(min);
        _captionButtons.Add(_maxButton);
        _captionButtons.Add(close);
        CaptionButtons.Children.Add(min);
        CaptionButtons.Children.Add(_maxButton);
        CaptionButtons.Children.Add(close);
        // Dragging, resizing, double-click maximize and Windows Snap are provided by the
        // WindowChrome caption area / resize border declared in MainWindow.xaml.
    }

    /// <summary>
    /// Reflects the *token*, not group membership: the window is only shown as elevated after
    /// the process really holds an elevated administrator token.
    /// </summary>
    private void UpdateAdminStatus()
        => AdminStatus.Text = Elevation.IsElevated() ? "· administrator" : "";

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _tray = new TrayIcon(this, "swiftwave tweaks.",
            restore: () => Dispatcher.Invoke(() =>
            {
                ShowInTaskbar = true;
                Show();
                WindowState = WindowState.Normal;
                Activate();
                _tray?.Hide();
            }),
            exit: () => Dispatcher.Invoke(() =>
            {
                _allowClose = true;
                Close();
            }));
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_prefs.CloseToTray && !_allowClose && _tray is not null)
        {
            e.Cancel = true;
            Hide();
            _tray.Show();
            return;
        }
        ThemeService.Changed -= OnThemeChanged;
        _telemetry?.Dispose();
        _telemetry = null;
        _tray?.Dispose();
        base.OnClosing(e);
    }

    /// <summary>
    /// A palette switch recolours the shared brushes in place and rewrites the resource brushes.
    /// Animated or locally cloned colours (caption glyphs, toggles) are refreshed by rebuilding
    /// the visible page, so no control is left on the previous theme.
    /// </summary>
    private void OnThemeChanged()
    {
        foreach (var button in _captionButtons) button.SyncColors();
        if (!IsLoaded || PageHost.Content is null || Navigation.SelectedIndex < 0) return;
        ReloadCurrentPage();
    }

    /// <summary>
    /// Invoked on the first instance when a second launch signals over the single-instance
    /// pipe. Unhides (tray), restores (minimized) and foregrounds the existing window.
    /// Creates nothing and never replays the welcome sequence (which only runs from OnLoaded).
    /// </summary>
    internal void ActivateFromSecondInstance()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        _tray?.Hide();

        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        WindowForeground.BringToFront(handle);
        Activate();
        Focus();
    }

    // ───────────────────────────── welcome overlay ─────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Background data collection starts immediately so pages feel instant.
        _snapshotTask = SystemDetector.CollectAsync();
        _nvidiaTask = Task.Run(NvidiaService.Query);

        if (_startedInTray)
        {
            // Device startup with "Start minimized": hand the app over to the notification area.
            // The window is hidden (no taskbar button, no minimized window), the tray icon is the
            // only surface, and the intro is not played because nothing is visible.
            Hide();
            _tray?.Show();
        }

        if (Motion.ReducedMotion || _startedInTray)
        {
            // No window is visible to animate, and the intro must never be able to change the
            // window's state or Z-order, so the overlay is dropped instead of played.
            WelcomeOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            var wave = (Storyboard)Resources["WaveMotion"];
            var intro = (Storyboard)Resources["WelcomeIn"];
            var outro = (Storyboard)Resources["WelcomeOut"];
            wave.Begin();
            intro.Begin();
            await Task.Delay(2400);
            outro.Begin();
            var fade = new DoubleAnimation(1, 0, Motion.Slow) { EasingFunction = Motion.InOutEasing };
            fade.Completed += (_, _) =>
            {
                WelcomeOverlay.Visibility = Visibility.Collapsed;
                wave.Stop();
            };
            WelcomeOverlay.BeginAnimation(OpacityProperty, fade);
        }

        if (!_prefs.OnboardingCompleted)
        {
            _prefs = _prefs with { OnboardingCompleted = true };
            PreferencesStore.Write(_prefs);
        }

        // The sidebar's initial selection fired during InitializeComponent (and was skipped),
        // so load the starting page explicitly.
        if (Navigation.SelectedIndex >= 0)
            NavigateInitial(Navigation.SelectedIndex);
    }

    // ───────────────────────────── navigation ─────────────────────────────

    private void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged fires while InitializeComponent is still running (the sidebar's
        // initial SelectedIndex), before PageTitle/PageHost exist — skip that first call.
        // OnLoaded performs the initial navigation instead.
        if (!IsInitialized) return;
        int index = Navigation.SelectedIndex;
        if (index < 0 || index >= PageDefs.Length) return;
        int token = ++_navToken;

        MoveHighlight(index);
        PageTitle.Text = PageDefs[index].Label;
        if (!Motion.ReducedMotion)
        {
            TitleShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, Motion.Medium) { EasingFunction = Motion.OutEasing });
            PageTitle.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.0, 1, Motion.Medium) { EasingFunction = Motion.OutEasing });
        }

        CrossfadeTo(PageDefs[index].Key, token);
    }

    /// <summary>Navigates to a page without animation; used once at startup.</summary>
    private void NavigateInitial(int index)
    {
        int token = ++_navToken;
        MoveHighlight(index);
        PageTitle.Text = PageDefs[index].Label;
        PageHost.Content = LoadingPanel();
        _ = LoadPage(PageDefs[index].Key, token);
    }

    private void MoveHighlight(int index)
    {
        if (Navigation.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item)
        {
            Dispatcher.BeginInvoke(() => MoveHighlight(index), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        double y = item.TranslatePoint(new Point(0, 0), NavLayer).Y;
        double h = item.ActualHeight;
        if (Motion.ReducedMotion)
        {
            NavShift.Y = y;
            NavHighlight.Height = h;
            return;
        }
        NavShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(NavShift.Y, y, Motion.Medium) { EasingFunction = Motion.OutEasing });
        NavHighlight.BeginAnimation(HeightProperty,
            new DoubleAnimation(NavHighlight.Height, h, Motion.Medium) { EasingFunction = Motion.OutEasing });
    }

    private void CrossfadeTo(string key, int token)
    {
        if (Motion.ReducedMotion || PageHost.Content is null)
        {
            PageHost.Content = LoadingPanel();
            _ = LoadPage(key, token);
            return;
        }
        var fadeOut = new DoubleAnimation(PageHost.Opacity, 0, Motion.Fast) { EasingFunction = Motion.OutEasing };
        fadeOut.Completed += (_, _) =>
        {
            if (token != _navToken) return;
            PageHost.Content = LoadingPanel();
            PageHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, Motion.Fast) { EasingFunction = Motion.OutEasing });
            _ = LoadPage(key, token);
        };
        PageHost.BeginAnimation(OpacityProperty, fadeOut);
    }

    private static FrameworkElement LoadingPanel()
        => new TextBlock
        {
            Text = "Reading the current system state…",
            Foreground = Ui.TextMuted,
            FontSize = 13,
            Margin = new Thickness(2, 6, 0, 0)
        };

    /// <summary>
    /// Gathers page data off the UI thread, then swaps in the finished page. The navigation
    /// token discards results for pages the user has already left (race guard).
    /// </summary>
    private async Task LoadPage(string key, int token)
    {
        FrameworkElement? content = null;
        string? error = null;
        Action<List<(Optimization Opt, Detection Detection)>?>? applyDetection = null;
        if (key != "dashboard")
        {
            // Monitoring belongs to the dashboard only: stop the timer as soon as another page
            // is requested so nothing keeps polling in the background.
            _telemetry?.Dispose();
            _telemetry = null;
        }
        try
        {
            content = key switch
            {
                "dashboard" => BuildDashboard(await EnsureSnapshot(), out applyDetection),
                "recommended" => BuildRecommended(await DetectCatalog()),
                "windows" => BuildCategory("windows", await DetectCatalog()),
                "gpu" => BuildNvidia(await EnsureNvidia(), (await DetectCatalog()).First(o => o.Opt.Category == "NVIDIA")),
                "power" => BuildCategory("power", await DetectCatalog()),
                "storage" => BuildCategory("storage", await DetectCatalog()),
                "network" => await BuildNetwork(),
                "startup" => BuildStartup(await Task.Run(StartupService.Enumerate)),
                "cleanup" => BuildCleanup(await Task.Run(CleanupService.Measure)),
                "diagnostics" => BuildDiagnostics(await EnsureSnapshot(), await EnsureNvidia()),
                "history" => BuildHistory(),
                "settings" => BuildSettings(),
                _ => LoadingPanel(),
            };
        }
        catch (Exception ex)
        {
            SafeLog.Write($"Page load failed: {key}", ex);
            error = "This section could not be loaded: " + ex.Message;
        }

        if (token != _navToken) return; // user navigated away while detecting
        PageHost.Content = Ui.Scrollable(error is not null
            ? new TextBlock { Text = error, Foreground = Ui.Bad, TextWrapping = TextWrapping.Wrap }
            : content ?? LoadingPanel());
        if (!Motion.ReducedMotion)
            PageHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.35, 1, Motion.Medium) { EasingFunction = Motion.OutEasing });

        // The dashboard renders immediately; its coverage numbers arrive when detection finishes.
        if (applyDetection is not null)
            _ = UpdateDashboardCoverage(applyDetection, token);
    }

    /// <summary>
    /// Resolves the dashboard's coverage and recommended-count placeholders from live detection.
    /// The navigation token prevents a stale result from updating a page the user already left.
    /// </summary>
    private async Task UpdateDashboardCoverage(
        Action<List<(Optimization Opt, Detection Detection)>?> apply, int token)
    {
        List<(Optimization Opt, Detection Detection)>? detected = null;
        try
        {
            detected = await DetectCatalog();
        }
        catch (Exception ex)
        {
            SafeLog.Write("Dashboard detection failed.", ex);
        }
        if (token != _navToken) return;
        apply(detected);
    }

    private Task<SystemSnapshot> EnsureSnapshot() => _snapshotTask ??= SystemDetector.CollectAsync();
    private Task<NvidiaInfo> EnsureNvidia() => _nvidiaTask ??= Task.Run(NvidiaService.Query);

    /// <summary>
    /// Drops the memoized snapshot and drive-bound catalog after a mutation (apply, revert,
    /// preset, startup change, cleanup, restore) so the next page load re-collects real data
    /// instead of showing pre-change drive space, power plan or process counts.
    /// </summary>
    private void InvalidateSystemCache()
    {
        _snapshotTask = null;
        _catalog = null;
    }

    /// <summary>Builds the catalog once (drive-bound), then live-detects every optimization off-thread.</summary>
    private async Task<List<(Optimization Opt, Detection Detection)>> DetectCatalog()
    {
        _catalog ??= OptimizationCatalog.Build(await EnsureSnapshot());
        var catalog = _catalog;
        var detections = await Task.Run(() => catalog.Select(o =>
        {
            try { return (o, o.Detect()); }
            catch (Exception ex)
            {
                SafeLog.Write($"Detect failed: {o.Id}", ex);
                return (o, new Detection(OptState.Unknown, "Detection failed: " + ex.Message, false, ""));
            }
        }).ToList());
        return detections;
    }

    // ───────────────────────────── pages ─────────────────────────────

    private const string CpuIcon = "M9 3v2 M15 3v2 M9 19v2 M15 19v2 M3 9h2 M3 15h2 M19 9h2 M19 15h2 M6 5h12v14H6z M10 9h4v6h-4z";
    private const string GpuIcon = "M2 7h20v10H2z M5 10h5v4H5z M13 10h6 M13 13h6 M6 17v3 M18 17v3";
    private const string RamIcon = "M3 8h18v7H3z M7 8v3 M11 8v3 M15 8v3 M19 8v3 M6 15v3 M18 15v3";
    private const string StorageIcon = "M3 6h18v9H3z M6 10h.01 M17 10h.01 M7 18c2-1.5 3-1.5 5 0s3 1.5 5 0";
    private const string BoardIcon = "M4 4h16v16H4z M8 8h8v8H8z M10 4v4 M14 4v4 M10 16v4 M14 16v4 M4 10h4 M4 14h4 M16 10h4 M16 14h4";
    private const string WindowsIcon = "M4 5.5l7-1v7H4z M12.5 4.3l7.5-1.1v8.3h-7.5z M4 12h7v6.5l-7-1z M12.5 12h7.5v8.2l-7.5-1.1z";

    /// <summary>
    /// Builds the dashboard with the hardware snapshot only. Coverage and the recommended count
    /// start as neutral placeholders and are filled by <paramref name="applyDetection"/> when
    /// live detection completes, so a slow detection pass never delays the page.
    /// </summary>
    private FrameworkElement BuildDashboard(SystemSnapshot s,
        out Action<List<(Optimization Opt, Detection Detection)>?> applyDetection)
    {
        _telemetry?.Dispose();
        _telemetry = null;

        var coverageRing = new RingGauge(Ui.Accent) { VerticalAlignment = VerticalAlignment.Top };
        coverageRing.SetValue(null);
        var coverageLine = Ui.Text("Evaluating installed optimizations…", 12.5, Ui.TextDim,
            margin: new Thickness(0, 12, 0, 2));
        var coverageNote = Ui.Text(
            "Coverage excludes unsupported and manual items; partially configured or unapplied items count as inactive.",
            11.5, Ui.TextFaint);
        var applySubtitle = Ui.Text("Checking the recommended set…", 11.5, Ui.TextMuted,
            margin: new Thickness(0, 3, 0, 0));

        var panel = new StackPanel();

        // Introduction on the left, live load on the right: the overview the dashboard opens with.
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(356) });
        top.Children.Add(BuildDashboardIntro(coverageRing, coverageLine, coverageNote));

        var live = BuildLiveLoad();
        live.Margin = new Thickness(12, 0, 0, 12);
        Grid.SetColumn(live, 1);
        top.Children.Add(live);
        panel.Children.Add(top);

        panel.Children.Add(BuildQuickActions(applySubtitle));
        panel.Children.Add(BuildHardware(s));

        applyDetection = detected =>
        {
            if (detected is null)
            {
                coverageRing.SetValue(null);
                coverageLine.Text = "Optimization coverage unavailable.";
                applySubtitle.Text = "The recommended set could not be evaluated.";
                return;
            }

            var relevant = detected
                .Where(d => !string.Equals(d.Opt.Category, "Legacy", StringComparison.OrdinalIgnoreCase))
                .ToList();
            int active = relevant.Count(d => d.Detection.State == OptState.Optimized);
            int applicable = relevant.Count(d => d.Detection.State == OptState.Optimized || d.Detection.Selectable);
            coverageRing.SetValue(applicable == 0 ? 0 : 100.0 * active / applicable);
            coverageLine.Text = applicable == 0
                ? "No optimizations could be evaluated on this system."
                : $"{active} of {applicable} applicable optimizations are active.";

            var recommended = Presets.Resolve(Presets.Recommended, detected.Select(d => d.Opt).ToList());
            int pending = recommended.Count(o =>
            {
                var d = detected.First(x => x.Opt.Id == o.Id).Detection;
                return d.Selectable && d.State != OptState.Optimized;
            });
            applySubtitle.Text = pending > 0
                ? $"{pending} recommended change{(pending == 1 ? "" : "s")} available on this PC."
                : "Every recommended change is already active.";
        };

        return panel;
    }

    /// <summary>
    /// Introduction plus the optimization overview. Coverage is the share of optimizations that
    /// can actually run on this PC and are currently in their recommended state. Unsupported and
    /// manual items are excluded from both sides; partial or unapplied items count as inactive.
    /// </summary>
    private static FrameworkElement BuildDashboardIntro(RingGauge ring, TextBlock coverageLine, TextBlock coverageNote)
    {
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var copy = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
        copy.Children.Add(Ui.Text("Getting started", 23, Ui.TextWhite, FontWeights.SemiBold,
            margin: new Thickness(0, 0, 0, 6)));
        copy.Children.Add(Ui.Text(
            "Swiftwave detects every optimization live, applies only what is eligible, verifies the result " +
            "and keeps a rollback path for each change.",
            12.5, Ui.TextMuted));
        copy.Children.Add(coverageLine);
        copy.Children.Add(coverageNote);
        body.Children.Add(copy);

        var ringBlock = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        ringBlock.Children.Add(ring);
        var caption = Ui.Text("coverage", 11, Ui.TextMuted, wrap: false, margin: new Thickness(0, 3, 0, 0));
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        ringBlock.Children.Add(caption);
        Grid.SetColumn(ringBlock, 1);
        body.Children.Add(ringBlock);

        return Ui.CardPanel(body, new Thickness(20, 16, 20, 16));
    }

    /// <summary>
    /// Live CPU / memory gauges plus uptime and used RAM. Values come from <see cref="LiveTelemetry"/>
    /// on a background timer and are marshalled here; a failed reading shows "—" and turns the
    /// indicator to UNAVAILABLE instead of claiming LIVE.
    /// </summary>
    private FrameworkElement BuildLiveLoad()
    {
        var box = new StackPanel();

        // LastChildFill off: otherwise the capped pill is the fill child and its background
        // stretches across the card instead of sitting compactly in the upper-right corner.
        var head = new DockPanel { LastChildFill = false };
        head.Children.Add(Ui.Text("Live system load", 13.5, Ui.TextWhite, FontWeights.SemiBold));
        var pill = LivePill(out var pillDot, out var pillText);
        pill.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(pill, Dock.Right);
        head.Children.Add(pill);
        box.Children.Add(head);

        var gauges = new Grid { Margin = new Thickness(0, 10, 0, 4) };
        gauges.ColumnDefinitions.Add(new ColumnDefinition());
        gauges.ColumnDefinitions.Add(new ColumnDefinition());
        var cpu = new RingGauge(Ui.Accent);
        var mem = new RingGauge(Ui.Good);
        gauges.Children.Add(GaugeBlock(cpu, "CPU"));
        var memory = GaugeBlock(mem, "Memory");
        Grid.SetColumn(memory, 1);
        gauges.Children.Add(memory);
        box.Children.Add(gauges);

        var footer = new Grid { Margin = new Thickness(2, 8, 2, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var uptime = Ui.Text("Uptime unavailable", 11.5, Ui.TextFaint, wrap: false);
        var ram = Ui.Text("", 11.5, Ui.TextFaint, wrap: false);
        Grid.SetColumn(ram, 1);
        footer.Children.Add(uptime);
        footer.Children.Add(ram);
        box.Children.Add(footer);

        _telemetry = new LiveTelemetry();
        _telemetry.Sampled += sample =>
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                    ApplyTelemetry(sample, cpu, mem, uptime, ram, pill, pillDot, pillText)));
            }
            catch { /* the dispatcher is shutting down */ }
        };

        // Startup-in-tray builds the page while the window is hidden; do not poll until it is
        // shown again (the show handler rebuilds the dashboard then).
        if (!IsVisible)
        {
            _telemetry.Dispose();
            _telemetry = null;
            _telemetrySuspended = true;
        }

        return Ui.CardPanel(box);
    }

    private static FrameworkElement GaugeBlock(RingGauge gauge, string label)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(gauge);
        var caption = Ui.Text(label, 11.5, Ui.TextMuted, wrap: false, margin: new Thickness(0, 4, 0, 0));
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(caption);
        return stack;
    }

    private static Border LivePill(out Ellipse dot, out TextBlock text)
    {
        dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Ui.StatusActiveText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        text = new TextBlock { Text = "LIVE", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.StatusActiveText };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(text);
        return new Border
        {
            Background = Ui.StatusActiveBg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = row
        };
    }

    private static void ApplyTelemetry(TelemetrySample sample, RingGauge cpu, RingGauge memory,
        TextBlock uptime, TextBlock ram, Border pill, Ellipse pillDot, TextBlock pillText)
    {
        cpu.SetValue(sample.CpuAvailable ? sample.CpuPercent : null);
        memory.SetValue(sample.MemoryAvailable ? sample.MemoryPercent : null);
        uptime.Text = "Up " + FormatUptime(sample.Uptime);
        ram.Text = sample.MemoryAvailable
            ? $"{sample.UsedGb:N1} / {sample.TotalGb:N1} GB RAM"
            : "Memory unavailable";

        bool live = sample.CpuAvailable || sample.MemoryAvailable;
        pill.Background = live ? Ui.StatusActiveBg : Ui.StatusAttentionBg;
        pillDot.Fill = live ? Ui.StatusActiveText : Ui.StatusAttentionText;
        pillText.Foreground = live ? Ui.StatusActiveText : Ui.StatusAttentionText;
        pillText.Text = live ? "LIVE" : "UNAVAILABLE";
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalMinutes < 1) return "just started";
        if (uptime.TotalHours < 1) return $"{(int)uptime.TotalMinutes}m";
        if (uptime.TotalDays < 1) return $"{(int)uptime.TotalHours}h {uptime.Minutes:D2}m";
        return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes:D2}m";
    }

    /// <summary>
    /// Three dashboard shortcuts wired to existing functionality: Recommended navigation,
    /// the existing System Restore checkpoint call and the existing safe temporary-file cleanup.
    /// </summary>
    private FrameworkElement BuildQuickActions(TextBlock applySubtitle)
    {
        var grid = new Grid();
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());

        var apply = QuickActionCard("Apply recommended", applySubtitle,
            "Open Recommended", out var applyButton, out _);
        applyButton.Click += (_, _) => NavigateTo("recommended");
        apply.Margin = new Thickness(0, 0, 12, 12);
        grid.Children.Add(apply);

        var restore = QuickActionCard("Restore point",
            Ui.Text("Creates a Windows System Restore checkpoint before the next change.", 11.5, Ui.TextMuted),
            "Create restore point", out var restoreButton, out var restoreStatus);
        restoreButton.Click += async (_, _) => await RunRestorePoint(restoreButton, restoreStatus);
        restore.Margin = new Thickness(0, 0, 12, 12);
        Grid.SetColumn(restore, 1);
        grid.Children.Add(restore);

        var junk = QuickActionCard("Clean junk",
            Ui.Text("Clears temporary files only — documents, downloads and app data are never touched.", 11.5, Ui.TextMuted),
            "Clean temporary files", out var junkButton, out var junkStatus);
        junkButton.Click += async (_, _) => await RunJunkClean(junkButton, junkStatus);
        Grid.SetColumn(junk, 2);
        grid.Children.Add(junk);

        return grid;
    }

    private static Border QuickActionCard(string title, FrameworkElement subtitle, string action,
        out Button button, out TextBlock status)
    {
        var copy = new StackPanel();
        copy.Children.Add(Ui.Text(title, 13, Ui.TextWhite, FontWeights.SemiBold));
        subtitle.Margin = new Thickness(0, 3, 0, 0);
        copy.Children.Add(subtitle);
        status = Ui.Text("", 11.5, Ui.TextMuted, margin: new Thickness(0, 4, 0, 0));
        status.Visibility = Visibility.Collapsed;
        copy.Children.Add(status);

        // The button sits in a star row so all three cards share one baseline regardless of how
        // many lines their subtitles take.
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(copy);
        button = new Button
        {
            Content = action,
            Margin = new Thickness(0, 10, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetRow(button, 1);
        layout.Children.Add(button);
        return Ui.CardPanel(layout, new Thickness(14, 12, 14, 12));
    }

    private void NavigateTo(string key)
    {
        int index = Array.FindIndex(PageDefs, p => p.Key == key);
        if (index >= 0) Navigation.SelectedIndex = index;
    }

    private async Task RunRestorePoint(Button button, TextBlock status)
    {
        string original = button.Content as string ?? "Create restore point";
        button.IsEnabled = false;
        button.Content = "Creating…";
        status.Visibility = Visibility.Visible;
        status.Text = "Contacting System Protection…";
        status.Foreground = Ui.TextMuted;
        SetBusy(true, "Creating a system restore point…");
        var (success, message) = await Task.Run(() => RestorePointService.TryCreate("Swiftwave Tweaks"));
        SetBusy(false);
        status.Text = message;
        status.Foreground = success ? Ui.Good : Ui.Warn;
        button.Content = original;
        button.IsEnabled = true;
    }

    private async Task RunJunkClean(Button button, TextBlock status)
    {
        string original = button.Content as string ?? "Clean temporary files";
        button.IsEnabled = false;
        button.Content = "Cleaning…";
        status.Visibility = Visibility.Visible;
        status.Text = "Measuring and clearing temporary files…";
        status.Foreground = Ui.TextMuted;
        SetBusy(true, "Cleaning temporary files…");

        var category = CleanupService.Measure().FirstOrDefault(c => c.Id == "user-temp");
        (bool Success, double ReclaimedMb, string Message) result;
        if (category is null) result = (false, 0, "The temporary-file cleanup category is unavailable.");
        else result = await Task.Run(() => CleanupService.Clean(category));

        SetBusy(false);
        status.Text = result.Message;
        status.Foreground = result.Success ? Ui.Good : Ui.Warn;
        button.Content = original;
        button.IsEnabled = true;
        if (result.Success) InvalidateSystemCache();
    }

    /// <summary>"Your hardware" card grid: six components plus the real elevation state.</summary>
    private FrameworkElement BuildHardware(SystemSnapshot s)
    {
        var section = new StackPanel();

        var head = new Grid { Margin = new Thickness(2, 10, 2, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Ui.Text("Your hardware", 13, Ui.TextDim, FontWeights.SemiBold));

        bool elevated = Elevation.IsElevated();
        var admin = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        admin.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = elevated ? Ui.Good : Ui.Warn,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        admin.Children.Add(Ui.Text(elevated ? "Running as administrator" : "Not running as administrator",
            11.5, elevated ? Ui.TextDim : Ui.Warn, wrap: false));
        Grid.SetColumn(admin, 1);
        head.Children.Add(admin);
        section.Children.Add(head);

        var grid = new Grid();
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());

        var speeds = s.RamModules
            .Select(m => m.ConfiguredSpeedMts)
            .Where(v => v is > 0)
            .Select(v => v!.Value)
            .Distinct()
            .OrderByDescending(v => v)
            .ToList();
        string memoryDetail = s.RamModules.Count == 0
            ? "Module details unavailable"
            : (speeds.Count == 0 ? "" : $"{string.Join(" / ", speeds)} MT/s · ") + $"{s.RamModules.Count} module(s)";

        Place(HardwareCard(CpuIcon, "Processor", s.CpuName,
            $"{s.CpuCores} cores · {s.CpuThreads} threads"), 0, 0);
        Place(HardwareCard(GpuIcon, "Graphics", s.GpuName,
            string.IsNullOrWhiteSpace(s.GpuDriver) ? "" : "Driver " + s.GpuDriver), 1, 0);
        Place(HardwareCard(RamIcon, "Memory", $"{s.MemoryGb:N0} GB", memoryDetail), 2, 0);
        Place(StorageCard(s), 0, 1);
        Place(HardwareCard(BoardIcon, "Motherboard",
            string.IsNullOrWhiteSpace(s.MotherboardModel) ? "Unavailable" : s.MotherboardModel,
            s.MotherboardVendor), 1, 1);
        Place(HardwareCard(WindowsIcon, "Windows", s.WindowsEdition,
            $"Version {s.WindowsVersion} · build {s.WindowsBuild}"), 2, 1);

        section.Children.Add(grid);
        return section;

        void Place(FrameworkElement card, int column, int row)
        {
            card.Margin = new Thickness(0, 0, column == 2 ? 0 : 12, 12);
            Grid.SetColumn(card, column);
            Grid.SetRow(card, row);
            grid.Children.Add(card);
        }
    }

    private static FrameworkElement HardwareCard(string icon, string label, string value,
        string? detail = null, FrameworkElement? extra = null)
    {
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Icon(icon, 15, Ui.TextMuted));
        head.Children.Add(Ui.Text(label, 11.5, Ui.TextMuted, wrap: false, margin: new Thickness(7, 0, 0, 0)));
        stack.Children.Add(head);
        stack.Children.Add(Ui.Text(value, 13.5, Ui.TextWhite, FontWeights.SemiBold,
            margin: new Thickness(0, 8, 0, 0), trimming: TextTrimming.CharacterEllipsis));
        if (!string.IsNullOrWhiteSpace(detail))
            stack.Children.Add(Ui.Text(detail, 11.5, Ui.TextDim, margin: new Thickness(0, 3, 0, 0)));
        if (extra is not null) stack.Children.Add(extra);
        return Ui.CardPanel(stack, new Thickness(14, 12, 14, 12));
    }

    /// <summary>Primary system drive with total/used/free capacity and a restrained usage bar.</summary>
    private static FrameworkElement StorageCard(SystemSnapshot s)
    {
        string root = (System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
        var drive = s.Drives.FirstOrDefault(d => string.Equals(d.Letter.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase))
                    ?? s.Drives.FirstOrDefault();
        if (drive is null)
            return HardwareCard(StorageIcon, "Storage", "Unavailable", "No fixed drive information.");

        double used = Math.Max(0, drive.SizeGb - drive.FreeGb);
        double fraction = drive.SizeGb > 0 ? used / drive.SizeGb : 0;

        var track = new Border { Height = 5, CornerRadius = new CornerRadius(3), Background = Ui.CardSoft };
        var fill = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(3),
            Background = Ui.Accent,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var bar = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        bar.Children.Add(track);
        bar.Children.Add(fill);
        bar.SizeChanged += (_, e) => fill.Width = Math.Max(0, e.NewSize.Width * fraction);

        return HardwareCard(StorageIcon, "Storage", $"{drive.Letter} · {drive.SizeGb:N0} GB",
            $"{used:N0} GB used · {drive.FreeGb:N0} GB free", bar);
    }

    private FrameworkElement BuildRecommended(List<(Optimization Opt, Detection Detection)> detected)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text(
            "Preset bundles apply each member optimization through its own detect → apply → verify cycle. " +
            "Anything already in the recommended state is skipped.",
            12.5, Ui.TextMuted, margin: new Thickness(2, 0, 0, 10)));

        foreach (var preset in Presets.All)
        {
            var members = Presets.Resolve(preset, detected.Select(d => d.Opt).ToList());
            var box = new StackPanel();
            var head = new DockPanel();
            head.Children.Add(Ui.Text(preset.Name, 14, Ui.TextWhite, FontWeights.SemiBold));
            var apply = new Button { Content = "Apply preset", HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(apply, Dock.Right);
            head.Children.Add(apply);
            box.Children.Add(head);
            box.Children.Add(Ui.Text(preset.Description, 12.5, Ui.TextMuted, margin: new Thickness(0, 4, 0, 8)));
            foreach (var m in members)
            {
                var d = detected.FirstOrDefault(x => x.Opt.Id == m.Id);
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(Ui.Text("• " + m.Name, 12.5, Ui.TextDim));
                if (d.Opt is not null)
                {
                    // Recommended terminology: already active → Active, everything actionable → Available.
                    FrameworkElement state = d.Detection.State switch
                    {
                        OptState.Optimized => Ui.StatusBox("Active", Ui.StatusActiveText, Ui.StatusActiveBg),
                        OptState.NeedsAttention or OptState.Unknown => Ui.StatusBox("Available", Ui.StatusAvailableText, Ui.StatusAvailableBg),
                        _ => Ui.Text(d.Detection.State.ToString(), 11.5, Ui.TextFaint, wrap: false),
                    };
                    state.VerticalAlignment = VerticalAlignment.Center;
                    DockPanel.SetDock(state, Dock.Right);
                    row.Children.Add(state);
                }
                box.Children.Add(row);
            }
            apply.Click += async (_, _) => await RunPreset(preset, members);
            panel.Children.Add(Ui.CardPanel(box));
        }
        return panel;
    }

    private async Task RunPreset(Presets.PresetDef preset, List<Optimization> members)
    {
        SetBusy(true, $"Applying {preset.Name}…");
        int applied = 0, skipped = 0, failed = 0;
        foreach (var opt in members)
        {
            StatusText.Text = $"Applying {preset.Name}: {opt.Name}…";
            var result = await Task.Run(() => opt.Apply());
            switch (result.Status)
            {
                case ApplyStatus.Applied: applied++; break;
                case ApplyStatus.AlreadyOptimized: skipped++; break;
                case ApplyStatus.Failed: failed++; break;
            }
        }
        StatusText.Text = $"{preset.Name}: {applied} applied, {skipped} already optimal" + (failed > 0 ? $", {failed} failed (see History)" : ".");
        SetBusy(false);
        InvalidateSystemCache(); // power plan / drive data may have changed
        ReloadCurrentPage();
    }

    internal FrameworkElement BuildCategory(string key, List<(Optimization Opt, Detection Detection)> detected)
    {
        var panel = new StackPanel();
        switch (key)
        {
            case "windows":
                panel.Children.Add(Ui.Text(
                    "Each Windows optimization detects this machine first, applies only what is eligible, " +
                    "then verifies the result. Device-level interrupt changes are advanced and need a restart.",
                    12.5, Ui.TextMuted, margin: new Thickness(2, 0, 0, 10)));
                panel.Children.Add(Ui.SectionHeader("General"));
                foreach (var d in detected.Where(x => x.Opt.Id == "fortnite-affinity"))
                    panel.Children.Add(new OptimizationCard(d.Opt, d.Detection, this, roomy: true));
                panel.Children.Add(Ui.SectionHeader("Performance & background activity"));
                foreach (var id in V3WindowsIds)
                    foreach (var d in detected.Where(x => x.Opt.Id == id))
                        panel.Children.Add(new OptimizationCard(d.Opt, d.Detection, this));
                panel.Children.Add(Ui.SectionHeader("Windows features"));
                foreach (var d in detected.Where(x => x.Opt.Category == "Gaming"
                    && !V3WindowsIds.Contains(x.Opt.Id) && x.Opt.Id != "fortnite-affinity"))
                    panel.Children.Add(new OptimizationCard(d.Opt, d.Detection, this, roomy: true));
                panel.Children.Add(Ui.SectionHeader("Visual effects"));
                foreach (var d in detected.Where(x => x.Opt.Category == "Visual"))
                    panel.Children.Add(new OptimizationCard(d.Opt, d.Detection, this, roomy: true));
                break;
            case "power":
                panel.Children.Add(Ui.Text(
                    "Only supported, verifiable Windows power policies are exposed. No forced minimum processor state, " +
                    "no forced maximum frequency, no BIOS or voltage modifications.",
                    12.5, Ui.TextMuted, margin: new Thickness(2, 0, 0, 10)));
                foreach (var d in detected.Where(x => x.Opt.Category == "Power"))
                    panel.Children.Add(new OptimizationCard(d.Opt, d.Detection, this));
                break;
            case "storage":
                panel.Children.Add(Ui.Text(
                    "Windows Optimize Drives — TRIM/retrim on SSD and NVMe media, defragmentation on mechanical drives. " +
                    "The correct method is chosen automatically per drive. Storage & memory reworks service hosting " +
                    "and NTFS behaviour and needs a restart.",
                    12.5, Ui.TextMuted, margin: new Thickness(2, 0, 0, 10)));
                panel.Children.Add(Ui.SectionHeader("Drives"));
                foreach (var d in detected.Where(x => x.Opt.Category == "Storage" && x.Opt.Id.StartsWith("storage-")))
                    panel.Children.Add(new OptimizationCard(d.Opt, d.Detection, this, roomy: true));
                panel.Children.Add(Ui.SectionHeader("Storage & memory"));
                foreach (var d in detected.Where(x => x.Opt.Id == "v3-storage-memory"))
                    panel.Children.Add(new OptimizationCard(d.Opt, d.Detection, this, roomy: true));
                break;
        }
        return panel;
    }

    /// <summary>
    /// Builds every optimization page tree from pre-collected detection data. Used only by the
    /// headless UI self-test so that page composition is verified without showing a window.
    /// </summary>
    internal (string Key, FrameworkElement Root)[] BuildPagesForTest(
        List<(Optimization Opt, Detection Detection)> detected)
        =>
        [
            ("windows", BuildCategory("windows", detected)),
            ("power", BuildCategory("power", detected)),
            ("storage", BuildCategory("storage", detected)),
            ("network", ComposeNetwork(NetworkAdapterService.ReadAsync().GetAwaiter().GetResult(), detected)),
        ];

    private FrameworkElement BuildNvidia(NvidiaInfo info, (Optimization Opt, Detection Detection) detected)
    {
        var panel = new StackPanel();
        if (!info.Present)
        {
            panel.Children.Add(Ui.CardPanel(Ui.Text(
                "No NVIDIA GPU was detected on this system. NVIDIA-specific settings are unavailable.",
                12.5, Ui.TextMuted)));
            return panel;
        }

        var facts = new StackPanel();
        facts.Children.Add(Ui.Text(info.Name, 15, Ui.TextWhite, FontWeights.SemiBold));
        facts.Children.Add(Ui.Text(
            $"Driver {info.Driver} · {info.Vram} VRAM" +
            (info.Temperature is not null ? $" · {info.Temperature}" : "") +
            (info.PowerDraw is not null ? $" · {info.PowerDraw}" : "") +
            (info.RebarState is not null ? $"\nResizable BAR: {info.RebarState}" : ""),
            12.5, Ui.TextDim, margin: new Thickness(0, 4, 0, 0)));
        panel.Children.Add(Ui.CardPanel(facts));

        // Use the detection already gathered off-thread by DetectCatalog(); the NVAPI read
        // must never run on the UI thread.
        panel.Children.Add(new OptimizationCard(detected.Opt, detected.Detection, this));
        return panel;
    }

    private async Task<FrameworkElement> BuildNetwork()
    {
        // Detection runs entirely off the UI thread (the original freeze came from synchronous
        // detection); LoadPage's navigation token already guards against stale results.
        var reading = await NetworkAdapterService.ReadAsync();
        var detected = await DetectCatalog();
        return ComposeNetwork(reading, detected);
    }

    /// <summary>
    /// Builds the Network page from already-collected data. Split out so the headless UI
    /// self-test exercises exactly the same card composition as the live page.
    /// </summary>
    internal FrameworkElement ComposeNetwork(
        NetworkAdapterReading reading, List<(Optimization Opt, Detection Detection)> detected)
    {
        var panel = new StackPanel();

        // Windows-level TCP/IP tuning is independent of the active adapter model, so it is always
        // shown (and remains usable on Wi-Fi-only or disconnected machines).
        panel.Children.Add(Ui.SectionHeader("Windows network performance"));
        var perf = detected.FirstOrDefault(d => d.Opt.Id == "v3-network-performance");
        if (perf.Opt is not null)
            panel.Children.Add(new OptimizationCard(perf.Opt, perf.Detection, this));

        panel.Children.Add(Ui.SectionHeader("Adapter"));
        if (!reading.Present)
        {
            panel.Children.Add(Ui.CardPanel(Ui.Text(
                "No active wired network adapter was detected. Adapter energy-saving controls are unavailable " +
                "on wireless or disconnected adapters; the Windows tuning above is unaffected.",
                12.5, Ui.TextMuted)));
            return panel;
        }

        var facts = new StackPanel();
        facts.Children.Add(Ui.Text(reading.Description, 14, Ui.TextWhite, FontWeights.SemiBold));
        facts.Children.Add(Ui.Text($"Link speed {reading.LinkSpeed} · driver {reading.DriverVersion}",
            12.5, Ui.TextDim, margin: new Thickness(0, 4, 0, 0)));
        panel.Children.Add(Ui.CardPanel(facts));

        if (!reading.SupportsEnergyControl)
        {
            // Deliberately hidden when the driver exposes no manageable energy controls.
            panel.Children.Add(Ui.CardPanel(Ui.Text(
                "This adapter's driver exposes no supported energy-saving controls (EEE / Green Ethernet / " +
                "Power Saving Mode), so there is nothing to change here.",
                12.5, Ui.TextMuted)));
            return panel;
        }

        var opt = detected.First(d => d.Opt.Id == "network-power-saving");
        panel.Children.Add(new OptimizationCard(opt.Opt, opt.Detection, this));
        return panel;
    }

    private FrameworkElement BuildStartup(List<StartupEntry> entries)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text(
            "Applications Windows starts with your session. Important system, security, driver, audio and input " +
            "components are never disabled automatically. Disabled entries are backed up and can be re-enabled here.",
            12.5, Ui.TextMuted, margin: new Thickness(2, 0, 0, 10)));

        if (entries.Count == 0)
        {
            panel.Children.Add(Ui.CardPanel(Ui.Text("No startup entries were found.", 12.5, Ui.TextMuted)));
            return panel;
        }

        foreach (var entry in entries)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Ui.Text(entry.Name, 13, Ui.TextWhite, FontWeights.SemiBold, wrap: false));
            head.Children.Add(Ui.Tag(entry.Scope, Ui.TextFaint));
            head.Children.Add(Ui.Tag(entry.Classification,
                entry.Classification == StartupEntry.Important ? Ui.Good :
                entry.Classification == StartupEntry.Probably ? Ui.Warn : Ui.TextDim));
            if (!entry.Enabled) head.Children.Add(Ui.Tag("Disabled", Ui.TextFaint));
            text.Children.Add(head);
            text.Children.Add(Ui.Text(entry.Command, 11.5, Ui.TextFaint,
                margin: new Thickness(0, 2, 0, 0), trimming: TextTrimming.CharacterEllipsis));
            row.Children.Add(text);

            if (entry.Enabled && entry.Classification != StartupEntry.Important)
            {
                var disable = new Button { Content = "Disable", VerticalAlignment = VerticalAlignment.Center };
                disable.Click += async (_, _) => await RunStartupAction(entry, disable: true);
                Grid.SetColumn(disable, 1);
                row.Children.Add(disable);
            }
            else if (!entry.Enabled)
            {
                var enable = new Button { Content = "Enable", VerticalAlignment = VerticalAlignment.Center };
                enable.Click += async (_, _) => await RunStartupAction(entry, disable: false);
                Grid.SetColumn(enable, 1);
                row.Children.Add(enable);
            }
            panel.Children.Add(Ui.CardPanel(row, new Thickness(14, 10, 14, 10)));
        }
        return panel;
    }

    private async Task RunStartupAction(StartupEntry entry, bool disable)
    {
        SetBusy(true, (disable ? "Disabling " : "Enabling ") + entry.Name + "…");
        var (success, message) = await Task.Run(() => disable ? StartupService.Disable(entry) : StartupService.Enable(entry));
        StatusText.Text = message;
        SetBusy(false);
        InvalidateSystemCache(); // process counts and startup state may have changed
        ReloadCurrentPage();
    }

    private FrameworkElement BuildCleanup(List<CleanupCategory> categories)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text(
            "Safe cleanup only: temporary files, the Windows Temp folder and the Recycle Bin. " +
            "Documents, downloads, games, browser profiles and application data are never touched. " +
            "Sizes are measured before deletion and reclaimed space is verified afterwards.",
            12.5, Ui.TextMuted, margin: new Thickness(2, 0, 0, 10)));

        foreach (var category in categories)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Ui.Text(category.Name, 13, Ui.TextWhite, FontWeights.SemiBold, wrap: false));
            head.Children.Add(Ui.Tag(
                category.SizeMb > 0 ? $"{category.SizeMb:N0} MB" : category.MeasureNote.Length > 0 ? category.MeasureNote : "—",
                category.SizeMb > 0 ? Ui.Warn : Ui.TextFaint));
            if (category.RequiresAdmin) head.Children.Add(Ui.Tag("Needs Administrator", Ui.Accent));
            text.Children.Add(head);
            text.Children.Add(Ui.Text(category.Description, 12, Ui.TextMuted, margin: new Thickness(0, 2, 0, 0)));
            row.Children.Add(text);
            var clean = new Button { Content = "Clean", VerticalAlignment = VerticalAlignment.Center };
            clean.Click += async (_, _) =>
            {
                SetBusy(true, $"Cleaning {category.Name}…");
                var result = await Task.Run(() => CleanupService.Clean(category));
                StatusText.Text = result.Message;
                SetBusy(false);
                InvalidateSystemCache(); // drive free space and sizes may have changed
                ReloadCurrentPage();
            };
            Grid.SetColumn(clean, 1);
            row.Children.Add(clean);
            panel.Children.Add(Ui.CardPanel(row, new Thickness(14, 10, 14, 10)));
        }
        return panel;
    }

    private FrameworkElement BuildDiagnostics(SystemSnapshot s, NvidiaInfo nvidia)
    {
        var panel = new StackPanel();
        var issues = DiagnosticsEngine.Run(s, nvidia);
        if (issues.Count == 0)
        {
            panel.Children.Add(Ui.CardPanel(Ui.Text(
                "No issues were identified from the detected system state.", 13, Ui.Good)));
            return panel;
        }
        panel.Children.Add(Ui.Text(
            $"{issues.Count} finding(s) based on live detection. Manual items describe what to do in Windows or BIOS/UEFI — this tool never pretends to automate them.",
            12.5, Ui.TextMuted, margin: new Thickness(2, 0, 0, 10)));
        foreach (var issue in issues)
        {
            var box = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Ui.Text(issue.Problem, 13, Ui.TextWhite, FontWeights.SemiBold));
            head.Children.Add(Ui.Tag(issue.Mode, issue.Mode.StartsWith("Automatic") ? Ui.Accent : Ui.TextFaint));
            box.Children.Add(head);
            box.Children.Add(Ui.Text(issue.WhyItMatters, 12, Ui.TextMuted, margin: new Thickness(0, 4, 0, 0)));
            box.Children.Add(Ui.Text("→ " + issue.RecommendedAction, 12, Ui.TextDim, margin: new Thickness(0, 4, 0, 0)));
            panel.Children.Add(Ui.CardPanel(box));
        }
        return panel;
    }

    private FrameworkElement BuildHistory()
    {
        var panel = new StackPanel();
        var entries = RollbackStore.Read().OrderByDescending(x => x.CreatedAt).ToList();
        panel.Children.Add(Ui.Text(
            "Changes applied by Swiftwave Tweaks with their captured previous state. Restoring re-applies the " +
            "recorded previous values and verifies the result. History is a record of operations — the current " +
            "state shown elsewhere always comes from live detection.",
            12.5, Ui.TextMuted, margin: new Thickness(2, 0, 0, 10)));

        if (entries.Count == 0)
        {
            panel.Children.Add(Ui.CardPanel(Ui.Text("No recorded changes yet.", 12.5, Ui.TextMuted)));
            return panel;
        }

        foreach (var entry in entries)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(Ui.Text(entry.Name, 13, Ui.TextWhite, FontWeights.SemiBold, wrap: false));
            text.Children.Add(Ui.Text(entry.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), 11.5, Ui.TextFaint));
            row.Children.Add(text);
            var restore = new Button { Content = "Restore", VerticalAlignment = VerticalAlignment.Center };
            restore.Click += async (_, _) =>
            {
                if (_catalog is null) await DetectCatalog();
                var opt = _catalog?.FirstOrDefault(o => o.Id == entry.Id);
                if (opt is null) { StatusText.Text = "This change can no longer be restored by the current build."; return; }
                SetBusy(true, $"Restoring {entry.Name}…");
                var result = await Task.Run(() => opt.Rollback(entry.PreviousState));
                if (result.Status == ApplyStatus.Applied) RollbackStore.Remove(entry.Id);
                StatusText.Text = result.Message;
                SetBusy(false);
                InvalidateSystemCache(); // the restored setting may affect snapshot data
                ReloadCurrentPage();
            };
            Grid.SetColumn(restore, 1);
            row.Children.Add(restore);
            panel.Children.Add(Ui.CardPanel(row, new Thickness(14, 10, 14, 10)));
        }
        return panel;
    }

    private FrameworkElement BuildSettings()
    {
        var panel = new StackPanel();

        panel.Children.Add(Ui.SectionHeader("Appearance"));
        panel.Children.Add(ThemePicker());

        panel.Children.Add(Ui.SectionHeader("Preferences"));
        panel.Children.Add(StartupRow());
        panel.Children.Add(PreferenceRow("Start minimized",
            "Only when Windows starts the app at sign-in: runs in the notification area as a tray icon, with no window or taskbar button. Manually launched windows always open in the foreground.",
            _prefs.MinimizeOnStartup, v => SavePrefs(_prefs with { MinimizeOnStartup = v })));
        panel.Children.Add(PreferenceRow("Close to system tray",
            "Closing the window keeps Swiftwave Tweaks running in the notification area. Left-click the icon to restore, right-click to exit.",
            _prefs.CloseToTray, v => SavePrefs(_prefs with { CloseToTray = v })));

        panel.Children.Add(Ui.SectionHeader("About"));
        var about = new StackPanel();
        about.Children.Add(Ui.Text("swiftwave tweaks.", 16, Ui.TextWhite, FontWeights.SemiBold));
        about.Children.Add(Ui.Text($"Version v{ShortVersion(FullVersion())} (build {FullVersion()})", 12.5, Ui.TextDim,
            margin: new Thickness(0, 4, 0, 0)));
        about.Children.Add(Ui.Text("Windows 11 performance & optimization utility — safe, reversible, verified.",
            12, Ui.TextMuted, margin: new Thickness(0, 4, 0, 0)));
        panel.Children.Add(Ui.CardPanel(about));

        var paths = new StackPanel();
        paths.Children.Add(Ui.Text("Data locations", 13.5, Ui.TextWhite, FontWeights.SemiBold));
        paths.Children.Add(Ui.Text(
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftwaveTweaks") +
            "\nActivity logs, rollback history and preferences are stored here.",
            12, Ui.TextMuted, margin: new Thickness(0, 4, 0, 0)));
        panel.Children.Add(Ui.CardPanel(paths));
        return panel;
    }

    /// <summary>
    /// Colour-theme chooser. The whole app repaints immediately (shared brushes + resource
    /// brushes) and the choice is written to preferences.json, so it survives a restart.
    /// </summary>
    private FrameworkElement ThemePicker()
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text("Color theme", 13, Ui.TextWhite, FontWeights.SemiBold, wrap: false));
        stack.Children.Add(Ui.Text(
            "Applies to every page, card and text colour. The choice is saved for the next launch.",
            12, Ui.TextMuted, margin: new Thickness(0, 2, 0, 0)));

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        var options = new (AppTheme Mode, string Title, string Detail)[]
        {
            (AppTheme.Default, "Default", "Swiftwave navy"),
            (AppTheme.Light, "Light", "Black text on light surfaces"),
            (AppTheme.Dark, "Dark", "Neutral dark"),
            (AppTheme.System, "Follow system", SystemThemeDetail()),
        };
        for (int i = 0; i < options.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var option = ThemeOption(options[i].Mode, options[i].Title, options[i].Detail);
            option.Margin = new Thickness(i == 0 ? 0 : 4, 0, i == options.Length - 1 ? 0 : 4, 0);
            Grid.SetColumn(option, i);
            grid.Children.Add(option);
        }
        stack.Children.Add(grid);
        return Ui.CardPanel(stack);
    }

    private Button ThemeOption(AppTheme mode, string title, string detail)
    {
        bool selected = _prefs.Theme == mode;
        var copy = new StackPanel();
        copy.Children.Add(Ui.Text(title, 13, Ui.TextWhite, FontWeights.SemiBold, wrap: false));
        copy.Children.Add(Ui.Text(detail, 11.5, Ui.TextMuted, margin: new Thickness(0, 3, 0, 0)));

        var button = new Button
        {
            Content = copy,
            Background = selected ? Ui.CardSoft : Ui.Card,
            BorderBrush = selected ? Ui.Accent : Ui.Line,
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(12, 10, 12, 10),
            MinHeight = 64
        };
        AutomationProperties.SetName(button, title + " theme");
        button.Click += (_, _) => SelectTheme(mode);
        return button;
    }

    private static string SystemThemeDetail()
        => Environment.OSVersion.Version.Build >= 22000
            ? "Matches the Windows light/dark setting"
            : "Matches the Windows app theme";

    private void SelectTheme(AppTheme mode)
    {
        if (_prefs.Theme == mode) return;
        SavePrefs(_prefs with { Theme = mode });
        // ThemeService raises Changed, which rebuilds this page with the new palette.
        ThemeService.Apply(mode);
    }

    private FrameworkElement PreferenceRow(string title, string description, bool value, Action<bool> changed)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(Ui.Text(title, 13, Ui.TextWhite, FontWeights.SemiBold, wrap: false));
        text.Children.Add(Ui.Text(description, 12, Ui.TextMuted, margin: new Thickness(0, 2, 0, 0)));
        grid.Children.Add(text);
        var toggle = new ToggleSwitch { VerticalAlignment = VerticalAlignment.Center };
        toggle.SetState(value);
        toggle.Toggled += (_, _) => changed(toggle.IsChecked == true);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return Ui.CardPanel(grid, new Thickness(14, 10, 14, 10));
    }

    /// <summary>
    /// The autostart preference. Its switch reflects the real HKCU Run entry (not a cached
    /// flag), and a failed registration is reported in place and the switch springs back, so
    /// the UI can never claim a state Windows does not have.
    /// </summary>
    private FrameworkElement StartupRow()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(Ui.Text("Run Swiftwave Tweaks on startup", 13, Ui.TextWhite, FontWeights.SemiBold, wrap: false));
        var detail = Ui.Text(StartupDetail(), 12, Ui.TextMuted, margin: new Thickness(0, 2, 0, 0));
        text.Children.Add(detail);
        grid.Children.Add(text);

        var toggle = new ToggleSwitch { VerticalAlignment = VerticalAlignment.Center };
        toggle.SetState(SelfStartup.GetState() == SelfStartup.State.Enabled);
        toggle.Toggled += (_, _) =>
        {
            bool requested = toggle.IsChecked == true;
            var result = SelfStartup.SetEnabled(requested);
            bool actual = SelfStartup.GetState() == SelfStartup.State.Enabled;
            toggle.SetState(actual);
            if (result.Success && actual == requested)
                SavePrefs(_prefs with { RunOnStartup = requested });
            detail.Text = result.Success ? StartupDetail() : result.Message;
            detail.Foreground = result.Success ? Ui.TextMuted : Ui.Bad;
        };
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return Ui.CardPanel(grid, new Thickness(14, 10, 14, 10));
    }

    private static string StartupDetail()
    {
        var state = SelfStartup.GetState();
        if (state == SelfStartup.State.Enabled)
        {
            return "Registered for this Windows account: " + SelfStartup.ExecutablePath +
                   (SelfStartup.IsDevelopmentBuild() ? " (development build path)." : ".") +
                   " Windows asks for administrator approval (UAC) when it starts with Windows.";
        }
        if (state == SelfStartup.State.Mismatched)
        {
            return "Windows has a startup entry that points to a different or missing file. Turn this off and on again to repair it.";
        }
        return "Not registered. When enabled it starts with Windows for this account only; Windows asks for " +
               "administrator approval (UAC) at sign-in, and declining that prompt means the app does not launch.";
    }

    private void SavePrefs(UserPreferences prefs)
    {
        _prefs = prefs;
        PreferencesStore.Write(prefs);
    }

    // ───────────────────────────── shared apply/restore plumbing ─────────────────────────────

    internal void SetBusy(bool busy, string? status = null)
    {
        BusyDots.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        var pulse = (Storyboard)Resources["BusyPulse"];
        if (busy) pulse.Begin(); else pulse.Stop();
        if (status is not null) StatusText.Text = status;
    }

    internal async Task ApplyOptimization(Optimization opt, OptimizationCard card)
    {
        SetBusy(true, $"Applying {opt.Name}…");
        card.SetBusy();
        var result = await Task.Run(() => opt.Apply());
        StatusText.Text = result.Message + (result.RestartRequired ? " A restart is required." : "");
        SetBusy(false);
        InvalidateSystemCache(); // the applied change may affect snapshot data
        await card.Refresh();
    }

    internal async Task RevertOptimization(Optimization opt, OptimizationCard card)
    {
        var entry = RollbackStore.Read().FirstOrDefault(x => x.Id == opt.Id);
        if (entry is null)
        {
            StatusText.Text = "No previous state was recorded for this change, so there is nothing to restore.";
            await card.Refresh();
            return;
        }
        SetBusy(true, $"Restoring {opt.Name}…");
        card.SetBusy();
        var result = await Task.Run(() => opt.Rollback(entry.PreviousState));
        if (result.Status == ApplyStatus.Applied) RollbackStore.Remove(opt.Id);
        StatusText.Text = result.Message + (result.RestartRequired ? " A restart is required." : "");
        SetBusy(false);
        InvalidateSystemCache(); // the restored setting may affect snapshot data
        await card.Refresh();
    }

    internal void ReloadCurrentPage()
    {
        int index = Navigation.SelectedIndex;
        if (index >= 0) { int token = ++_navToken; PageHost.Content = LoadingPanel(); _ = LoadPage(PageDefs[index].Key, token); }
    }

}

/// <summary>
/// One optimization row: name, v2 metadata tags, description, live-detected state and a
/// Swiftwave toggle (or a one-shot Apply button for preset/maintenance-style operations).
/// The v1-style general status (Active / Needs attention / Available) sits top-right.
/// The toggle position always reflects live detection, never a saved boolean.
/// </summary>
public sealed class OptimizationCard : Border
{
    private readonly Optimization _opt;
    private readonly MainWindow _window;
    private readonly TextBlock _state;
    private readonly Border _status;
    private readonly StackPanel _head;
    private readonly ToggleSwitch? _toggle;
    private readonly Button? _run;
    private Detection _last = new(OptState.Unknown, "", false, "");

    /// <summary>Identity and last detected state, exposed for the headless UI self-test.</summary>
    internal string OptId => _opt.Id;
    internal string OptName => _opt.Name;
    internal Detection Current => _last;

    /// <summary>State lines that merely repeat the general status (on/off) carry no extra information.</summary>
    private static readonly HashSet<string> GenericStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Enabled", "Disabled", "On", "Off", "Optimized", "Not optimized",
        "Enabled (Windows default)", "Disabled (Windows default)",
        "Not configured (Windows/driver default)",
    };

    public OptimizationCard(Optimization opt, Detection detection, MainWindow window, bool roomy = false)
    {
        _opt = opt;
        _window = window;
        Background = Ui.Card;
        CornerRadius = new CornerRadius(10);
        BorderBrush = Ui.Line;
        BorderThickness = new Thickness(1);
        Padding = roomy ? new Thickness(16, 22, 16, 22) : new Thickness(16, 14, 16, 14);
        Margin = new Thickness(0, 0, 0, 12);
        if (roomy) MinHeight = 132;

        var outer = new Grid();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        _head = new StackPanel { Orientation = Orientation.Horizontal };
        _head.Children.Add(Ui.Text(opt.Name, 13.5, Ui.TextWhite, FontWeights.SemiBold, wrap: false));
        if (opt.Safety == Safety.Advanced) _head.Children.Add(Ui.Tag("Advanced", Ui.Warn));
        else if (opt.Safety == Safety.Moderate) _head.Children.Add(Ui.Tag("Moderate", Ui.TextDim));
        if (opt.RequiresAdmin) _head.Children.Add(Ui.Tag("Needs Administrator", Ui.Accent));
        if (opt.RequiresRestart) _head.Children.Add(Ui.Tag("Restart Required", Ui.Warn));
        text.Children.Add(_head);
        text.Children.Add(Ui.Text(opt.Description, 12, Ui.TextMuted, margin: roomy ? new Thickness(0, 8, 0, 0) : new Thickness(0, 3, 0, 0)));
        _state = Ui.Text("", 12, Ui.TextDim, margin: roomy ? new Thickness(0, 12, 0, 0) : new Thickness(0, 6, 0, 0));
        text.Children.Add(_state);
        grid.Children.Add(text);

        // One-shot operations (storage optimize, visual presets) get an Apply button;
        // stateful toggles get the Swiftwave switch.
        bool oneShot = opt.Id.StartsWith("storage-") || opt.Id.StartsWith("visual-");
        if (oneShot)
        {
            _run = new Button { Content = "Apply", VerticalAlignment = VerticalAlignment.Center };
            _run.Click += async (_, _) => await _window.ApplyOptimization(_opt, this);
            Grid.SetColumn(_run, 1);
            grid.Children.Add(_run);
        }
        else
        {
            _toggle = new ToggleSwitch { VerticalAlignment = VerticalAlignment.Center };
            _toggle.Toggled += async (_, on) =>
            {
                _toggle.IsEnabled = false;
                if (on) await _window.ApplyOptimization(_opt, this);
                else await _window.RevertOptimization(_opt, this);
            };
            Grid.SetColumn(_toggle, 1);
            grid.Children.Add(_toggle);
        }

        outer.Children.Add(grid);

        // v1-style general status, top-right corner; never interactive, never merged with metadata.
        _status = Ui.StatusBox("", Ui.StatusAvailableText, Ui.StatusAvailableBg);
        _status.Visibility = Visibility.Collapsed;
        _status.IsHitTestVisible = false;
        outer.Children.Add(_status);

        Child = outer;
        Update(detection);
    }

    private static (string Label, SolidColorBrush Fg, SolidColorBrush Bg)? StatusInfo(Detection d) => d.State switch
    {
        OptState.Optimized => ("Active", Ui.StatusActiveText, Ui.StatusActiveBg),
        // A committed transaction whose recorded values no longer match is not simply "needs
        // attention" — the feature is partially configured or the system was changed externally.
        OptState.NeedsAttention when d.Reason == "E-FOREIGN-EDIT"
            => ("Incomplete", Ui.StatusAttentionText, Ui.StatusAttentionBg),
        OptState.NeedsAttention when d.Reason == "E-JOURNAL-CORRUPT"
            => ("Unverifiable", Ui.StatusAttentionText, Ui.StatusAttentionBg),
        OptState.NeedsAttention => ("Needs attention", Ui.StatusAttentionText, Ui.StatusAttentionBg),
        OptState.Unknown when d.Selectable => ("Available", Ui.StatusAvailableText, Ui.StatusAvailableBg),
        _ => null,
    };

    /// <summary>
    /// Shows an in-progress state on the card and disables its control while an apply/restore
    /// runs. <see cref="Update"/> always restores the real state afterwards.
    /// </summary>
    public void SetBusy()
    {
        if (_toggle is not null) _toggle.IsEnabled = false;
        if (_run is not null) _run.IsEnabled = false;
        _state.Visibility = Visibility.Visible;
        _state.Text = "Working…";
        _state.Foreground = Ui.Accent;
    }

    private static bool IsGenericStateLine(string current, string reason)
        => reason.Length == 0 && GenericStates.Contains(current.Trim());

    public void Update(Detection d)
    {
        _last = d;
        var status = StatusInfo(d);
        var statusText = (TextBlock)_status.Child;
        if (status is null)
        {
            _status.Visibility = Visibility.Collapsed;
            _head.Margin = new Thickness(0);
        }
        else
        {
            _status.Visibility = Visibility.Visible;
            _status.Background = status.Value.Bg;
            statusText.Text = status.Value.Label;
            statusText.Foreground = status.Value.Fg;
            // Keep the title/metadata row from sliding underneath the status pill.
            _head.Margin = new Thickness(0, 0, 106, 0);
        }

        if (IsGenericStateLine(d.CurrentState, d.Reason))
        {
            _state.Visibility = Visibility.Collapsed;
            _state.Text = "";
        }
        else
        {
            _state.Visibility = Visibility.Visible;
            _state.Text = d.CurrentState + (d.Reason.Length > 0 ? " — " + d.Reason : "");
        }
        _state.Foreground = d.State switch
        {
            OptState.Optimized => Ui.Good,
            OptState.NeedsAttention => Ui.Warn,
            OptState.Manual or OptState.Unsupported => Ui.TextFaint,
            _ => Ui.TextDim,
        };
        if (_toggle is not null)
        {
            _toggle.IsEnabled = d.Selectable && d.State is OptState.Optimized or OptState.NeedsAttention or OptState.Unknown;
            _toggle.SetState(d.State == OptState.Optimized);
        }
        if (_run is not null)
            _run.IsEnabled = d.Selectable && d.State != OptState.Optimized;
    }

    /// <summary>Re-detects the live state off-thread and refreshes this card.</summary>
    public async Task Refresh()
    {
        Detection d;
        try { d = await Task.Run(() => _opt.Detect()); }
        catch (Exception ex) { d = new Detection(OptState.Unknown, "Detection failed: " + ex.Message, false, ""); }
        Update(d);
    }
}
