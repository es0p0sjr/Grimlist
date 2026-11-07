using System.IO;
using System.Windows;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckMfm(MainWindow owner, string directory, string imagePath)
    {
        using var store = WindowsServices.OpenStore(Path.Combine(Path.GetDirectoryName(directory)!, "mfm-ui-" + Guid.NewGuid().ToString("N")));
        var source = new SourceInfo("BSData · UI fixture", "1", "2026-10-07", "https://github.com/BSData/wh40k-11e", Edition: "11");
        var data = new Dataset(source, [new("Dark Angels", "Deathwing Knights", [new(5, 240), new(5, 260, 3)], Profile: new(Notes: "Keep this datasheet"))]);
        store.Import(data);
        var unit = store.LoadDataset()!.Units.Single();
        var army = ArmyList.New("Dark Angels") with { Entries = Enumerable.Range(1, 3).Select(i => new ArmyEntry(i.ToString(), unit.Key, unit.Name, 5)).ToList() };
        store.Save(army);
        var mirror = new SourceInfo("Munitorum Field Manual · BSData mirror", "UI snapshot", "", "https://github.com/BSData/wh40k-11e-mfm/blob/main", Edition: "11");
        var prices = MfmMirrorParser.Parse(File.ReadAllText(Path.Combine("tests", "fixtures", "mfm", "dark-angels-1.5.yaml")), mirror).Dataset;
        foreach (var incoming in new[] { prices, data })
        {
            var import = new ImportWindow(store, initialLayer: incoming == prices ? ImportLayer.Points : ImportLayer.Combined) { Owner = owner };
            Find<TextBox>(import, "RawText").Text = DataJson.Write(incoming);
            Exception? failure = null;
            import.ContentRendered += async (_, _) =>
            {
                try
                {
                    var before = DataJson.Write(store.LoadDataset());
                    Click(import, "PreviewButton");
                    Check(Find<Button>(import, "ApplyButton").IsEnabled, Find<TextBlock>(import, "Summary").Text);
                    Check(Find<TextBlock>(import, "Summary").Text.Contains("MFM unit prices: 1/1"), "MFM provenance missing from preview");
                    Check(DataJson.Write(store.LoadDataset()) == before, "Preview changed live data");
                    var rows = Find<DataGrid>(import, "PreviewGrid").Items.Cast<object>().ToList();
                    Check(rows.Count == 2 && rows[0].GetType().GetProperty("Points")!.GetValue(rows[0])?.ToString() == "255", "Preview displays stale catalogue price");
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Capture(import, imagePath + (incoming == prices ? ".mfm.png" : ".retained.png"));
                    Click(import, "ApplyButton");
                }
                catch (Exception ex) { failure = ex; import.DialogResult = false; }
            };
            import.ShowDialog();
            if (failure != null) throw failure;
            Check(ArmyCalculator.Calculate(store.LoadArmies().Single(), store.LoadDataset()).Total == 785, "Wrong reviewed roster total");
            Check(store.LoadDataset()!.Units.Single().Profile!.Notes == "Keep this datasheet", "Datasheet changed");
        }
        File.WriteAllText(imagePath + ".mfm.txt", "PASS: Windows preview/apply uses MFM 255/255/275, keeps datasheet and retains points on later BSData import.");
    }
}
