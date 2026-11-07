using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    private static void CheckEnhancementDefences(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var path = Path.Combine(root, "enhancement-defence-fixture.json");
        if (!File.Exists(path)) return;
        var fixture = DataJson.Read<Dataset>(File.ReadAllText(path));
        var open = typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var (faction, name, enhancement) in new[] { ("Death Guard", "Daemon Prince of Nurgle", "Revolting Regeneration"),
            ("Adeptus Custodes", "Shield-Captain", "Augury Uplink") })
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "defence-ui-" + faction));
            store.Import(fixture); var data = store.LoadDataset()!;
            var unit = data.Units.Single(u => u.Faction == faction && u.Name == name);
            var card = data.ReferenceCards!.Single(c => c.Faction == faction && c.Section == "Enhancements" && c.Name == enhancement);
            var army = ArmyList.New(faction) with { Name = "Enhancement FNP UI", Detachments = [card.Detachment], Entries =
                [new("bearer", unit.Key, name, 1, Loadout: Loadouts.Create(unit, 1)), new("other", unit.Key, name, 1, Loadout: Loadouts.Create(unit, 1))] };
            store.Save(army);
            ArmyList Saved() => store.LoadArmies().Single();
            var window = new MainWindow(store.DirectoryPath) { Owner = owner }; window.Show();
            void Select(string id) { Find<ListBox>(window, "Roster").SelectedValue = id; window.UpdateLayout(); }
            void Choose()
            {
                window.UpdateLayout();
                var button = Descendants<Button>(Find<ItemsControl>(window, "UnitEnhancementChoices"))
                    .Single(b => b.Tag is EnhancementSelection selection && selection.Name == enhancement);
                Check(button.IsEnabled, "Imported FNP enhancement choice disabled"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            void HasFnp(bool expected)
            {
                Check(Find<Button>(window, "FeelNoPainPanel").IsVisible == expected, "Wrong FNP visibility after enhancement change: " + faction);
                if (expected) Check(Find<TextBlock>(window, "FeelNoPainValue").Text == "5+++ FNP"
                    && Find<Button>(window, "FeelNoPainPanel").ToolTip.ToString()!.Contains(enhancement), "Missing enhancement FNP value or source");
            }
            try
            {
                open.Invoke(window, [army]); Select("bearer"); HasFnp(false);
                Choose(); HasFnp(true); Check(Saved().Entries[0].Enhancement?.Name == enhancement, "Enhancement not persisted");
                Select("other"); HasFnp(false); Select("bearer"); HasFnp(true);
                Click(window, "UndoButton"); Select("bearer"); HasFnp(false);
                Click(window, "RedoButton"); Select("bearer"); HasFnp(true);
                Choose(); HasFnp(false); Choose(); HasFnp(true);
                Click(window, "PlayModeButton"); Select("bearer"); HasFnp(true);
                Capture(window, imagePath + ".enhancement-fnp-" + faction + ".png");
                Click(window, "PlayModeButton");
            }
            finally { window.Close(); }
            var reopened = new MainWindow(store.DirectoryPath) { Owner = owner }; reopened.Show();
            try
            {
                open.Invoke(reopened, [Saved()]); Find<ListBox>(reopened, "Roster").SelectedValue = "bearer"; reopened.UpdateLayout();
                Check(Find<TextBlock>(reopened, "FeelNoPainValue").Text == "5+++ FNP" && Find<Button>(reopened, "FeelNoPainPanel").IsVisible,
                    "Restored enhancement did not restore FNP badge");
            }
            finally { reopened.Close(); }
        }
        File.WriteAllText(imagePath + ".enhancement-fnp.txt", "PASS: real Daemon Prince/Revolting Regeneration and Shield-Captain/Augury Uplink; actual enhancement buttons; select/remove, undo/redo, separate roster entries, source attribution, Play mode and reopen.");
    }
}
