using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckRecipients(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var data = DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(root, "recipient-ui.json")));
        using var store = WindowsServices.OpenStore(Path.Combine(root, "recipient-ui-" + Guid.NewGuid().ToString("N")));
        store.Import(data);
        var guard = data.Units.Single(u => u.Name == "Deathshroud Terminators");
        var leader = data.Units.Single(u => u.Name == "Lord of Contagion");
        var army = ArmyList.New(guard.Faction) with { Name = "Attachment recipient rendering", Entries = [
            new("guard", guard.Key, guard.Name, 3, Loadout: Loadouts.Create(guard, 3)),
            new("leader", leader.Key, leader.Name, 1, Loadout: Loadouts.Create(leader, 1))] };
        store.Save(army);
        var window = new MainWindow(store.DirectoryPath) { Owner = owner, ShowInTaskbar = false }; window.Show();
        var session = (ArmySession)typeof(MainWindow).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void View(string id, bool expected)
        {
            typeof(MainWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)!.Invoke(window, null);
            Find<ListBox>(window, "Roster").SelectedValue = id; window.UpdateLayout();
            var badge = Find<Button>(window, "FeelNoPainPanel");
            Check(badge.IsVisible == expected, "Wrong FNP recipient shown: " + id);
            if (expected) Check(Find<TextBlock>(window, "FeelNoPainValue").Text == "4+++ FNP", "Recipient FNP value incorrect");
        }
        try
        {
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
            View("guard", false); View("leader", false);
            session.Commit(LeaderRules.Attach(session.Army, session.Dataset, "leader", "guard"));
            View("guard", false); Capture(window, imagePath + ".bodyguard.png");
            View("leader", true); Capture(window, imagePath + ".leader.png");
            Click(window, "PlayModeButton"); View("guard", false); View("leader", true);
            Click(window, "PlayModeButton");
            session.Commit(LeaderRules.Attach(session.Army, session.Dataset, "leader", ""));
            View("leader", false); View("guard", false);
            session.Undo(); View("leader", true); View("guard", false);
            session.Redo(); View("leader", false);
            File.WriteAllText(imagePath + ".recipients.txt", "PASS: Deathshroud grant appears only on the leader in Windows build/play modes; detach, undo and redo refresh both badges.");
        }
        finally { window.Close(); }
    }
}
