using GrimList.Application;
using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private string exportTab = "Text list", exportText = "", exportRoster = "", exportProblem = "", armyCode = "";
    private bool pdfRules = true, pdfAllWeapons;
    private ArmyList? exportArmy;
    private Dataset? exportData;
    private void PrepareExport(ArmySession session)
    {
        if (ReferenceEquals(exportArmy, session.Army) && ReferenceEquals(exportData, session.Dataset)) return;
        exportText = RosterExport.Text(session.Army, session.Dataset); exportRoster = ""; exportProblem = ""; armyCode = "";
        try { exportRoster = YellowscribeExport.Roster(session.Army, session.Dataset); }
        catch (InvalidDataException ex) { exportProblem = ex.Message; }
        exportArmy = session.Army; exportData = session.Dataset;
    }
    private string ExportFileName => string.Concat(Session.Army.Name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c)).Trim() is { Length: > 0 } name ? name : "army";
    private void RenderExport()
    {
        var body = new VerticalStackLayout { Padding = 16, Spacing = 14 };
        body.Add(Ui.Text(Session.Army.Name + " · " + Session.Army.Faction, 18, true));
        body.Add(Ui.Text(Session.Result.ValidationStatus, 14, true, Session.Result.Validated ? Color.FromArgb("#9DCFAD") : Color.FromArgb("#DCB87D")));
        var tabs = Ui.Picker("Export format", "export-format"); tabs.ItemsSource = new[] { "Text list", "PDF + datasheets", "Yellowscribe / TTS" }; tabs.SelectedItem = exportTab;
        tabs.SelectedIndexChanged += async (_, _) =>
        { if (!rendering && tabs.SelectedItem is string selected) await Execute(() => { exportTab = selected; return Task.CompletedTask; }); };
        body.Add(tabs);
        if (exportTab == "Text list")
        {
            body.Add(Ui.Text("Units grouped by category, with attached Leaders and Support beneath their bodyguard. Includes selected equipment, enhancements, Warlord and points.", 14, colour: Ui.Muted));
            var preview = new Label { Text = exportText, FontSize = 12, TextColor = Colors.White,
                BackgroundColor = Ui.Input, LineBreakMode = LineBreakMode.WordWrap, Padding = 10,
                AutomationId = "export-text-preview" };
            preview.HandlerChanged += (_, _) =>
            {
                if (preview.Handler?.PlatformView is Android.Widget.TextView native)
                    native.SetTextIsSelectable(true);
            };
            var actions = new VerticalStackLayout { Padding = new Thickness(16, 8), Spacing = 8 };
            actions.Add(Action("Copy text", () => clipboard.SetTextAsync(exportText), "copy-export"));
            actions.Add(Action("Save .txt", () => SaveExport("txt", "text/plain", exportText), "save-text", Ui.Surface));
            var layout = new Grid { RowDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) } };
            layout.Add(body);
            layout.Add(Ui.Scroll(preview), 0, 1);
            layout.Add(actions, 0, 2);
            SetPage(Heading("Export army"), layout);
            return;
        }
        else if (exportTab == "PDF + datasheets")
        {
            body.Add(Ui.Text("A printable roster and datasheet pack", 20, true));
            body.Add(Toggle("Include army and detachment rules", pdfRules, value => { pdfRules = value; return Task.CompletedTask; }, false));
            body.Add(Toggle("All available weapon profiles", pdfAllWeapons, value => { pdfAllWeapons = value; return Task.CompletedTask; }, false));
            body.Add(Action("Save PDF", async () =>
            {
                var army = Session.Army; var data = Session.Dataset;
                var bytes = await Task.Run(() => pdfExport.Create(army, data, pdfRules, pdfAllWeapons));
                if (await documents.SaveAsync(ExportFileName + ".pdf", "application/pdf", bytes))
                    await DisplayAlertAsync("Export army", "PDF saved.", "OK");
            }, "save-pdf"));
        }
        else
        {
            body.Add(Ui.Text("Take this army into Tabletop Simulator", 20, true));
            body.Add(Ui.Text("Save a Yellowscribe-compatible roster file, or send this roster to yellowscribe.link to receive a code for its TTS mod. The file includes model profiles, selected equipment and attachment notes.", 14));
            body.Add(Ui.Text("Leaders remain separate units with attachment notes. Notes and attachment effects do not automatically change TTS stats.", 13, colour: Ui.Muted));
            body.Add(Ui.Text(exportProblem.Length == 0 ? "Ready to export." : exportProblem, 14, colour: Color.FromArgb("#DCB87D")));
            // A generic XML MIME type makes Android's document picker append .xml to the .ros name.
            var save = Action("Save .ros file", () => SaveExport("ros", "application/x-battlescribe-roster", exportRoster), "save-ros"); save.IsEnabled = exportProblem.Length == 0; body.Add(save);
            var send = Action("Send to Yellowscribe — get code", SendYellowscribe, "send-yellowscribe", Ui.Surface); send.IsEnabled = exportProblem.Length == 0; body.Add(send);
            body.Add(Ui.Text("Army code", 14, true));
            var code = Ui.Entry("Army code", armyCode, "army-code"); code.IsReadOnly = true; body.Add(code);
            if (armyCode.Length > 0) body.Add(Action("Copy code", () => clipboard.SetTextAsync(armyCode), "copy-army-code", Ui.Surface));
            body.Add(Ui.Text("Codes expire. Import the code into the Yellowscribe TTS mod soon after generating it.", 12, colour: Ui.Muted));
        }
        SetPage(Heading("Export army"), Ui.Scroll(body));
    }
    private async Task SaveExport(string extension, string mime, string text)
    {
        if (await documents.SaveAsync(ExportFileName + "." + extension, mime, System.Text.Encoding.UTF8.GetBytes(text)))
            await DisplayAlertAsync("Export army", "File saved.", "OK");
    }
    private async Task SendYellowscribe()
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
        armyCode = ""; activityText.Text = "Sending roster to Yellowscribe…";
        armyCode = await YellowscribeClient.Upload(client, exportRoster);
    }
}
