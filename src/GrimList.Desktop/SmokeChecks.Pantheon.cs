using System.IO;
using System.Reflection;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckPantheon(MainWindow owner, string directory, string imagePath)
    {
        var bindings = new Dictionary<string, string>
        {
            ["C'tan Shard of the Deceiver"] = "Lord of Deceit (Aura)",
            ["C'tan Shard of the Nightbringer"] = "Quantum Goad",
            ["C'tan Shard of the Void Dragon"] = "Animus Damper",
            ["Transcendent C'tan"] = "Reletavistic Tether"
        };
        var bundled = BootstrapRelease.ReadBundled().Dataset;
        var data = bundled with { Units = bundled.Units.Where(u => u.Faction == "Necrons"
            && (bindings.ContainsKey(u.Name) || u.Name == "Necron Warriors")).ToList() };
        var ctan = data.Units.Where(u => bindings.ContainsKey(u.Name)).Select((u, i) => new ArmyEntry("ctan-" + i, u.Key, u.Name, 1)).ToList();
        var warriors = data.Units.Single(u => u.Name == "Necron Warriors");
        var army = ArmyList.New("Necrons") with { Detachment = "Pantheon of Woe", Entries = [.. ctan,
            new("small", warriors.Key, warriors.Name, 10), new("large", warriors.Key, warriors.Name, 20)] };
        using var store = WindowsServices.OpenStore(Path.Combine(directory, "pantheon-ui"));
        store.Import(data); store.Save(army);
        var window = new MainWindow(store.DirectoryPath) { Owner = owner, ShowInTaskbar = false };
        try
        {
            window.Show();
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
            var session = (ArmySession)typeof(MainWindow).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var roster = Find<ListBox>(window, "Roster");
            void Verify(bool active)
            {
                foreach (var entry in ctan)
                {
                    roster.SelectedValue = entry.Id; window.UpdateLayout();
                    IEnumerable<AbilityCard> Flatten(AbilityCard card) => new[] { card }.Concat(card.Options.SelectMany(Flatten));
                    var cards = Find<ItemsControl>(window, "AbilityCards").Items.Cast<AbilityCard>().SelectMany(Flatten).ToList();
                    Check(cards.Count(a => a.Name == bindings[entry.Name]) == (active ? 1 : 0), "Incorrect rendered binding: " + entry.Name);
                }
                var order = roster.Items.Cast<object>().Select(r => (string)r.GetType().GetProperty("Id")!.GetValue(r)!)
                    .Where(id => id is "small" or "large").ToList();
                Check(order.SequenceEqual(["large", "small"]), "Roster does not put larger Warriors first");
            }
            Verify(true);
            Click(window, "PlayModeButton"); Verify(true);
            Capture(window, imagePath + ".play.png");
            Click(window, "PlayModeButton");
            session.Commit(session.Army with { Detachment = "Awakened Dynasty", Detachments = [] });
            typeof(MainWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)!.Invoke(window, null);
            Verify(false);
            File.WriteAllText(imagePath + ".result.txt", "PASS: four mandatory bindings render once in build/play and disappear on detachment change; same-name units sort largest first in build/play.");
        }
        finally { window.Close(); }
    }
}
