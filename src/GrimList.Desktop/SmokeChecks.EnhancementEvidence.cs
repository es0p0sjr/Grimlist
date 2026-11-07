using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckEnhancementEvidence(MainWindow window, string output)
    {
        var session = (ArmySession)typeof(MainWindow).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var scenarios = new Dictionary<string, EnhancementAcceptance.Scenario>();
        var report = EnhancementAcceptance.Run(s => scenarios.TryAdd(s.Army.Faction, s));
        List<string> rendered = [];
        foreach (var scenario in scenarios.Values)
        {
            var army = scenario.Army with { Id = "enhancement-native-check", Name = "Enhancement acceptance" };
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
            Find<ListBox>(window, "Roster").SelectedValue = "bearer";
            window.UpdateLayout();
            var picker = Find<ItemsControl>(window, "UnitEnhancementChoices");
            var button = Descendants<Button>(picker).Single(b => b.Tag is EnhancementSelection selection
                && selection.Name == scenario.Card.Name && selection.Detachment == scenario.Card.Detachment);
            Check(button.IsEnabled, "Native picker disabled an eligible enhancement: " + scenario.Card.Name);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(session.SelectedEntry?.Enhancement?.Name == scenario.Card.Name, "Native enhancement action did not select the card");
            session.Undo(); Check(session.SelectedEntry?.Enhancement == null, "Native selection undo failed");
            session.Redo(); Check(session.SelectedEntry?.Enhancement?.Name == scenario.Card.Name, "Native selection redo failed");
            rendered.Add(army.Faction);
        }
        Check(rendered.Count >= 20, "Native picker coverage is incomplete");
        File.WriteAllText(output + ".result.txt", "PASS " + DataJson.Write(new { report, NativePickerFactions = rendered }));
    }
}
