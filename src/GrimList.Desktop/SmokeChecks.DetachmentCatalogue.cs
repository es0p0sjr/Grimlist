using System.IO;
using System.Reflection;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckDetachmentCatalogue(MainWindow window, string directory, string imagePath)
    {
        var session = (ArmySession)typeof(MainWindow).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var home = Find<HomeView>(window, "Home");
        var setup = Find<ArmySetupView>(window, "Setup");
        Click(home, "NewArmyTile");
        Find<ListBox>(setup, "FactionList").SelectedValue = "Ultramarines";
        Click(setup, "NextButton");
        Check(setup.Step == 1, "Detachment setup did not open");
        var data = session.DatasetForArmy(ArmyList.New("Ultramarines"))!;
        var options = DetachmentRules.Options("Ultramarines", data);
        Check(options.Any(d => d.Name == "Assault Brethren"), "Current core detachment missing");
        Check(!options.Any(d => DetachmentCatalogue.IsRetired(data, d.Faction, d.Name)), "Retired detachment selectable");
        Check(!options.Any(d => d.Name == "Vow-Sworn Crusaders"), "Other Chapter detachment selectable");
        Check(DetachmentRules.Issues(ArmyList.New("Ultramarines") with { Detachment = "Anvil Siege Force" }, data).Any(s => s.Contains("retired after")), "Legacy selection lacks diagnostic");
        window.UpdateLayout(); Capture(window, imagePath);
        File.WriteAllText(imagePath + ".result.txt", "PASS: Windows detachment setup opens with current core options, Chapter restrictions and no retired selections. Existing retired selections receive a review diagnostic.\n" + string.Join("\n", options.Select(d => d.Name)));
    }
}
