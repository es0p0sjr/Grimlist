using System.IO;
using System.Reflection;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    private static void CheckWarlordRestrictions(MainWindow owner, string directory, string imagePath)
    {
        var path = Path.Combine(Path.GetDirectoryName(directory)!, "warlord-restriction-fixture.json");
        if (!File.Exists(path)) throw new InvalidDataException("Run the Warlord regressions to create warlord-restriction-fixture.json.");
        using var store = WindowsServices.OpenStore(Path.Combine(Path.GetDirectoryName(directory)!, "warlord-ui"));
        var data = DataJson.Read<Dataset>(File.ReadAllText(path)); store.Import(data);
        var forbidden = data.Units.Single(u => u.Name == "Transcendent C'tan");
        var allowed = data.Units.Single(u => u.Name == "Overlord");
        var army = ArmyList.New("Necrons") with { Name = "Warlord restrictions UI", Entries =
            [new("ctan", forbidden.Key, forbidden.Name, 1), new("lord", allowed.Key, allowed.Name, 1)] };
        store.Save(army);
        var window = new MainWindow(store.DirectoryPath) { Owner = owner }; window.Show();
        try
        {
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
            Find<ListBox>(window, "Roster").SelectedValue = "ctan"; window.UpdateLayout();
            var picker = Find<ComboBox>(window, "WarlordPicker");
            var ids = picker.Items.Cast<object>().Select(i => (string)i.GetType().GetProperty("Id")!.GetValue(i)!).ToList();
            Check(ids.SequenceEqual(["", "lord"]), "Forbidden C'tan appears in Warlord picker or legal Overlord missing");
            Check(Find<TextBlock>(window, "WarlordHint").Text.Contains("Enslaved Star God"), "Warlord exclusion source not visible");
            picker.SelectedValue = "lord";
            Check(store.LoadArmies().Single().WarlordEntryId == "lord", "Legal Warlord not saved");
            Click(window, "UndoButton"); Check(store.LoadArmies().Single().WarlordEntryId == "", "Warlord undo failed");
            Click(window, "RedoButton"); Check(store.LoadArmies().Single().WarlordEntryId == "lord", "Warlord redo failed");
            Capture(window, imagePath + ".warlord.png");
        }
        finally { window.Close(); }
        File.WriteAllText(imagePath + ".warlord.txt", "PASS: forbidden C'tan filtered; source reason visible; legal Overlord selection, persistence and undo/redo.");
    }
}
