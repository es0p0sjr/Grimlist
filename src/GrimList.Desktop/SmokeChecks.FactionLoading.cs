using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckFactionLoading(MainWindow window, string directory, string imagePath)
    {
        var session = (ArmySession)typeof(MainWindow).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var checks = new List<string>();
        void Verify(bool value, string name) { Check(value, name); checks.Add(name); }
        Verify(session.Dataset!.Units.Count == 0, "Home starts without loading faction profiles");
        Verify(session.FactionLoading!.FullReads == 0, "Warm launch avoids full library read");
        Verify(session.SummaryCalculationCount == 0, "Home reuses persisted totals");
        var home = Find<HomeView>(window, "Home");
        var setup = Find<ArmySetupView>(window, "Setup");
        Click(home, "NewArmyTile");
        Verify(Find<ListBox>(setup, "FactionList").Items.Count >= session.Factions.Count, "New army lists unloaded factions");
        Find<ListBox>(setup, "FactionList").SelectedValue = "Adeptus Mechanicus";
        Click(setup, "NextButton");
        Verify(setup.Step == 1, "Unloaded faction opens setup");
        var setupData = (Dataset)typeof(ArmySetupView).GetField("data", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(setup)!;
        Verify(setupData.Units.Any(u => u.Faction == "Adeptus Mechanicus") && setupData.Units.Count < session.LibraryUnitCount, "Setup loads chosen faction and dependencies");
        Click(setup, "CancelButton");
        foreach (var faction in new[] { "Death Guard", "Orks", "Death Guard" })
        {
            window.UpdateLayout();
            var tile = Descendants<Button>(home).Single(b => b.Tag is ArmyList a && a.Faction == faction);
            tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Verify(session.Army.Faction == faction && session.Dataset!.Units.Count < session.LibraryUnitCount, "Army tile loads " + faction);
            Verify(session.BrowseLibrary(new()).Count > 0, "Unit library populated for " + faction);
            Verify(Find<Grid>(window, "BuilderScreen").Visibility == Visibility.Visible, "Editor visible for " + faction);
            Capture(window, imagePath + "." + faction.Replace(" ", "-") + ".png");
            Click(window, "NewArmyButton");
        }
        Verify(session.FactionLoading.CachedViews <= 2, "Recent faction cache is bounded");
        Verify(session.LoadFullDataset()!.Units.Count == session.LibraryUnitCount, "Explicit export loads complete library");
        Capture(window, imagePath);
        File.WriteAllLines(imagePath + ".result.txt", checks.Select(c => "PASS " + c));
    }
}
