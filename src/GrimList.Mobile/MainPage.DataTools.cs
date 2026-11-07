using System.Security.Cryptography;
using SourceInfo = GrimList.Core.SourceInfo;
using GrimList.Application;
using GrimList.Core;
using GrimList.Platform;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private PricingRepairPreview? repairPreview;
    private List<ArmyImpact> repairImpacts = [];
    private string manualName = "Manual source", manualVersion = "", manualDate = "", manualUrl = "", manualFaction = "", manualText = "";
    private async Task<ImportPreview> ReadSource(PickedDocument document, string edition, ImportLayer layer, CancellationToken token)
    {
        var ext = Path.GetExtension(document.Name).ToLowerInvariant();
        if (ext is not (".pdf" or ".txt")) return await workspace!.Library.ReadDocumentAsync(document, edition,
            new Progress<string>(s => activityText.Text = s), token);
        if (layer != ImportLayer.Points) throw new InvalidDataException("Select Points only for MFM PDF/text imports.");
        using var memory = new MemoryStream();
        var buffer = new byte[65536]; int count;
        while ((count = await document.Content.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + count > LibraryWorkflow.MaximumDocumentBytes) throw new InvalidDataException("Choose a source no larger than 128 MB.");
            await memory.WriteAsync(buffer.AsMemory(0, count), token);
        }
        var bytes = memory.ToArray(); string text;
        if (ext == ".pdf")
        {
            var path = Path.Combine(Microsoft.Maui.Storage.FileSystem.CacheDirectory, Guid.NewGuid() + ".pdf");
            try { await File.WriteAllBytesAsync(path, bytes, token); text = await pdfText.ExtractAsync(path, Path.GetDirectoryName(path)!, token); }
            finally { File.Delete(path); }
        }
        else text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        var source = new SourceInfo(document.Name, MfmImporter.GuessVersion(text), "", "", Edition: edition,
            ContentHash: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return await Task.Run(() => workspace!.Library.Parse(text, source, layer, manualFaction), token);
    }
    private View ManualSource(Func<string> edition) => Ui.Fold("Manual source / edit source text", () =>
    {
        var body = new VerticalStackLayout { Spacing = 8 };
        void Field(string label, string value, Action<string> update, string id)
        { var field = Ui.Entry(label, value, id); field.TextChanged += (_, e) => update(e.NewTextValue ?? ""); body.Add(field); }
        Field("Source name", manualName, s => manualName = s, "manual-source-name");
        Field("Version", manualVersion, s => manualVersion = s, "manual-version");
        Field("Date (YYYY-MM-DD)", manualDate, s => manualDate = s, "manual-date");
        Field("Source URL", manualUrl, s => manualUrl = s, "manual-url");
        Field("Fallback faction for MFM text", manualFaction, s => manualFaction = s, "manual-faction");
        var editor = new Editor { Text = manualText, Placeholder = "Paste/edit library JSON or MFM points text", HeightRequest = 240,
            TextColor = Colors.White, BackgroundColor = Ui.Input, AutomationId = "manual-source-text" };
        editor.TextChanged += (_, e) => manualText = e.NewTextValue ?? ""; body.Add(editor);
        body.Add(Action("Preview manual source", async () =>
        {
            var source = new SourceInfo(manualName, manualVersion, manualDate, manualUrl, Edition: edition());
            var parsed = await Task.Run(() => workspace!.Library.Parse(manualText, source, importLayer, manualFaction));
            await PreviewImport(parsed.Dataset, importLayer, parsed.Warnings, default);
        }, "preview-manual-source"));
        return body;
    }, Ui.Surface);
    private View RepairPrices() => Ui.Fold("Repair missing prices", () =>
    {
        var body = new VerticalStackLayout { Spacing = 8 };
        body.Add(Action("Scan current library", async () =>
        {
            var rows = await Task.Run(() => workspace!.Library.ScanPrices());
            await ShowReference("Missing prices", rows.Select(r => new RuleDefinition(r.Faction + " / " + r.Unit, r.Reason)));
        }, "scan-prices"));
        body.Add(Action("Preview repair from local source", () => PrepareRepair("local"), "repair-local", Ui.Surface));
        body.Add(Action("Preview repair from cached BSData", () => PrepareRepair("cached"), "repair-cached", Ui.Surface));
        body.Add(Action("Download BSData and preview repair", () => PrepareRepair("online"), "repair-online", Ui.Surface));
        if (repairPreview is { } preview)
        {
            body.Add(Ui.Text($"{preview.Recovered} recovered; {preview.Unresolved} unresolved. Review before applying.", 15, true));
            body.Add(ReviewList(preview.Rows.Select(r => $"{r.Faction} / {r.Unit}: {r.Status}\n{r.Prices}\n{r.Reason}")));
            body.Add(Ui.Fold("Saved-army effects", () => ReviewList(repairImpacts.Select(a => $"{a.Name}: {a.Before.Total} -> {a.After.Total} / {a.Limit} pts\n{string.Join("\n", a.After.Issues)}")), Ui.Surface));
            body.Add(Action("Export repair report", async () => { await documents.SaveAsync("pricing-repair.json", "application/json",
                System.Text.Encoding.UTF8.GetBytes(DataJson.Write(preview.Rows))); }, "export-repair"));
            var apply = Action("Apply price repair", async () =>
            {
                var backup = await Task.Run(() => workspace!.Library.ApplyRepair(preview));
                await Session.RunAsync(s => s.ReloadLibrary()); repairPreview = null;
                await DisplayAlertAsync("Price repair", "Applied. Backup saved in app data: " + Path.GetFileName(backup), "OK");
            }, "apply-repair"); apply.IsEnabled = preview.Recovered > 0; body.Add(apply);
            body.Add(Action("Discard repair preview", () => { repairPreview = null; return Task.CompletedTask; }, "cancel-repair", Ui.Surface));
        }
        return body;
    }, Ui.Surface);
    private async Task PrepareRepair(string provider)
    {
        var data = Session.Dataset ?? throw new InvalidDataException("Import a library first.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        importCancellation = cancellation; cancelImport.IsVisible = true;
        var progress = new Progress<string>(s => activityText.Text = s);
        try
        {
            Dataset incoming;
            if (provider == "local")
            {
                using var file = await files.PickAsync(cancellation.Token); if (file == null) return;
                incoming = (await ReadSource(file, data.Source.Edition, ImportLayer.Points, cancellation.Token)).Dataset;
            }
            else if (provider == "cached") incoming = (await BsDataClient.ReadCached(BsDataClient.CachedArchive(data,
                Path.Combine(workspace!.Repository.DirectoryPath, "sources")) ?? throw new InvalidDataException("No cached BSData archive. Choose a local source or download one."), progress, cancellation.Token)).Dataset;
            else incoming = (await workspace!.Library.DownloadAsync(true, data.Source.Edition, null, progress, cancellation.Token)).Dataset;
            var preview = await workspace!.Library.PreviewRepairAsync(incoming, cancellation.Token);
            var impacts = await Task.Run(() => workspace.Repository.LoadArmies().Select(a => new ArmyImpact(a.Name,
                ArmyCalculator.Calculate(a, data), ArmyCalculator.Calculate(a, preview.Dataset), a.Limit)).ToList(), cancellation.Token);
            repairPreview = preview; repairImpacts = impacts;
        }
        finally { importCancellation = null; cancelImport.IsVisible = false; }
    }
}
