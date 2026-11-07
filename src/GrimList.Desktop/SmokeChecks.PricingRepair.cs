using System.IO;
using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;
internal static partial class SmokeChecks
{
    private static void CheckPricingRepair(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        using var store = WindowsServices.OpenStore(Path.Combine(root, "pricing-repair-ui"));
        store.Import(DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(root, "pricing-repair-before.json"))));
        var source = DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(root, "pricing-repair-source.json")));
        var before = store.LoadDataset()!; var unit = before.Units.Single(u => u.Name == "Fixture assassin");
        store.Save(ArmyList.New("Astra Militarum") with { Entries = [new("saved", unit.Key, unit.Name, 1)] });
        var armies = DataJson.Write(store.LoadArmies()); string? failure = null;
        var dialog = new PricingRepairWindow(store) { Owner = owner };
        dialog.ContentRendered += (_, _) =>
        {
            try
            {
                Check(Find<DataGrid>(dialog, "Results").Items.Count == 3, "Initial audit missed units");
                Check(!Find<Button>(dialog, "ApplyButton").IsEnabled, "Repair allowed before source check");
                dialog.ShowPreview(PricingRepair.Preview(before, source), source.Source);
                Check(Find<TextBlock>(dialog, "Summary").Text.Contains("2 recovered"), "Recovered count missing");
                Check(Find<TextBlock>(dialog, "ImpactText").Text.Contains("110"), "Allied roster impact missing");
                Check(DataJson.Write(store.LoadDataset()) == DataJson.Write(before), "Preview mutated library");
                Capture(dialog, imagePath + ".pricing-repair.png");
                Click(dialog, "ApplyButton");
            }
            catch (Exception ex) { failure = ex.Message; dialog.Close(); }
        };
        Check(dialog.ShowDialog() == true && failure == null, failure ?? "Repair dialog failed");
        Check(DataJson.Write(store.LoadArmies()) == armies, "Repair changed saved army");
        Check(ArmyCalculator.Calculate(store.LoadArmies().Single(), store.LoadDataset()).Total == 110, "Applied ally price not persisted");
        var builder = new MainWindow(store.DirectoryPath) { Owner = owner };
        builder.Show();
        try
        {
            typeof(MainWindow).GetMethod("OpenRoster", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(builder, [store.LoadArmies().Single()]);
            var picker = Find<ListBox>(builder, "UnitPicker");
            var displayed = picker.Items.Cast<UnitDefinition>().Single(u => u.Name == "Fixture assassin");
            Check(displayed.Summary.Contains("110 pts"), "Library displayed native price for ally");
        }
        finally { builder.Close(); }
    }
}
