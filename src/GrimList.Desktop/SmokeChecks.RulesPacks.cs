using System.IO;
using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckRulesPacks(MainWindow owner, string directory, string imagePath)
    {
        using var store = WindowsServices.OpenStore(Path.Combine(Path.GetDirectoryName(directory)!, "rules-ui-" + Guid.NewGuid().ToString("N")));
        var source = new SourceInfo("BSData UI fixture", "1", "2026-10-07", "https://github.com/BSData/wh40k-11e", Edition: "11");
        var stats = new Dictionary<string, string> { ["T"] = "9", ["W"] = "10" };
        store.Import(new(source, [new("Dark Angels", "Lion El'Jonson", [new(1, 310)], Profile: new(stats,
            Models: [new("Lion El'Jonson", new(stats))], Abilities: [new("Fixture ability", "Retain uncovered rule")]))]));
        var incoming = File.ReadAllText(Path.Combine("data", "rules-packs", "dark-angels-2026-partial.json"));
        var import = new ImportWindow(store, initialLayer: ImportLayer.Profiles) { Owner = owner };
        Find<TextBox>(import, "RawText").Text = incoming;
        Exception? failure = null;
        import.ContentRendered += async (_, _) =>
        {
            try
            {
                var before = DataJson.Write(store.LoadDataset());
                Click(import, "PreviewButton");
                var summary = Find<TextBlock>(import, "Summary").Text;
                Check(Find<Button>(import, "ApplyButton").IsEnabled, summary);
                Check(summary.Contains("Partial") && summary.Contains("NOT the complete codex"), "Partial coverage hidden");
                Check(Find<DataGrid>(import, "PreviewGrid").Items.Count == 1, "Rules pack preview has no unit row");
                Check(DataJson.Write(store.LoadDataset()) == before, "Preview applied live data");
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Capture(import, imagePath + ".rules.png");
                Click(import, "ApplyButton");
            }
            catch (Exception ex) { failure = ex; import.DialogResult = false; }
        };
        import.ShowDialog();
        if (failure != null) throw failure;
        var unit = store.LoadDataset()!.Units.Single();
        Check(unit.Profile!.Characteristics!["T"] == "10" && unit.Profile.Models![0].Characteristics["W"] == "16", "Statline import failed");
        Check(unit.Options[0].Points == 310 && unit.Profile.Abilities!.Single().Name == "Fixture ability", "Uncovered data overwritten");
        File.WriteAllText(imagePath + ".rules.txt", "PASS: Rules pack preview/apply displays Partial coverage, corrects T10/W16 in both profiles, and retains points and uncovered abilities.");
    }
}
