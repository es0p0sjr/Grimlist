using System.IO;
using System.Reflection;
using System.Windows.Automation;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    private static void CheckStructuralAttachments(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var path = Path.Combine(root, "real-association-fixture.json");
        if (!File.Exists(path)) return; // Created when the exact-catalogue integration test is enabled.
        var fixture = DataJson.Read<Dataset>(File.ReadAllText(path));
        var open = typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int checks = 0;
        foreach (var partner in new[] { "Biologus Putrifier", "Tallyman" })
        foreach (var fromBodyguard in new[] { true, false })
        foreach (var order in new[] { new[] { "1", "2" }, new[] { "2", "1" } })
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, $"structural-attachment-ui-{++checks}"));
            store.Import(fixture); var data = store.LoadDataset()!;
            var names = new[] { "Plague Marines", "Malignant Plaguecaster", partner, "Malignant Plaguecaster" };
            var army = ArmyList.New("Death Guard") with { Name = "Real revision-17 association test", Entries = names.Select((n, i) =>
            { var u = data.Units.Single(u => u.Name == n); return new ArmyEntry(i.ToString(), u.Key, u.Name, i == 0 ? 10 : 1); }).ToList() };
            store.Save(army);
            var window = new MainWindow(store.DirectoryPath) { Owner = owner }; window.Show();
            ComboBox? Picker(string name) => OpenAttachmentPicker(Descendants<ComboBox>(Find<StackPanel>(window, "AttachmentEditor")).SingleOrDefault(p => AutomationProperties.GetName(p) == name));
            bool Offers(ComboBox? picker, string id) => picker?.Items.Cast<object>().Any(i => (string?)i.GetType().GetProperty("Id")!.GetValue(i) == id) == true;
            void Select(string id) { Find<ListBox>(window, "Roster").SelectedValue = id; window.UpdateLayout(); }
            ArmyList Saved() => store.LoadArmies().Single();
            try
            {
                open.Invoke(window, [army]);
                foreach (var id in order)
                {
                    Select(id); var targets = Picker("Attach leader to unit");
                    Check(Offers(targets, "0") && targets!.IsEnabled, "Real bodyguard missing from Leader-side candidate path");
                    Select("0"); var leaders = Picker("Attach leader");
                    Check(Offers(leaders, id) && leaders!.IsEnabled, "Candidate-dependent structural modifier not evaluated by bodyguard picker");
                    if (fromBodyguard) leaders!.SelectedValue = id;
                    else { Select(id); Picker("Attach leader to unit")!.SelectedValue = "0"; }
                    Check(Saved().Entries.Single(e => e.Id == id).AttachedToId == "0", "Real source UI selection did not save");
                }
                Check(Saved().Entries.Count(e => e.AttachedToId == "0") == 2 && LeaderRules.Issues(Saved(), data).Count == 0, "Real structural group invalid after UI selection");
                Select("0");
                window.UpdateLayout();
                if (checks == 1) Capture(window, imagePath + ".real-associations.png");
                Check(!Offers(Picker("Attach leader"), "3"), "Third Leader offered despite effective max 2");
                Select("2"); Picker("Attach leader to unit")!.SelectedValue = "";
                Select("0"); Check(!Offers(Picker("Attach leader"), "3"), "Raised Leader limit persisted after permission condition stopped matching");
                Select("3"); Check(!Offers(Picker("Attach leader to unit"), "0"), "Two ordinary Leaders allowed through alternate picker");
                Select("2"); Picker("Attach leader to unit")!.SelectedValue = "0";
                Click(window, "UndoButton"); Check(Saved().Entries.Count(e => e.AttachedToId == "0") == 1, "Undo lost prospective constraint state");
                Click(window, "RedoButton"); Check(Saved().Entries.Count(e => e.AttachedToId == "0") == 2, "Redo failed to restore structural group");
            }
            finally { window.Close(); }
            var reopened = new MainWindow(store.DirectoryPath) { Owner = owner }; reopened.Show();
            try
            {
                open.Invoke(reopened, [Saved()]); Find<ListBox>(reopened, "Roster").SelectedValue = "0"; reopened.UpdateLayout();
                Check(Find<StackPanel>(reopened, "AttachmentSummary").Children.OfType<Button>().Count() == 2, "Reload dropped a real-source attachment");
                Click(reopened, "PlayModeButton");
                Check(Find<StackPanel>(reopened, "AttachmentSummary").Children.OfType<Button>().Count() == 2, "Play mode dropped real-source attachment");
            }
            finally { reopened.Close(); }
        }
        File.WriteAllText(imagePath + ".real-associations.txt", $"PASS: {checks} real revision-17 desktop attachment flows; both orders, both pickers, Biologus and Tallyman, ordinary Leader rejection, undo/redo/reopen/Play mode.");
    }
}
