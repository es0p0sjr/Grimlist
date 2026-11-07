using System.IO;
using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;
internal static partial class SmokeChecks
{
    private static void CheckMultiDispositions(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var fixture = Path.Combine(root, "multi-disposition-fixture.json");
        if (!File.Exists(fixture)) return;
        var path = Path.Combine(root, "multi-disposition-ui");
        using var store = WindowsServices.OpenStore(path);
        store.Import(DataJson.Read<Dataset>(File.ReadAllText(fixture)));
        store.Save(ArmyList.New("Death Guard") with { Name = "Multiple disposition checks", Detachments = ["Dual disposition fixture"], Disposition = "Take and Hold" });
        var window = new MainWindow(path) { Owner = owner, ShowInTaskbar = false }; string? failure = null;
        window.ContentRendered += (_, _) =>
        {
            try
            {
                void OpenArmy() => Find<WrapPanel>(Find<HomeView>(window, "Home"), "TilePanel").Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Single(b => b.Tag is ArmyList)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ArmyList Saved() => store.LoadArmies().Single();
                OpenArmy(); Click(window, "RulesButton");
                var picker = Find<ComboBox>(window, "DispositionPicker");
                Check(picker.IsEnabled && picker.Items.Cast<string>().SequenceEqual(["Purge the Foe", "Take and Hold"]), "One detachment did not offer both dispositions");
                picker.SelectedItem = "Purge the Foe";
                Check(Saved().Disposition == "Purge the Foe" && Find<TextBlock>(window, "DispositionHint").Text.Contains("Dual disposition fixture"), "Second disposition did not save or show its source detachment");
                Click(window, "RulesUndoButton"); Check(Saved().Disposition == "Take and Hold", "Undo lost disposition");
                Click(window, "RulesRedoButton"); Check(Saved().Disposition == "Purge the Foe", "Redo lost second disposition");
                Capture(window, imagePath + ".multi-dispositions.png");
                Click(window, "CloseRulesButton"); Click(window, "NewArmyButton"); OpenArmy(); Click(window, "RulesButton");
                Check((string)picker.SelectedItem == "Purge the Foe", "Reopening reset second disposition");
                DetachmentAction(window, "CurrentDetachments", "Dual disposition fixture").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(picker.Items.Count == 0 && Saved().Disposition == "", "Removing detachment retained its disposition options");
                Click(window, "RulesUndoButton");
                Check(picker.Items.Count == 2 && (string)picker.SelectedItem == "Purge the Foe", "Undo removal did not restore both options and selection");

                var selector = new DetachmentSelector();
                var preview = new Window { Content = selector, Width = 1100, Height = 760, Owner = window, ShowInTaskbar = false };
                selector.Begin(ArmyList.New("Death Guard"), store.LoadDataset());
                preview.ContentRendered += (_, _) =>
                {
                    try
                    {
                        DetachmentAction(selector, "OptionsList", "Dual disposition fixture").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var options = Find<ComboBox>(selector, "DispositionPicker");
                        Check(options.IsEnabled && options.Items.Count == 2, "Setup did not offer both options from one detachment");
                        Check(Find<TextBlock>(selector, "PreviewMeta").Text.Contains("Take and Hold / Purge the Foe"), "Detachment preview hides second disposition");
                        options.SelectedItem = "Purge the Foe";
                        Check(selector.Draft.Disposition == "Purge the Foe", "Setup lost second choice");
                    }
                    catch (Exception ex) { failure = ex.ToString(); }
                    finally { preview.Close(); }
                };
                preview.ShowDialog();
            }
            catch (Exception ex) { failure = ex.ToString(); }
            finally { window.Close(); }
        };
        window.ShowDialog(); Check(failure == null, failure ?? "");
    }
}
