using System.IO;
using System.Reflection;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckSpaceMarineStratagems(MainWindow window, string directory, string imagePath)
    {
        var army = ArmyList.New("Dark Angels") with { Name = "Current Chapter stratagems", Detachment = "Wrath of the Rock" };
        typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
        Click(window, "RulesButton");
        var cards = Find<ItemsControl>(window, "StratagemCards").Items.Cast<ReferenceCard>().Where(c => !ArmyReferences.IsCore(c)).ToList();
        Check(cards.Count == 6 && cards.Any(c => c.Name == "KNIGHTS OF IRON") && cards.Any(c => c.Name == "IN SACRIFICE, VICTORY"), "Current Chapter set missing");
        Check(cards.All(c => c.Source?.Date == "2026-10-14" && c.Details!.Effect.Length > 0 && c.Name != "ARMOUR OF CONTEMPT"), "Obsolete/unattributed cards");
        Check(StratagemTiming.FromText(cards.Single(c => c.Name == "KNIGHTS OF IRON").DisplayText) == StratagemTurn.Yours, "Wrong turn colour for Knights of Iron");
        window.UpdateLayout();
        var panel = Find<ItemsControl>(window, "StratagemCards");
        panel.BringIntoView(); window.UpdateLayout();
        PumpUntil(window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle).Task);
        Capture(window, imagePath);
        File.WriteAllText(imagePath + ".result.txt", "PASS: Native Windows Rules displays six current Wrath of the Rock stratagems with Wahapedia provenance.\n" + string.Join("\n", cards.Select(c => c.Name)));
    }
}
