using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        ("Dashboard", "dashboard"), ("Recommended", "recommended"), ("Gaming", "gaming"),
        ("NVIDIA", "nvidia"), ("Power", "power"), ("Storage", "storage"), ("Network", "network"),
        ("Startup", "startup"), ("Cleanup", "cleanup"), ("Diagnostics", "diagnostics"),
        ("History & Restore", "history"), ("Settings", "settings"),
    ];

    private UserPreferences _prefs;
    private TrayIcon? _tray;
    private bool _allowClose;
    private int _navToken;
    private CaptionButton? _maxButton;

    private Task<SystemSnapshot>? _snapshotTask;
    private Task<NvidiaInfo>? _nvidiaTask;
    private List<Optimization>? _catalog;

    public MainWindow()
    {
        InitializeComponent();
        _prefs = PreferencesStore.Read();

        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = "v" + MajorMinorVersion(info);

        BuildCaptionButtons();
        UpdateAdminStatus();

        Loaded += OnLoaded;
        StateChanged += (_, _) => _maxButton?.SetAlt(WindowState == WindowState.Maximized);
    }

    /// <summary>"2.0.0" → "2.0" — the visible footer reads v2.0, derived from the project version.</summary>
    internal static string MajorMinorVersion(string? informational)
    {
        if (informational is not null && Version.TryParse(informational.Split('+')[0], out var v))
            return $"{v.Major}.{v.Minor}";
        return informational ?? "2.0";
    }

    /// <summary>"2.0.0" → "2" — short form used by the About card.</summary>
    internal static string ShortVersion(string? informational)
    {
        if (informational is not null && Version.TryParse(informational.Split('+')[0], out var v))
            return v.Minor == 0 && v.Build <= 0 ? v.Major.ToString() : $"{v.Major}.{v.Minor}";
        return informational ?? "2";
    }

    internal static string FullVersion()
        => Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "2.0.0";

    // ───────────────────────────── window chrome ─────────────────────────────

    private void BuildCaptionButtons()
    {
        var min = new CaptionButton("Minimize", "M 1 5 H 9");
        min.Clicked += (_, _) => WindowState = WindowState.Minimized;

        _maxButton = new CaptionButton("Maximize", "M 1 1 H 9 V 9 H 1 Z", "M 3 1 H 9 V 7 H 7 M 1 3 H 7 V 9 H 1 Z");
        _maxButton.Clicked += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        var close = new CaptionButton("Close", "M 1 1 L 9 9 M 9 1 L 1 9", danger: true);
        close.Clicked += (_, _) => Close();

        CaptionButtons.Children.Add(min);
        CaptionButtons.Children.Add(_maxButton);
        CaptionButtons.Children.Add(close);
        // Dragging, resizing, double-click maximize and Windows Snap are provided by the
        // WindowChrome caption area / resize border declared in MainWindow.xaml.
    }

    private void UpdateAdminStatus()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            bool admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            AdminStatus.Text = admin ? "· administrator" : "";
        }
        catch { AdminStatus.Text = ""; }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _tray = new TrayIcon(this, "swiftwave tweaks.",
            restore: () => Dispatcher.Invoke(() =>
            {
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
        _tray?.Dispose();
        base.OnClosing(e);
    }

    /// <summary>
    /// Invoked on the first instance when a second launch signals over the single-instance
    /// pipe. Unhides (tray), restores (minimized) and foregrounds the existing window.
    /// Creates nothing and never replays the welcome sequence (which only runs from OnLoaded).
    /// </summary>
    internal void ActivateFromSecondInstance()
    {
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

        if (Motion.ReducedMotion)
        {
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
        if (_prefs.MinimizeOnStartup)
            WindowState = WindowState.Minimized;

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
        try
        {
            content = key switch
            {
                "dashboard" => BuildDashboard(await EnsureSnapshot()),
                "recommended" => BuildRecommended(await DetectCatalog()),
                "gaming" => BuildCategory("gaming", await DetectCatalog()),
                "nvidia" => BuildNvidia(await EnsureNvidia(), (await DetectCatalog()).First(o => o.Opt.Category == "NVIDIA").Opt),
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
    }

    private Task<SystemSnapshot> EnsureSnapshot() => _snapshotTask ??= SystemDetector.CollectAsync();
    private Task<NvidiaInfo> EnsureNvidia() => _nvidiaTask ??= Task.Run(NvidiaService.Query);

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

    private FrameworkElement BuildDashboard(SystemSnapshot s)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.CardPanel(BuildPcSpecs(s)));

        var gaming = new StackPanel();
        gaming.Children.Add(Ui.Text("Gaming-relevant state", 13.5, Ui.TextWhite, FontWeights.SemiBold));
        var stateGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        stateGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        stateGrid.ColumnDefinitions.Add(new ColumnDefinition());
        (string Label, string Value)[] states =
        [
            ("Game Mode", s.GameModeState == 0 ? "disabled" : "enabled (recommended)"),
            ("Game Bar overlay", s.GameBarState == 0 ? "disabled" : "enabled"),
            ("Game DVR recording", s.GameDvrPolicyPresent ? "disabled (system policy)" : s.GameDvrUserState == 0 ? "disabled" : "enabled"),
            ("HAGS", s.HagsMode == 2 ? "enabled" : s.HagsMode == 1 ? "disabled" : "not configured (default)"),
        ];
        for (int i = 0; i < states.Length; i++)
        {
            stateGrid.RowDefinitions.Add(new RowDefinition());
            var label = Ui.Text(states[i].Label, 12.5, Ui.TextMuted, margin: new Thickness(0, 2, 0, 2));
            var value = Ui.Text(states[i].Value, 12.5, Ui.TextDim, margin: new Thickness(0, 2, 0, 2));
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            stateGrid.Children.Add(label);
            stateGrid.Children.Add(value);
        }
        gaming.Children.Add(stateGrid);
        panel.Children.Add(Ui.CardPanel(gaming));

        panel.Children.Add(Ui.CardPanel(Ui.Text(
            "Open Recommended for the safe one-click set, or review each section from the sidebar. " +
            "Every change is detected live, verified after applying, and restorable from History & Restore.",
            12.5, Ui.TextMuted)));
        return panel;
    }

    /// <summary>
    /// "Your PC" heading plus secondary category labels over the live-detected values.
    /// Data, ordering and the v2 card layout are unchanged from ToDisplayText(); only the
    /// typographic hierarchy is new.
    /// </summary>
    private FrameworkElement BuildPcSpecs(SystemSnapshot s)
    {
        var ram = s.RamModules.Count == 0
            ? $"{s.MemoryGb:N0} GB total"
            : $"{s.MemoryGb:N0} GB total across {s.RamModules.Count} module(s)\n" +
              string.Join("\n", s.RamModules.Select(m =>
                  $"{m.Slot}: {m.CapacityGb:N0} GB @ {m.ConfiguredSpeedMts?.ToString() ?? "?"} MT/s" +
                  (m.SpeedMts is int rated && m.ConfiguredSpeedMts is int cfg && cfg < rated ? $" (rated {rated} MT/s)" : "")));
        var drives = s.Drives.Count == 0 ? "No fixed drives detected." : string.Join("\n", s.Drives.Select(d =>
            $"{d.Letter} {d.VolumeLabel,-12} {d.BusKind,-5} {d.FreeGb,6:N0} GB free of {d.SizeGb,6:N0} GB — {d.Model}"));
        var monitors = s.Monitors.Count == 0 ? "Display information unavailable." : string.Join("\n", s.Monitors.Select(m =>
            $"{m.Device}: {m.Width}x{m.Height} @ {m.CurrentHz} Hz" + (m.MaxHzAtCurrentRes > m.CurrentHz ? $" (supports up to {m.MaxHzAtCurrentRes} Hz)" : "")));

        (string Label, string Value)[] sections =
        [
            ("Windows", s.WindowsLine),
            ("Processor", $"{s.CpuName} — {s.CpuCores} cores / {s.CpuThreads} threads"),
            ("Graphics", $"{s.GpuName}\nDriver {s.GpuDriver}"),
            ("Memory", ram),
            ("Displays", monitors),
            ("Active power plan", s.PowerPlan),
            ("Storage", drives),
            ("Processes running", s.ProcessCount.ToString()),
        ];

        var box = new StackPanel();
        box.Children.Add(Ui.Text("Your PC", 16, Ui.TextWhite, FontWeights.SemiBold,
            margin: new Thickness(0, 0, 0, 10)));
        for (int i = 0; i < sections.Length; i++)
        {
            box.Children.Add(new TextBlock
            {
                Text = sections[i].Label,
                FontSize = 11,
                Foreground = Ui.TextMuted,
                Margin = new Thickness(0, i == 0 ? 0 : 9, 0, 2)
            });
            box.Children.Add(new TextBlock
            {
                Text = sections[i].Value,
                FontSize = 12.5,
                Foreground = Ui.TextDim,
                LineHeight = 19,
                TextWrapping = TextWrapping.Wrap
            });
        }
        return box;
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
        ReloadCurrentPage();
    }

    private FrameworkElement BuildCategory(string key, List<(Optimization Opt, Detection Detection)> detected)
    {
        var panel = new StackPanel();
        switch (key)
        {
            case "gaming":
                panel.Children.Add(Ui.SectionHeader("General"));
                foreach (var d in detected.Where(x => x.Opt.Id == "fortnite-affinity"))
                    panel.Children.Add(new OptimizationCard(d.Opt, d.Detection, this, roomy: true));
                panel.Children.Add(Ui.SectionHeader("Windows Background & Debloat"));
                foreach (var d in detected.Where(x => x.Opt.Id == "windows-suggestions"))
                    panel.Children.Add(new OptimizationCard(d.Opt, d.Detection, this));
                panel.Children.Add(Ui.SectionHeader("Gaming"));
                foreach (var d in detected.Where(x => x.Opt.Category == "Gaming" && x.Opt.Id is not ("fortnite-affinity" or "windows-suggestions")))
                    panel.Children.Add(new OptimizationCard(d.Opt, d.Detection, this, roomy: true));
                panel.Children.Add(Ui.SectionHeader("Visual effects"));
                foreach (var d in detected.Where(x => x.Opt.Category == "Visual"))
                    panel.Children.Add(new OptimizationCard(d.Opt, d.Detection, this, roomy: true));
                break;
            case "power":
                panel.Children.Add(Ui.Text(
                    "Only supported, verifiable Windows power policies are exposed. No forced minimum processor state, " +
                    "no forced maximum frequency, no global core-parking changes, no BIOS or voltage modifications.",
                    12.5, Ui.TextMuted, margin: new Thickness(2, 0, 0, 10)));
                foreach (var d in detected.Where(x => x.Opt.Category == "Power"))
                    panel.Children.Add(new OptimizationCard(d.Opt, d.Detection, this));
                break;
            case "storage":
                panel.Children.Add(Ui.Text(
                    "Windows Optimize Drives — TRIM/retrim on SSD and NVMe media, defragmentation on mechanical drives. " +
                    "The correct method is chosen automatically per drive.",
                    12.5, Ui.TextMuted, margin: new Thickness(2, 0, 0, 10)));
                foreach (var d in detected.Where(x => x.Opt.Category == "Storage"))
                    panel.Children.Add(new OptimizationCard(d.Opt, d.Detection, this, roomy: true));
                break;
        }
        return panel;
    }

    private FrameworkElement BuildNvidia(NvidiaInfo info, Optimization powerOpt)
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

        Detection detection;
        try { detection = powerOpt.Detect(); }
        catch (Exception ex) { detection = new Detection(OptState.Unsupported, "Detection failed: " + ex.Message, false, ""); }
        panel.Children.Add(new OptimizationCard(powerOpt, detection, this));
        return panel;
    }

    private async Task<FrameworkElement> BuildNetwork()
    {
        // Detection runs entirely off the UI thread (the original freeze came from synchronous
        // detection); LoadPage's navigation token already guards against stale results.
        var reading = await NetworkAdapterService.ReadAsync();
        var panel = new StackPanel();
        if (!reading.Present)
        {
            panel.Children.Add(Ui.CardPanel(Ui.Text(
                "No active wired network adapter was detected. Network energy-saving controls are unavailable " +
                "on wireless or disconnected adapters.",
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

        var opt = (await DetectCatalog()).First(d => d.Opt.Id == "network-power-saving");
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

        panel.Children.Add(Ui.SectionHeader("Preferences"));
        panel.Children.Add(PreferenceRow("Start minimized",
            "The window starts minimized after the welcome sequence.",
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
        var result = await Task.Run(() => opt.Apply());
        StatusText.Text = result.Message + (result.RestartRequired ? " A restart is required." : "");
        SetBusy(false);
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
        var result = await Task.Run(() => opt.Rollback(entry.PreviousState));
        if (result.Status == ApplyStatus.Applied) RollbackStore.Remove(opt.Id);
        StatusText.Text = result.Message + (result.RestartRequired ? " A restart is required." : "");
        SetBusy(false);
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
        OptState.NeedsAttention => ("Needs attention", Ui.StatusAttentionText, Ui.StatusAttentionBg),
        OptState.Unknown when d.Selectable => ("Available", Ui.StatusAvailableText, Ui.StatusAvailableBg),
        _ => null,
    };

    private static bool IsGenericStateLine(string current, string reason)
        => reason.Length == 0 && GenericStates.Contains(current.Trim());

    public void Update(Detection d)
    {
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
