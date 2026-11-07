using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    private static void CheckMuster(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var path = Path.Combine(root, "muster-fixture.json");
        if (!File.Exists(path)) return;
        using var store = WindowsServices.OpenStore(Path.Combine(root, "muster-ui"));
        store.Import(DataJson.Read<Dataset>(File.ReadAllText(path)));
        var data = store.LoadDataset()!;
        var army = ArmyList.New("Death Guard") with { Name = "Real catalogue muster UI", Limit = 2000 };
        store.Save(army);
        var open = typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var window = new MainWindow(store.DirectoryPath) { Owner = owner }; window.Show();
        ArmyList Saved() => store.LoadArmies().Single();
        ListBox Library() => Find<ListBox>(window, "UnitPicker");
        UnitDefinition Unit(string name) => data.Units.Single(u => u.Name == name);
        int Count(string name) => MusterRules.Count(Saved(), data, Unit(name));
        void Select(string name)
        {
            Library().SelectedItem = Library().Items.Cast<UnitDefinition>().Single(u => u.Name == name);
            Library().ScrollIntoView(Library().SelectedItem); window.UpdateLayout();
        }
        Button Quick(string name) => FindTaggedButton(Library(), Unit(name).Key);
        string Group(string name) => ((CollectionView)Library().ItemsSource).Groups!.Cast<CollectionViewGroup>()
            .Single(g => g.Items.OfType<UnitDefinition>().Any(u => u.Name == name)).Name.ToString()!;
        void Add(string name, int count)
        {
            for (var i = 0; i < count; i++)
            {
                Select(name); Check(Find<Button>(window, "AddButton").IsEnabled, $"Premature cap for {name}");
                var before = Count(name);
                if (i == 0)
                {
                    var quick = Quick(name);
                    Check(quick.IsEnabled, $"Quick-add prematurely disabled for {name}");
                    quick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else Click(window, "AddButton");
                Check(Count(name) == before + 1, $"Add did not persist another {name}");
            }
        }
        void Capped(string name, int expected)
        {
            Select(name);
            Check(!Find<Button>(window, "AddButton").IsEnabled && !Quick(name).IsEnabled, $"Add buttons enabled above cap for {name}");
            Click(window, "AddButton"); Quick(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Count(name) == expected, $"Add event bypassed cap for {name}");
            Find<ListBox>(window, "Roster").SelectedValue = Saved().Entries.First(e => e.UnitKey == Unit(name).Key).Id;
            Check(!Find<Button>(window, "DuplicateEntryButton").IsEnabled, $"Duplicate enabled above cap for {name}");
            Click(window, "DuplicateEntryButton"); Check(Count(name) == expected, $"Duplicate event bypassed cap for {name}");
        }
        void Detachment(bool add)
        {
            Click(window, "RulesButton");
            if (add) Click(window, "SelectDetachmentsButton");
            DetachmentAction(window, add ? "AvailableDetachments" : "CurrentDetachments", "Shamblerot Vectorium")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Click(window, "CloseRulesButton");
        }
        try
        {
            open.Invoke(window, [army]);
            Add("Malignant Plaguecaster", 3); Capped("Malignant Plaguecaster", 3);
            Check(Group("Chaos Land Raider") == "Vehicles", "Ordinary Transport moved to Dedicated Transports");
            Add("Chaos Rhino", 6); Capped("Chaos Rhino", 6);
            Check(Group("Chaos Rhino") == "Dedicated Transports", "Dedicated Transport group missing");
            Add("Typhus", 1); Capped("Typhus", 1);
            Add("Poxwalkers", 3); Capped("Poxwalkers", 3);
            Check(Group("Poxwalkers") == "Others", "Poxwalkers started Battleline");
            Detachment(true);
            Check(Group("Poxwalkers") == "Battleline", "Real detachment selection did not regroup library");
            Select("Poxwalkers"); Quick("Poxwalkers").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<ListBox>(window, "Roster").SelectedValue = Saved().Entries.Last().Id;
            Click(window, "DuplicateEntryButton"); Add("Poxwalkers", 1); Capped("Poxwalkers", 6);
            Check(RosterExport.Text(Saved(), data).Contains("BATTLELINE"), "Export lost effective Battleline group");
            Capture(window, imagePath + ".muster.png");
            Detachment(false);
            Check(Group("Poxwalkers") == "Others" && Count("Poxwalkers") == 6 && MusterRules.Issues(Saved(), data).Any(i => i.Contains("Poxwalkers")),
                "Detachment removal must regroup and flag excess while retaining units");
            Capped("Poxwalkers", 6);
            Click(window, "UndoButton"); Check(Group("Poxwalkers") == "Battleline", "Undo did not restore effective keyword");
            Click(window, "RedoButton"); Check(Group("Poxwalkers") == "Others", "Redo did not restore category");
            Detachment(true);
            Click(window, "RulesButton");
            var limit = Find<TextBox>(window, "LimitBox"); limit.Text = "1000"; limit.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Click(window, "CloseRulesButton");
            Check(MusterRules.Limit(Saved(), Unit("Poxwalkers"), data) == 4 && Count("Poxwalkers") == 6, "Incursion resize discarded units or retained six-unit allowance");
            Capped("Poxwalkers", 6);
        }
        finally { window.Close(); }
        var reopened = new MainWindow(store.DirectoryPath) { Owner = owner }; reopened.Show();
        try
        {
            open.Invoke(reopened, [Saved()]);
            Check(Count("Poxwalkers") == 6 && MusterRules.Category(Saved(), Unit("Poxwalkers"), data) == "Battleline"
                && ArmyCalculator.Calculate(Saved(), data).Issues.Any(i => i.Contains("Poxwalkers") && i.Contains("limit of 4")), "Reload lost units, effective keyword or excess warning");
        }
        finally { reopened.Close(); }
        File.WriteAllText(imagePath + ".muster.txt", "PASS: real catalogue; Add, quick-add, Duplicate caps; ordinary/Dedicated Transport, Epic Hero, Shamblerot add/remove, regrouping, 3/6 and 2/4 limits, undo/redo, excess retention and reload.");
    }
}
