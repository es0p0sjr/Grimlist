using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    private static void CheckNominations(MainWindow owner, string directory, string imagePath)
    {
        foreach (var fixture in new[] { "houndpack", "subterranean" })
        {
            var root = Path.GetDirectoryName(directory)!;
            var path = Path.Combine(root, fixture + "-nomination-fixture.json");
            if (!File.Exists(path)) continue;
            using var store = WindowsServices.OpenStore(Path.Combine(root, fixture + "-nomination-ui"));
            store.Import(DataJson.Read<Dataset>(File.ReadAllText(path)));
            var data = store.LoadDataset()!;
            var detachment = data.Detachments!.Single();
            var army = ArmyList.New(detachment.Faction) with { Name = "Muster nominations UI", Detachment = detachment.Name };
            var active = MusterEffects.Rules(army, data).Single();
            var unit = data.Units.First(u => MusterEffects.Eligible(army, data, active, new("candidate", u.Key, u.Name, 1)));
            army = army with { Entries = Enumerable.Range(0, 4).Select(i => new ArmyEntry("nom-" + i, unit.Key, unit.Name, 1)).ToList() };
            store.Save(army);
            var window = new MainWindow(store.DirectoryPath) { Owner = owner, ShowInTaskbar = false }; string? failure = null;
            window.ContentRendered += (_, _) =>
            {
                try
                {
                    typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
                    var rule = MusterEffects.Rules(army, data).Single();
                    for (int i = 0; i < rule.Rule.Maximum; i++)
                    {
                        var panel = Find<StackPanel>(window, "MusterPanel");
                        var expander = panel.Children.OfType<Expander>().Single(); expander.IsExpanded = true; window.UpdateLayout();
                        var choices = Descendants<CheckBox>(panel).ToList();
                        Check(choices.Count == 4, "Eligible nominations missing from editor");
                        choices[i].IsChecked = true; choices[i].RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                    }
                    var saved = store.LoadArmies().Single();
                    Check(WarlordRules.Candidates(saved, data).Count == rule.Rule.Maximum, "UI nomination did not grant Character");
                    var roster = Find<ListBox>(window, "Roster"); roster.SelectedValue = "nom-0"; window.UpdateLayout();
                    Check(Find<ComboBox>(window, "WarlordPicker").Items.Count == rule.Rule.Maximum + 1, "UI Warlord picker ignored nominations");
                    Check(Find<ItemsControl>(window, "UnitEnhancementChoices").Items.Count >= 1, "UI Enhancement picker ignored nominations");
                    Capture(window, imagePath + "." + fixture + "-nominations.png");
                    Click(window, "UndoButton"); Check(store.LoadArmies().Single().MusterSelections!.Count == rule.Rule.Maximum - 1, "UI undo did not remove nomination");
                    Click(window, "RedoButton"); Check(store.LoadArmies().Single().MusterSelections!.Count == rule.Rule.Maximum, "UI redo lost nomination");
                }
                catch (Exception ex) { failure = ex.ToString(); }
                finally { window.Close(); }
            };
            window.ShowDialog(); Check(failure == null, failure ?? "");
        }
    }
}
