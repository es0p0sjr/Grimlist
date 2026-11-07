using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckStatlines(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        using var store = WindowsServices.OpenStore(Path.Combine(root, "statlines-ui-" + Guid.NewGuid().ToString("N")));
        Dictionary<string, string> Stats(string wounds) => new() { ["M"] = "6\"", ["T"] = "4", ["Sv"] = "6+", ["W"] = wounds, ["LD"] = "7+", ["OC"] = "1" };
        var mixed = new UnitDefinition("Chaos Space Marines", "Accursed Cultists", [new(8, 90)], Profile: new(
            Keywords: ["Infantry"], Models: [new("Torment", Stats("3")), new("Mutant", Stats("1"))]));
        var same = mixed with { Name = "Identical profiles", Profile = mixed.Profile! with { Models = [new("Trooper", Stats("1")), new("Champion", Stats("1"))] } };
        store.Import(new(new("Statline UI fixture", "1", "", "", Edition: "11"), [mixed, same]));
        var data = store.LoadDataset()!;
        var army = ArmyList.New(mixed.Faction) with { Name = "Distinct model statlines", Entries = data.Units.Select(u => new ArmyEntry(u.Name, u.Key, u.Name, 8)).ToList() };
        store.Save(army);
        var window = new MainWindow(store.DirectoryPath) { Owner = owner, ShowInTaskbar = false }; window.Show();
        try
        {
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
            Find<ListBox>(window, "Roster").SelectedValue = mixed.Name; window.UpdateLayout();
            var primary = Find<ItemsControl>(window, "Stats"); var extra = Find<ItemsControl>(window, "ModelCards");
            Check(primary.Items.Count == 6 && primary.Items.Cast<EffectiveCharacteristic>().Single(s => s.Key == "W").Value == "3", "Primary model not shown in normal stat row");
            Check(Find<TextBlock>(window, "PrimaryModelName").Text == "Torment" && extra.Items.Count == 1, "Expected one additional named statline");
            Check(Descendants<TextBlock>(extra).Any(t => t.Text == "Mutant"), "Second model label missing");
            var extraStats = Descendants<ItemsControl>(extra).Single();
            Check(ReferenceEquals(primary.ItemTemplate, extraStats.ItemTemplate), "Additional row does not reuse the normal stat template");
            Check(extraStats.Items.Cast<EffectiveCharacteristic>().Single(s => s.Key == "W").Value == "1", "Second model wounds lost");
            var firstPosition = primary.TransformToAncestor(window).Transform(new Point());
            var secondPosition = extra.TransformToAncestor(window).Transform(new Point());
            Check(secondPosition.Y > firstPosition.Y && Math.Abs(secondPosition.X - firstPosition.X) < 1, "Statlines are not stacked/aligned");
            Capture(window, imagePath + ".statlines.png");
            Find<ListBox>(window, "Roster").SelectedValue = same.Name; window.UpdateLayout();
            Check(Find<ItemsControl>(window, "Stats").Items.Count == 6 && extra.Items.Count == 0
                && Find<StackPanel>(window, "ModelsSection").Visibility == Visibility.Collapsed, "Identical profiles were duplicated");
            File.WriteAllText(imagePath + ".statlines.txt", "PASS: normal model stat row, distinct second row, shared template, alignment, labels and identical-profile deduplication.");
        }
        finally { window.Close(); }
    }
}
