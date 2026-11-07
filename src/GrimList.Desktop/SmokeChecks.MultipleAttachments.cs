using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    private static void CheckMultipleAttachments(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        using var store = WindowsServices.OpenStore(Path.Combine(root, "multiple-attachments-ui"));
        store.Import(DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(root, "multiple-leader-fixture.json"))));
        var units = store.LoadDataset()!.Units;
        var names = new[] { "Celestian Sacresants", "Junith Eruita", "Hospitaller", "Canoness" };
        var army = ArmyList.New("Adepta Sororitas") with { Name = "Leader and Support UI check", Entries = names.Select((n, i) =>
        {
            var unit = units.Single(u => u.Name == n); return new ArmyEntry(i.ToString(), unit.Key, unit.Name, unit.Options[0].Models);
        }).ToList() };
        store.Save(army);
        var window = new MainWindow(store.DirectoryPath) { Owner = owner }; window.Show();
        try
        {
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
            var roster = Find<ListBox>(window, "Roster");
            ComboBox AttachPicker() => OpenAttachmentPicker(Descendants<ComboBox>(Find<StackPanel>(window, "AttachmentEditor")).Single())!;
            roster.SelectedValue = "0"; window.UpdateLayout();
            AttachPicker().SelectedValue = "1";
            Check(store.LoadArmies().Single().Entries.Single(e => e.Id == "1").AttachedToId == "0", "Junith attachment did not persist");
            roster.SelectedValue = "0"; window.UpdateLayout();
            AttachPicker().SelectedValue = "2";
            Check(store.LoadArmies().Single().Entries.Count(e => e.AttachedToId == "0") == 2, "Hospitaller unavailable after attaching Junith");
            Check(Find<StackPanel>(window, "AttachmentSummary").Children.OfType<Button>().Count() == 2, "Combined attachment summary lost a character");
            Capture(window, imagePath + ".leader-support.png");
            Click(window, "UndoButton");
            Check(store.LoadArmies().Single().Entries.Count(e => e.AttachedToId == "0") == 1, "Undo did not remove only the second attachment");
            Click(window, "RedoButton");
            Check(store.LoadArmies().Single().Entries.Count(e => e.AttachedToId == "0") == 2, "Redo did not restore second attachment");
            roster.SelectedValue = "0"; window.UpdateLayout();
            Click(window, "PlayModeButton");
            Check(Find<StackPanel>(window, "AttachmentSummary").Children.OfType<Button>().Count() == 2, "Play mode lost attached characters");
            Click(window, "PlayModeButton");
            Click(window, "RulesButton");
            var limit = Find<TextBox>(window, "LimitBox");
            foreach (var width in new[] { 1440d, 1100d })
            {
                window.Width = width;
                foreach (var text in new[] { "2000", "1000000" })
                {
                    limit.Text = text; window.UpdateLayout();
                    var host = (ScrollViewer)limit.Template.FindName("PART_ContentHost", limit);
                    var last = limit.GetRectFromCharacterIndex(text.Length - 1, true);
                    var bounds = host.TransformToAncestor(limit).TransformBounds(new Rect(host.RenderSize));
                    Check(!last.IsEmpty && last.Top >= bounds.Top - 1 && last.Bottom <= bounds.Bottom + 1 && last.Right <= bounds.Right + 1,
                        "Maximum points digits are clipped in the content viewport");
                    var panel = (Grid)Find<Grid>(window, "RulesScreen").Children[0];
                    var center = limit.TranslatePoint(new Point(limit.ActualWidth / 2, limit.ActualHeight / 2), panel);
                    Check(Math.Abs(center.Y - panel.ActualHeight / 2) < 1, "Maximum points field is not vertically centered");
                }
            }
            limit.Text = "2000"; window.UpdateLayout(); Capture(window, imagePath + ".points-limit.png");
            Click(window, "CloseRulesButton");
            Check(store.LoadArmies().Single().Limit == 2000, "Points limit did not persist after closing Army Rules");
        }
        finally { window.Close(); }
    }
}
