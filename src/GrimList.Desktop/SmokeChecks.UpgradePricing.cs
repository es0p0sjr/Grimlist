using System.IO;
using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;
internal static partial class SmokeChecks
{
    private static void CheckUpgradePricing(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var upgradesPath = Path.Combine(root, "upgrade-fixture.json"); var pricingPath = Path.Combine(root, "pricing-fixture.json");
        if (!File.Exists(upgradesPath) || !File.Exists(pricingPath)) return;
        var upgrades = DataJson.Read<Dataset>(File.ReadAllText(upgradesPath));
        var pricing = DataJson.Read<Dataset>(File.ReadAllText(pricingPath));
        var path = Path.Combine(root, "upgrade-pricing-ui");
        using var store = WindowsServices.OpenStore(path);
        store.Import(upgrades with { Units = [.. upgrades.Units, .. pricing.Units] });
        var data = store.LoadDataset()!;
        var troops = data.Units.Single(u => u.Name == "Upgrade troops"); var defiler = data.Units.Single(u => u.Name == "Defiler");
        var army = ArmyList.New("Death Guard") with { Name = "Upgrades and paid wargear", Detachment = "Test host", Entries = [
            .. Enumerable.Range(0, 4).Select(i => new ArmyEntry("troop-" + i, troops.Key, troops.Name, 1)),
            .. Enumerable.Range(0, 2).Select(i => new ArmyEntry("defiler-" + i, defiler.Key, defiler.Name, 1, Loadout: Loadouts.Create(defiler, 1)))] };
        store.Save(army);
        var window = new MainWindow(path) { Owner = owner, ShowInTaskbar = false }; string? failure = null;
        window.ContentRendered += (_, _) =>
        {
            try
            {
                Find<WrapPanel>(Find<HomeView>(window, "Home"), "TilePanel").Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Single(b => b.Tag is ArmyList)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var roster = Find<ListBox>(window, "Roster");
                ArmyList Saved() => store.LoadArmies().Single();
                Button UpgradeButton()
                {
                    window.UpdateLayout();
                    return Descendants<Button>(Find<ItemsControl>(window, "UnitEnhancementChoices")).Single(b => b.Tag is EnhancementSelection s && s.Name == "Infantry blessing");
                }
                for (var i = 0; i < 3; i++)
                {
                    roster.SelectedValue = "troop-" + i;
                    Check(UpgradeButton().IsEnabled, "Eligible regular unit could not take upgrade");
                    UpgradeButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                Check(Enhancements.Usage(Saved(), data) == (1, 3), "UI upgrade allowance did not count shared choice");
                roster.SelectedValue = "troop-3";
                Check(!UpgradeButton().IsEnabled, "UI allowed fourth upgrade");
                roster.SelectedValue = "troop-2";
                Capture(window, imagePath + ".upgrades.png");
                Click(window, "DuplicateEntryButton");
                Check(Saved().Entries.Count == 6 && Find<Border>(window, "ErrorBanner").Visibility == Visibility.Visible
                    && Find<TextBlock>(window, "ErrorMessage").Text.Contains("Upgrade limit"), "Duplicating upgraded unit bypassed army cap");
                roster.SelectedValue = "troop-2"; Click(window, "PlayModeButton");
                Check(Find<TextBlock>(window, "SelectedEnhancementRule").Text == upgrades.ReferenceCards!.Single(c => c.Name == "Infantry blessing").Text, "Play mode added unrelated upgrade reminder text");
                Click(window, "PlayModeButton");
                roster.SelectedValue = "defiler-1"; window.UpdateLayout();
                Check(Find<TextBlock>(window, "UnitPointsBreakdown").Text.Contains("Copy 2: +50 pts"), "Copy tax missing from unit breakdown");
                var paid = Descendants<Button>(Find<StackPanel>(window, "LoadoutCards")).Where(b => b.Content is TextBlock t && t.Text.Contains("Hades lascannon") && t.Text.Contains("+15 pts")).ToList();
                Check(paid.Count == 2, "Paid prices missing from the two weapon buttons");
                paid[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var result = ArmyCalculator.Calculate(Saved(), data);
                Check(result.Entries.Single(e => e.Entry.Id == "defiler-1").Points == 365, "UI did not add selected weapon to taxed copy");
                Check(Find<TextBlock>(window, "UnitPointsBreakdown").Text.Contains("Wargear +15 pts"), "Selected wargear subtotal not shown");
                Capture(window, imagePath + ".paid-wargear.png");
                Click(window, "UndoButton");
                Check(ArmyCalculator.Calculate(Saved(), data).Entries.Single(e => e.Entry.Id == "defiler-1").Points == 350, "Undo did not remove equipment cost");
                Click(window, "RedoButton");
                Check(ArmyCalculator.Calculate(Saved(), data).Entries.Single(e => e.Entry.Id == "defiler-1").Points == 365, "Redo did not restore equipment cost");
                var firstItem = roster.Items.Cast<object>().Single(o => (string?)o.GetType().GetProperty("Id")?.GetValue(o) == "defiler-0");
                roster.ScrollIntoView(firstItem); window.UpdateLayout();
                var firstRow = Descendants<Button>(roster).Single(b => b.Tag is string id && id == "defiler-0");
                firstRow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(ArmyCalculator.Calculate(Saved(), data).Entries.Single(e => e.Entry.Id == "defiler-1").Points == 315, "Removing first copy left a stale tax");
            }
            catch (Exception ex) { failure = ex.ToString(); }
            finally { window.Close(); }
        };
        window.ShowDialog(); Check(failure == null, failure ?? "");
    }
}
