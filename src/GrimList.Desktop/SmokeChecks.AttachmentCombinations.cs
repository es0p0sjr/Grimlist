using System.IO;
using System.Reflection;
using System.Windows.Automation;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    private static void CheckAttachmentCombinations(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var fixture = DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(root, "attachment-combination-fixture.json")));
        const string body = "Plague Marines", caster = "Malignant Plaguecaster", biologus = "Biologus Putrifier", ordinary = "Ordinary Leader fixture";
        var open = typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ComboBox? Picker(MainWindow window, string name) => OpenAttachmentPicker(Descendants<ComboBox>(Find<StackPanel>(window, "AttachmentEditor"))
            .SingleOrDefault(p => AutomationProperties.GetName(p) == name));
        bool Offers(ComboBox? picker, string id) => picker?.Items.Cast<object>().Any(i => (string?)i.GetType().GetProperty("Id")!.GetValue(i) == id) == true;
        void Select(MainWindow window, string id) { Find<ListBox>(window, "Roster").SelectedValue = id; window.UpdateLayout(); }
        var count = 0;
        foreach (var fromBodyguard in new[] { true, false })
        foreach (var order in new[] { new[] { caster, biologus }, new[] { biologus, caster } })
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, $"combination-picker-ui-{++count}"));
            store.Import(fixture);
            var data = store.LoadDataset()!;
            ArmyList Saved(string id) => store.LoadArmies().Single(a => a.Id == id);
            var army = ArmyList.New("Death Guard") with { Name = $"10-model combination UI {count}", Entries = new[] { body, caster, biologus, ordinary }.Select(n =>
            {
                var unit = data.Units.Single(u => u.Name == n);
                return new ArmyEntry(n, unit.Key, unit.Name, n == body ? 10 : 1);
            }).ToList() };
            store.Save(army);
            var window = new MainWindow(store.DirectoryPath) { Owner = owner }; window.Show();
            try
            {
                open.Invoke(window, [army]);
                foreach (var leader in order)
                {
                    // Check BOTH actual candidate filters, then fire the real SelectionChanged handler on one.
                    Select(window, leader);
                    var bodyguardPicker = Picker(window, "Attach leader to unit");
                    Check(Offers(bodyguardPicker, body) && bodyguardPicker!.IsEnabled, $"{leader}: 10-model bodyguard unavailable after first attachment");
                    Select(window, body);
                    var leaderPicker = Picker(window, "Attach leader");
                    Check(Offers(leaderPicker, leader) && leaderPicker!.IsEnabled, $"{leader}: missing from bodyguard's enabled candidate picker");
                    if (fromBodyguard) leaderPicker!.SelectedValue = leader;
                    else { Select(window, leader); Picker(window, "Attach leader to unit")!.SelectedValue = body; }
                    Check(Saved(army.Id).Entries.Single(e => e.Id == leader).AttachedToId == body, "UI selection failed to persist attachment");
                }
                Check(Saved(army.Id).Entries.Count(e => e.AttachedToId == body) == 2, "Final roster lost a Leader");
                Check(Saved(army.Id).Entries.Single(e => e.Id == body).Models == 10, "Attachment changed squad size");
                Check(LeaderRules.Issues(Saved(army.Id), data).Count == 0, "Saved roster rejected by final validation");
                Select(window, body);
                Check(!Offers(Picker(window, "Attach leader"), ordinary), "Third Leader offered");
                if (count == 1) Capture(window, imagePath + ".two-leaders.png");
                // Remove the permission-granting character using the UI. A second ordinary Leader must now be unavailable.
                Select(window, biologus); Picker(window, "Attach leader to unit")!.SelectedValue = "";
                Select(window, body);
                Check(!Offers(Picker(window, "Attach leader"), ordinary), "Bodyguard kept a global second-Leader slot after Biologus detached");
                Select(window, ordinary);
                Check(!Offers(Picker(window, "Attach leader to unit"), body), "Ordinary Leader picker bypassed one-Leader default");
                // Restore the legal group through the same control before reopening it.
                Select(window, biologus); Picker(window, "Attach leader to unit")!.SelectedValue = body;
                Check(Saved(army.Id).Entries.Count(e => e.AttachedToId == body) == 2, "Restoring Biologus did not restore pair");
            }
            finally { window.Close(); }
            var reopened = new MainWindow(store.DirectoryPath) { Owner = owner }; reopened.Show();
            try
            {
                open.Invoke(reopened, [Saved(army.Id)]); Select(reopened, body);
                Check(Find<StackPanel>(reopened, "AttachmentSummary").Children.OfType<Button>().Count() == 2, "Reopening lost one of the attached Leaders");
                Click(reopened, "PlayModeButton");
                Check(Find<StackPanel>(reopened, "AttachmentSummary").Children.OfType<Button>().Count() == 2, "Play mode lost the two-Leader group");
            }
            finally { reopened.Close(); }
        }
        // Synthetic missing-permission check only. The real catalogue has structural bodyguard permissions.
        var missingRule = fixture with { Units = fixture.Units.Select(u => u.Name == biologus ? u with { Profile = u.Profile! with
                { Abilities = [new("Leader", "This model can be attached to the following unit: Plague Marines.")] } } : u).ToList() };
        List<string> rejections = [];
        foreach (var order in new[] { new[] { caster, biologus }, new[] { biologus, caster } })
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, $"missing-permission-ui-{++count}"));
            store.Import(missingRule); var data = store.LoadDataset()!;
            var army = ArmyList.New("Death Guard") with { Name = "Installed permission UI diagnostic", Entries = new[] { body, caster, biologus }.Select(n =>
            {
                var unit = data.Units.Single(u => u.Name == n); return new ArmyEntry(n, unit.Key, unit.Name, n == body ? 10 : 1);
            }).ToList() };
            store.Save(army);
            var window = new MainWindow(store.DirectoryPath) { Owner = owner }; window.Show();
            try
            {
                open.Invoke(window, [army]); Select(window, body);
                Check(Offers(Picker(window, "Attach leader"), order[0]), "First installed Leader unavailable");
                Picker(window, "Attach leader")!.SelectedValue = order[0];
                Select(window, body);
                Check(!Offers(Picker(window, "Attach leader"), order[1]), "Missing exception incorrectly allowed a second Leader");
                Select(window, order[1]);
                Check(!Offers(Picker(window, "Attach leader to unit"), body), "Leader-side picker bypassed missing permission");
                var saved = store.LoadArmies().Single();
                Check(saved.Entries.Count(e => e.AttachedToId == body) == 1, "Rejected candidate changed the roster");
                rejections.Add(order[0] + " then " + order[1] + ": " + LeaderRules.Problem(saved, data, order[1], body));
                if (count == 5) Capture(window, imagePath + ".unavailable-attachment.png");
            }
            finally { window.Close(); }
        }
        File.WriteAllText(imagePath + ".attachments.json", DataJson.Write(new
        {
            BodyguardModels = 10, SuccessfulFlows = 4, MissingPermissionFlows = 2, MissingPermissionSource = missingRule.Source,
            Checks = new[] { "Both attachment orders through both real pickers", "Candidate presence and enabled state", "SelectionChanged and SQLite save", "No third Leader", "Ordinary Leader pair rejected after permission owner detaches", "Reopen and Play mode preserve both Leaders" },
            Rejections = rejections
        }));
    }
}
