using System.Collections;
using System.IO;
using System.Windows;
using SwiftwaveTweaks.Core;

namespace SwiftwaveTweaks.Tests;

/// <summary>
/// Headless UI integration test. Constructs the real <see cref="MainWindow"/> and the real page
/// trees for Gaming, System, Power, Storage and Network, then verifies that every V3 feature is
/// present on exactly the page its Category targets, that retired cards never render, and that
/// every card carries a description and a legal detected state. It does not click controls and
/// does not apply anything — interactive apply/restore still requires an elevated session.
/// Invoke with <c>--ui-selftest</c>; results go to %LocalAppData%\SwiftwaveTweaks\ui-selftest.txt.
/// </summary>
internal static class UiSelfTest
{
    private static readonly List<string> Lines = new();
    private static int _pass, _fail;

    private static readonly string[] FeatureIds =
    [
        "v3-fps-input-lag", "v3-device-affinity", "v3-msi", "v3-network-performance",
        "v3-dynamic-interrupt-steering", "v3-storage-memory", "debloat", "v3-max-performance-power",
    ];

    public static int Run()
    {
        Lines.Clear(); _pass = 0; _fail = 0;

        try
        {
            // Only InitializeComponent and constructor work: no handle, no tray, no page load.
            var window = new MainWindow();
            var catalog = OptimizationCatalog.Build(null);
            var detected = catalog.Select(o =>
            {
                try { return (Opt: o, Detection: o.Detect()); }
                catch (Exception ex)
                {
                    return (Opt: o, Detection: new Detection(OptState.Unknown, "Detection failed: " + ex.Message, false, ""));
                }
            }).ToList();

            var pages = window.BuildPagesForTest(detected);
            var perPage = new Dictionary<string, List<OptimizationCard>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, root) in pages)
            {
                var cards = new List<OptimizationCard>();
                Collect(root, cards);
                perPage[key] = cards;
                Lines.Add($"[ui] page '{key}': {cards.Count} card(s) — " +
                          string.Join(", ", cards.Select(c => c.OptId)));
            }

            foreach (var (key, cards) in perPage)
            {
                Check($"ui: '{key}' page renders at least one card", cards.Count > 0, $"{cards.Count}");
                foreach (var card in cards)
                {
                    Detection? cardDetection = card.Current;
                    Check($"ui: '{key}' card {card.OptId} has a description",
                        !string.IsNullOrWhiteSpace(card.OptName), null);
                    Check($"ui: '{key}' card {card.OptId} has a legal detected state",
                        cardDetection is not null && Enum.IsDefined(cardDetection.State),
                        cardDetection?.State.ToString());
                    Check($"ui: '{key}' card {card.OptId} is not a retired card",
                        catalog.First(x => x.Id == card.OptId).Category != "Legacy", null);
                }
            }

            CheckPage(perPage, "windows",
                ["v3-fps-input-lag", "debloat", "v3-device-affinity", "v3-msi", "v3-dynamic-interrupt-steering"],
                ["power-plan", "v3-storage-memory", "v3-max-performance-power"]);
            CheckPage(perPage, "storage",
                ["v3-storage-memory"],
                ["debloat", "power-plan"]);
            CheckPage(perPage, "power",
                ["v3-max-performance-power"],
                ["power-plan", "v3-storage-memory", "debloat"]);
            CheckPage(perPage, "network",
                ["v3-network-performance"],
                ["v3-fps-input-lag", "v3-msi"]);

            // Explicit card order on the Windows page: FPS → Debloat → Device Affinities.
            if (perPage.TryGetValue("windows", out var winCards))
            {
                int fps = winCards.FindIndex(c => c.OptId == "v3-fps-input-lag");
                int debloat = winCards.FindIndex(c => c.OptId == "debloat");
                int affinity = winCards.FindIndex(c => c.OptId == "v3-device-affinity");
                Check("ui: 'windows' orders FPS → Debloat → Device Affinities",
                    fps >= 0 && debloat > fps && affinity > debloat,
                    $"fps={fps} debloat={debloat} affinity={affinity}");
            }
            else
            {
                Fail("ui: 'windows' page exists", "page was not built");
            }

            foreach (var id in FeatureIds)
            {
                int pagesWithFeature = perPage.Count(p => p.Value.Any(c => c.OptId == id));
                Check($"ui: {id} appears on exactly one page", pagesWithFeature == 1, $"{pagesWithFeature} page(s)");
            }

            window.Close();
        }
        catch (Exception ex)
        {
            Fail("harness", ex.ToString());
        }

        string report = $"UI self-test: {_pass} passed, {_fail} failed{Environment.NewLine}" +
                        string.Join(Environment.NewLine, Lines);
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftwaveTweaks");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "ui-selftest.txt"), report);
        }
        catch { }
        return _fail == 0 ? 0 : 1;
    }

    private static void CheckPage(
        Dictionary<string, List<OptimizationCard>> perPage,
        string pageName,
        string[] required,
        string[] forbidden)
    {
        if (!perPage.TryGetValue(pageName, out var cards))
        {
            Fail($"ui: page '{pageName}' exists", "page was not built");
            return;
        }

        foreach (var id in required)
        {
            int count = cards.Count(c => c.OptId == id);
            Check($"ui: '{pageName}' shows {id} exactly once", count == 1, $"count={count}");
        }

        foreach (var id in forbidden)
            Check($"ui: '{pageName}' does not show {id}", cards.All(c => c.OptId != id), null);
    }

    /// <summary>Walks the logical tree (card contents are not in a visual tree until rendered).</summary>
    private static void Collect(object? node, List<OptimizationCard> cards)
    {
        if (node is OptimizationCard card) { cards.Add(card); return; }
        if (node is not DependencyObject dep) return;
        IEnumerable children;
        try { children = LogicalTreeHelper.GetChildren(dep); }
        catch { return; }
        foreach (var child in children) Collect(child, cards);
    }

    private static void Check(string name, bool ok, string? detail)
    {
        if (ok) { _pass++; Lines.Add($"PASS  {name}"); }
        else { _fail++; Lines.Add($"FAIL  {name}" + (detail is null ? "" : $" — {detail}")); }
    }

    private static void Fail(string name, string detail)
    {
        _fail++;
        Lines.Add($"FAIL  {name} — {detail}");
    }
}
