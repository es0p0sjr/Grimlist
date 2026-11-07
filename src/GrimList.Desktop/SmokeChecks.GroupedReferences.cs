using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    private static void CheckGroupedReferences(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var fixture = Path.Combine(root, "ability-groups-fixture.json");
        if (!File.Exists(fixture)) return;
        using var store = WindowsServices.OpenStore(Path.Combine(root, "ability-groups-ui"));
        store.Import(DataJson.Read<Dataset>(File.ReadAllText(fixture)));
        var data = store.LoadDataset()!; var unit = data.Units.Single();
        var other = unit with { Name = "Standalone ability fixture", Id = "standalone", Aliases = [], Profile = new(Abilities: [new("Standalone", "Independent rule")]) };
        store.ApplyImport(data with { Units = [unit, other] }); data = store.LoadDataset()!;
        var army = ArmyList.New(unit.Faction) with { Name = "Grouped abilities", Entries = [
            new("parent", unit.Key, unit.Name, 1, Loadout: Loadouts.Create(unit, 1)), new("other", other.Key, other.Name, 1)] };
        store.Save(army);
        var window = new MainWindow(store.DirectoryPath) { Owner = owner, ShowInTaskbar = false }; window.Show();
        try
        {
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
            void Select(string id) { Find<ListBox>(window, "Roster").SelectedValue = id; window.UpdateLayout(); }
            void Grouped()
            {
                var control = Find<ItemsControl>(window, "AbilityCards");
                var parent = control.Items.Cast<AbilityCard>().Single(c => c.Name == "Lord of the Death Guard");
                Check(parent.Options.Count == 3, "Parent ability lost its options");
                Check(parent.Options.All(c => Descendants<TextBlock>(control).Any(t => t.Text == c.Name)), "Nested option names not rendered");
                Check(parent.Options.All(c => Descendants<TextBlock>(control).Any(t => t.Text == c.Text)), "Nested option descriptions not rendered");
            }
            Select("parent"); Grouped(); Capture(window, imagePath + ".grouped-abilities.png");
            Click(window, "PlayModeButton"); window.UpdateLayout(); Grouped();
            Capture(window, imagePath + ".grouped-abilities-play.png");
            Select("other");
            Check(Find<ItemsControl>(window, "AbilityCards").Items.Cast<AbilityCard>().Single().Options.Count == 0, "Switching units kept another unit's options");
            Select("parent"); Grouped();
            Click(window, "RulesButton"); window.UpdateLayout();
            var core = Find<ItemsControl>(window, "CoreStratagemCards");
            Check(core.Items.Count == 10, "Core stratagems missing without detachment");
            CheckStratagemColours(core);
            var textBlocks = Descendants<TextBlock>(core).ToList();
            foreach (var card in core.Items.Cast<ReferenceCard>())
                Check(textBlocks.Any(t => t.Text == card.Text && t.Text.Contains("TARGET:") && t.Text.Contains("EFFECT:")), "Full core rule not rendered");
            Check(!textBlocks.Any(t => t.Inlines.OfType<System.Windows.Documents.Hyperlink>().Any() || t.Text.Contains("REMINDER:")), "Core cards still show source links or reminders");
            core.BringIntoView(new Rect(0, 0, core.ActualWidth, 700)); window.UpdateLayout();
            Capture(window, imagePath + ".core-stratagems.png");
            File.WriteAllBytes(imagePath + ".grouped-references.pdf", RosterPdf.Create(army, data));
        }
        finally { window.Close(); }
    }
}
