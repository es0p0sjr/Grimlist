using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private Screen utilityReturnScreen = Screen.Home, reviewReturnScreen = Screen.Home;
    private void ToggleMenu()
    {
        if (busy) return;
        if (menu != null) { CloseMenu(); return; }
        var overlay = new Grid { BackgroundColor = Color.FromArgb("#66000000") };
        // Only the backdrop dismisses; tapping or scrolling menu rows must not bubble to it.
        var backdrop = new BoxView { Color = Colors.Transparent }; Ui.Tap(backdrop, CloseMenu); overlay.Add(backdrop);
        var items = new VerticalStackLayout { Spacing = 2 };
        var hasArmy = Session.IsOpen && screen is Screen.Editor or Screen.Unit or Screen.Rules or Screen.Export;
        void Add(string title, string description, string glyph, string id, Func<Task> action, bool enabled = true, bool danger = false)
        {
            var labels = new VerticalStackLayout { Spacing = 4, InputTransparent = true };
            var colour = danger ? Color.FromArgb("#F09E91") : Color.FromArgb("#F4F4F4");
            labels.Add(Ui.Text(title, 15, true, colour)); labels.Add(Ui.Text(description, 11, colour: Ui.Muted));
            var content = new Grid { ColumnDefinitions = { new(new GridLength(28)), new(GridLength.Star) }, Padding = new Thickness(12, 10), InputTransparent = true };
            content.Add(Ui.Text(glyph, 21, colour: colour)); content.Add(labels, 1);
            var row = new Grid { MinimumHeightRequest = 62, Opacity = enabled ? 1 : .5 };
            var button = Action("", action, id, Colors.Transparent); button.IsEnabled = enabled;
            SemanticProperties.SetDescription(button, title); row.Add(button); row.Add(content); items.Add(row);
        }
        void Separator() => items.Add(new BoxView { Color = Ui.Line, HeightRequest = 1, Margin = new Thickness(12, 6) });
        Add("Home", "Your saved armies", "⌂", "menu-home", () => Go(Screen.Home));
        Add("New army", "Choose a faction and detachment", "+", "menu-new", StartNewArmy);
        if (hasArmy) Add("Export army", "Text, PDF or Yellowscribe", "↥", "menu-export", () => OpenUtility(Screen.Export));
        Separator();
        Add("Update data", "Download BSData + MFM points and review changes", "↓", "menu-update", () =>
        { completeFaction = false; return DownloadLibrary(Edition, ImportLayer.Combined); });
        if (hasArmy)
        {
            Add(playMode ? "Build mode" : "Play mode", playMode ? "Edit composition and equipment" : "Equipped weapons only", playMode ? "✎" : "▷", "menu-mode", async () =>
            {
                playMode = !playMode;
                if (playMode && previewKey.Length > 0)
                {
                    previewKey = ""; unitTab = "datasheet";
                    await Session.RunAsync(s => s.SelectEntry(s.SelectedEntry?.Id ?? s.Army.Entries.FirstOrDefault()?.Id));
                }
                await Go(screen == Screen.Unit && Session.SelectedEntry != null ? Screen.Unit : Screen.Editor);
            });
            Add("Army rules", "Detachment rules, enhancements and stratagems", "≡", "menu-rules", () => Go(Screen.Rules));
            Add("Battle tracker", "Start or resume a battle with this army", "⚑", "menu-battle", async () =>
            {
                CloseMenu();
                await Navigation.PushModalAsync(new BattlePage(Session.Army, Session.Dataset, FileSystem.Current.AppDataDirectory));
            });
        }
        Separator();
        Add("Report a Bug", "Review and share a bug report", "!", "menu-bug-report", async () => await Navigation.PushModalAsync(new BugReportPage(Session.Dataset, hasArmy ? Session.Army : null, documents)));
        Add("Manage data", "Import and review your library", "⚙", "menu-library", () => OpenUtility(Screen.Library));
        Add("Export dataset", "Save your library as JSON", "↥", "menu-export-dataset", ExportDataset, Session.Dataset != null);
        Add("BSData ↗", "Open the community catalogue repository", "↗", "menu-community", async () =>
            await Microsoft.Maui.ApplicationModel.Launcher.Default.OpenAsync($"https://github.com/BSData/wh40k-{Edition}e"));
        if (hasArmy)
        {
            Separator();
            Add("Delete army", "Remove this saved roster", "×", "menu-delete", DeleteCurrentArmy, danger: true);
        }
        var panel = Ui.Card(Ui.Scroll(items), Ui.Surface, 6);
        panel.WidthRequest = Math.Min(320, Math.Max(240, Width - 24)); panel.MaximumHeightRequest = Math.Max(120, Height - 84);
        panel.HorizontalOptions = LayoutOptions.End; panel.VerticalOptions = LayoutOptions.Start; panel.Margin = new Thickness(12, 60, 12, 12);
        overlay.Add(panel); menu = overlay; root.Add(overlay);
    }
    private string Edition => Session.Dataset?.Source.Edition == "10" ? "10" : "11";
    private async Task StartNewArmy()
    {
        if (Session.Dataset == null) { await OpenUtility(Screen.Library); return; }
        draftName = ""; draftLimit = "2000"; draftDetachments.Clear(); await Go(Screen.Factions);
    }
    private Task OpenUtility(Screen next)
    {
        if (screen is not (Screen.Library or Screen.Export or Screen.ImportReview)) utilityReturnScreen = screen;
        return Go(next);
    }
    private async Task DeleteCurrentArmy()
    {
        var army = Session.Army;
        if (!await DisplayAlertAsync("Delete army?", $"Delete {army.Name}? This removes the saved roster.", "Delete", "Cancel")) return;
        await Session.RunAsync(s => s.DeleteArmy(army.Id));
        previewKey = ""; playMode = false; await Go(Screen.Home);
    }
    private async Task ExportDataset()
    {
        byte[] bytes = [];
        await Session.RunAsync(s => bytes = System.Text.Encoding.UTF8.GetBytes(DataJson.Write(ReferenceModel.ExportDataset(s.LoadFullDataset() ?? throw new InvalidDataException("Import points first.")))));
        if (await documents.SaveAsync("grimlist-points.json", "application/json", bytes))
            await DisplayAlertAsync("Export dataset", "Library exported, including rules references and source metadata.", "OK");
    }
}
