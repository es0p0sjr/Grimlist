using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GrimList.Core;

namespace GrimList.Desktop;
internal static partial class SmokeChecks
{
    private static void CheckAllies(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var fixture = Path.Combine(root, "allies-fixture.json");
        if (!File.Exists(fixture)) throw new Exception("Allies UI fixture missing; run core tests first.");
        var testDirectory = Path.Combine(root, "allies-ui");
        using (var store = WindowsServices.OpenStore(testDirectory))
        {
            store.Import(DataJson.Read<Dataset>(File.ReadAllText(fixture)));
            store.Save(ArmyList.New("Death Guard") with { Name = "Allies UI fixture", Detachments = ["Tallyband Summoners"] });
        }
        var window = new MainWindow(testDirectory) { Owner = owner };
        window.Show();
        try
        {
            // Exercise the same roster-opening path as the home screen.
            using var store = WindowsServices.OpenStore(testDirectory);
            typeof(MainWindow).GetMethod("OpenRoster", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [store.LoadArmies().Single()]);
            var picker = Find<ListBox>(window, "UnitPicker");
            var view = (ListCollectionView)picker.ItemsSource;
            var categories = view.Groups!.Cast<CollectionViewGroup>().Select(g => (string)g.Name).ToList();
            Check(categories.Contains("Dedicated Transports") && categories.Contains("Allies"), "New library groups missing");
            var dog = picker.Items.Cast<UnitDefinition>().Single(u => u.Name == "Fixture War Dog");
            Check(!picker.Items.Cast<UnitDefinition>().Any(u => u.Faction == "Orks"), "Unrelated faction selectable");
            picker.SelectedItem = dog; Click(window, "AddButton");
            Check(store.LoadArmies().Single().Entries.Count == 1, "Adding ally did not persist");
            var roster = (ListCollectionView)Find<ListBox>(window, "Roster").ItemsSource;
            Check(roster.Groups!.Cast<CollectionViewGroup>().Single().Name.Equals("Allies"), "Roster ally group missing");
            Check(Find<TextBlock>(window, "AlliesSummaryText").Text.Contains("150 pts"), "Allied subtotal missing");
            Check(Find<TextBlock>(window, "AllyHintText").Text.Contains("Dreadblades"), "Ally rule hint missing");
            Click(window, "UndoButton"); Check(store.LoadArmies().Single().Entries.Count == 0, "Undo ally failed");
            Click(window, "RedoButton"); Check(store.LoadArmies().Single().Entries.Count == 1, "Redo ally failed");
            Capture(window, imagePath + ".allies.png");
        }
        finally { window.Close(); }
    }
}
