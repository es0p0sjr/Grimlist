using System.IO;
using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;
internal static partial class SmokeChecks
{
    private static void CheckAvailability(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var fixture = Path.Combine(root, "availability-fixture.json");
        if (!File.Exists(fixture)) return;
        var path = Path.Combine(root, "availability-ui");
        using var store = WindowsServices.OpenStore(path);
        store.Import(DataJson.Read<Dataset>(File.ReadAllText(fixture)));
        store.Save(ArmyList.New("Death Guard") with { Name = "Detachment availability", Detachments = ["Test host"] });
        var window = new MainWindow(path) { Owner = owner, ShowInTaskbar = false }; string? failure = null;
        window.ContentRendered += (_, _) =>
        {
            try
            {
                Find<WrapPanel>(Find<HomeView>(window, "Home"), "TilePanel").Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Single(b => b.Tag is ArmyList)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var picker = Find<ListBox>(window, "UnitPicker");
                bool Has(string name) => picker.Items.Cast<UnitDefinition>().Any(u => u.Name == name);
                Check(Has("Native daemon") && !Has("Daemon ally"), "Detachment filter hid native daemon or exposed ally");
                Click(window, "RulesButton"); Click(window, "SelectDetachmentsButton");
                DetachmentAction(window, "AvailableDetachments", "Tallyband Summoners").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Click(window, "CloseRulesButton");
                Check(Has("Daemon ally"), "Adding detachment did not refresh unit picker");
                picker.SelectedItem = picker.Items.Cast<UnitDefinition>().Single(u => u.Name == "Daemon ally");
                Click(window, "AddButton");
                Click(window, "RulesButton");
                DetachmentAction(window, "CurrentDetachments", "Tallyband Summoners").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!Has("Daemon ally") && store.LoadArmies().Single().Entries.Count == 1, "Detachment removal must hide new choices and retain existing entries");
                Check(Find<Border>(window, "ErrorBanner").Visibility == Visibility.Collapsed, "Availability warning used enhancement error note");
                Check(Find<TextBlock>(window, "ValidationText").Text.Contains("requires Tallyband"), "Existing unavailable unit missing validation warning");
                var limit = Find<TextBox>(window, "LimitBox");
                Check(limit.TextAlignment == TextAlignment.Center && limit.VerticalAlignment == VerticalAlignment.Center, "Max points input is not centered");
                limit.Text = "bad"; limit.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                Check(Find<Border>(window, "ErrorBanner").Visibility == Visibility.Collapsed, "Unrelated input error appeared in unit enhancement note");
                limit.Text = "2000";
                Click(window, "RulesUndoButton"); Check(Has("Daemon ally"), "Undo failed to restore availability");
                Click(window, "RulesRedoButton"); Check(!Has("Daemon ally"), "Redo failed to hide unavailable ally");
                Click(window, "CloseRulesButton");
                Capture(window, imagePath + ".availability.png");
            }
            catch (Exception ex) { failure = ex.ToString(); }
            finally { window.Close(); }
        };
        window.ShowDialog(); Check(failure == null, failure ?? "");
    }
}
