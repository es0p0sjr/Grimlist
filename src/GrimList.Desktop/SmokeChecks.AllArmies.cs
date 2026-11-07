using System.IO;
using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;
internal static partial class SmokeChecks
{
    private static void CheckAllArmies(MainWindow owner, string directory, string imagePath)
    {
        var fixture = Path.Combine(Path.GetDirectoryName(directory)!, "all-armies-fixture.zip");
        if (!File.Exists(fixture)) return;
        var testDirectory = Path.Combine(Path.GetDirectoryName(directory)!, "all-armies-ui");
        using var store = WindowsServices.OpenStore(testDirectory);
        var import = new ImportWindow(store, initialLayer: ImportLayer.Combined) { Owner = owner };
        string? failure = null;
        import.ContentRendered += async (_, _) =>
        {
            try
            {
                Check((string)Find<ComboBox>(import, "DownloadFaction").SelectedItem == "All armies", "Download scope is still Death Guard");
                var bytes = File.ReadAllBytes(fixture);
                await import.LoadCommunity(BsDataDocument.ReadArchive(bytes), bytes);
                Click(import, "PreviewButton");
                Check(Find<Button>(import, "ApplyButton").IsEnabled && Find<TextBlock>(import, "Summary").Text.StartsWith("6 factions"), "Multi-army preview failed");
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Capture(import, imagePath + ".all-armies-import.png");
                Click(import, "ApplyButton");
            }
            catch (Exception ex) { failure = ex.Message; import.DialogResult = false; }
        };
        import.ShowDialog(); Check(failure == null, failure ?? "");
        Check(store.LoadDataset()!.Units.Select(u => u.Faction).Distinct().Count() == 6, "Not all armies persisted");
        var window = new MainWindow(testDirectory) { Owner = owner };
        window.ContentRendered += (_, _) =>
        {
            try
            {
                Click(Find<HomeView>(window, "Home"), "NewArmyTile");
                var setup = Find<ArmySetupView>(window, "Setup");
                Find<ListBox>(setup, "FactionList").SelectedValue = "Orks";
                Click(setup, "NextButton");
                Check(setup.Step == 1, "Imported faction cannot enter army setup");
                Capture(window, imagePath + ".all-armies-setup.png");
                var captain = store.LoadDataset()!.Units.Single(u => u.Faction == "Black Templars" && u.Name == "Chapter Captain");
                var army = ArmyList.New("Black Templars") with { Name = "Imported chapter fixture", Entries = [new("captain-entry", captain.Key, captain.Name, 1)] };
                store.Save(army);
                Check(ArmyCalculator.Calculate(store.LoadArmies().Single(), store.LoadDataset()).Total == 90, "Inherited chapter unit cannot price a saved army");
            }
            catch (Exception ex) { failure = ex.Message; }
            finally { window.Close(); }
        };
        window.ShowDialog(); Check(failure == null, failure ?? "");
    }
}
