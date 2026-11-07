using GrimList.Application;
using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private LibraryPreview? pendingImport;
    private ImportLayer importLayer = ImportLayer.Combined;
    private bool completeFaction;
    private string importEdition = "";
    private bool officialProvider;

    private void RenderLibrarySettings()
    {
        var body = new VerticalStackLayout { Spacing = 14, Padding = 18 };
        if (Session.Dataset is { } data)
            body.Add(Ui.Text($"{data.Source.Name}\nEdition {data.Source.Edition} · {Session.LibraryUnitCount:N0} units\n{data.Source.Date}\nRevision {data.Source.Version}", 13, colour: Ui.Muted));
        else body.Add(Ui.Text("No library imported yet."));
        var provider = Ui.Picker("Source provider", "source-provider");
        provider.ItemsSource = new[] { "BSData + MFM points", "MFM points only" }; provider.SelectedIndex = officialProvider ? 1 : 0;
        provider.SelectedIndexChanged += (_, _) => officialProvider = provider.SelectedIndex == 1;
        body.Add(provider);
        body.Add(Ui.Text("Download data → review changes → apply. 11th edition combines BSData datasheets with MFM unit points from the versioned mirror.", 14, colour: Ui.Muted));
        body.Add(Ui.Text("11th-edition BSData updates also check mapped Wahapedia stratagem pages. Any unavailable or incomplete references are flagged for review.", 12, colour: Ui.Muted));
        body.Add(Ui.Text("Complete BSData downloads mark superseded detachments obsolete when applied. Saved armies are kept for review. Points-only updates do not retire detachments.", 12, colour: Ui.Muted));
        var edition = Ui.Picker("Edition", "import-edition"); edition.ItemsSource = new[] { "10th edition", "11th edition" };
        edition.SelectedIndex = (importEdition.Length > 0 ? importEdition : Edition) == "10" ? 0 : 1;
        edition.SelectedIndexChanged += (_, _) => importEdition = edition.SelectedIndex == 0 ? "10" : "11";
        body.Add(edition);
        var layer = Ui.Picker("Import layer", "import-layer"); layer.ItemsSource = new[] { "Points only", "Datasheets only", "Points + datasheets" };
        layer.SelectedIndex = (int)importLayer; layer.SelectedIndexChanged += (_, _) => { if (layer.SelectedIndex >= 0) importLayer = (ImportLayer)layer.SelectedIndex; };
        body.Add(layer);
        body.Add(Ui.Text("Stratagems and rules references require Datasheets only or Points + datasheets. MFM updates unit points only; equipment, enhancement and detachment costs keep their catalogue sources.", 12, colour: Ui.Muted));
        body.Add(Toggle("Complete faction points", completeFaction, value => { completeFaction = value; return Task.CompletedTask; }, false));
        body.Add(Ui.Text("Leave complete faction points off for partial updates. Points imports preserve profiles; datasheet imports preserve prices.", 12, colour: Ui.Muted));
        string SelectedEdition() => edition.SelectedIndex == 0 ? "10" : "11";
        body.Add(Action("↓ Download data", () => DownloadLibrary(SelectedEdition(), officialProvider ? ImportLayer.Points : importLayer, !officialProvider), "download-data"));
        body.Add(Action("Choose local source", () => Import(SelectedEdition(), officialProvider ? ImportLayer.Points : importLayer), "import-library", Ui.Surface));
        body.Add(Action("Import rules references", ImportReferences, "import-references", Ui.Surface));
        body.Add(Ui.Text("BSData ZIP / catalogue · GrimList JSON · MFM PDF/text (Points only)", 13, colour: Ui.Muted));
        body.Add(Ui.Text("PDF text import requires Android 15+. Review extracted points before applying. JSON/text imports also work on older devices.", 12, colour: Ui.Muted));
        body.Add(ManualSource(SelectedEdition));
        body.Add(RepairPrices());
        SetPage(Heading("Manage data"), Ui.Scroll(body));
    }
    private async Task Import(string edition, ImportLayer layer)
    {
        using var document = await files.PickAsync(); if (document == null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        importCancellation = cancellation; cancelImport.IsVisible = true;
        try
        {
            activityText.Text = "Reading source · Loading points, datasheets and rules…";
            var parsed = await ReadSource(document, edition, layer, cancellation.Token);
            await PreviewImport(parsed.Dataset, layer, parsed.Warnings, cancellation.Token);
        }
        finally { importCancellation = null; cancelImport.IsVisible = false; }
    }
    private async Task ImportReferences()
    {
        using var document = await files.PickAsync(); if (document == null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        importCancellation = cancellation; cancelImport.IsVisible = true;
        try
        {
            activityText.Text = "Preparing reference changes…";
            pendingImport = await workspace!.Library.PrepareReferenceImportAsync(document, cancellation.Token);
            if (screen != Screen.ImportReview) reviewReturnScreen = screen;
            await Go(Screen.ImportReview);
        }
        finally { importCancellation = null; cancelImport.IsVisible = false; }
    }
    private async Task DownloadLibrary(string edition, ImportLayer layer, bool community = true)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        importCancellation = cancellation; cancelImport.IsVisible = true; activityText.Text = "Downloading source…";
        try
        {
            var downloaded = await workspace!.Library.DownloadAsync(community, edition, null,
                new Progress<string>(s => activityText.Text = s), cancellation.Token);
            await PreviewImport(downloaded.Dataset, layer, downloaded.Warnings, cancellation.Token);
        }
        finally { importCancellation = null; cancelImport.IsVisible = false; }
    }
    private async Task PreviewImport(Dataset data, ImportLayer layer, IEnumerable<string> warnings, CancellationToken cancellation)
    {
        activityText.Text = "Preparing import preview…";
        var preview = await workspace!.Library.PrepareAsync(data, layer, completeFaction, warnings, cancellation,
            new Progress<string>(message => activityText.Text = message));
        cancellation.ThrowIfCancellationRequested(); pendingImport = preview;
        if (screen != Screen.ImportReview) reviewReturnScreen = screen;
        await Go(Screen.ImportReview);
    }
    private static View ReviewList(IEnumerable<string> values)
    {
        var list = new CollectionView { HeightRequest = 290, SelectionMode = SelectionMode.None, ItemsSource = values.ToList() };
        list.ItemTemplate = new DataTemplate(() =>
        {
            var label = Ui.Text("", 13); label.Margin = new Thickness(8, 7); label.SetBinding(Label.TextProperty, "."); return label;
        });
        return list;
    }
    private void RenderImportReview()
    {
        var body = new VerticalStackLayout { Padding = 16, Spacing = 14 };
        if (pendingImport is not { } preview) { body.Add(Ui.Text("Choose a source to preview.")); SetPage(Heading("Review import"), body); return; }
        var data = preview.Dataset;
        var source = preview.IncomingSource ?? data.Source;
        body.Add(Ui.Text($"{source.Name}\nEdition {source.Edition} · {source.Version}\n{source.Date}", 16, true));
        body.Add(Ui.Text($"{data.Units.Count:N0} units · {preview.Prices.Count:N0} price changes · {preview.Armies.Count} affected armies.\nReview the changes before applying. Your active library has not changed.", 14));
        body.Add(Ui.Text(MfmPoints.Summary(data), 13, colour: Ui.Muted));
        foreach (var pack in data.RulesPacks ?? [])
            body.Add(Ui.Text($"{pack.Source.Name}: revision {pack.Revision} · {pack.Status} · {pack.Coverage} coverage\n{pack.Notes}", 14));
        if (preview.Stratagems is { } stratagems)
        {
            body.Add(Ui.Text(stratagems.Message, 14, true));
            body.Add(Ui.Fold("Detachment stratagem coverage after import", () => ReviewList(stratagems.Coverage), Ui.Surface));
        }
        body.Add(Ui.Fold("Review units", () => ReviewList(data.Units.Select(u => $"{u.Faction} / {u.Name}\n{string.Join(" · ", u.Options.Select(o => $"Copy {o.FromCopy}+: {o.Label}"))}\n{u.PointsSource?.Name} · {u.PointsSource?.Version} · {u.PointsSource?.Date}")), Ui.Surface));
        body.Add(Ui.Fold($"Changes · {preview.Changes.Count}", () => ReviewList(preview.Changes), Ui.Surface));
        body.Add(Ui.Fold("Reference changes and matching", () => ReviewList(preview.ReferenceChanges.Select(c =>
            $"{c.Faction} / {c.Detachment} / {c.Name}: {c.Status}\n{c.Message}")), Ui.Surface));
        body.Add(Ui.Fold($"Warnings · {preview.Warnings.Count}", () => ReviewList(preview.Warnings), Ui.Surface));
        body.Add(Ui.Fold($"Saved armies · {preview.Armies.Count}", () => ReviewList(preview.Armies.Select(a => $"{a.Name}: {a.Before.Total} → {a.After.Total} / {a.Limit} points\n{string.Join("\n", a.After.Issues)}")), Ui.Surface));
        var apply = Action("Apply update", async () =>
        {
            activityText.Text = "Saving update · Saving points, datasheets and rules…";
            try { await workspace!.ApplyLibraryAsync(preview, progress: new Progress<string>(message => activityText.Text = message)); }
            catch (LibraryRefreshException)
            {
                pendingImport = null; await Go(Screen.Library); throw;
            }
            activityText.Text = "Finishing update · Refreshing your army view…";
            pendingImport = null; await Go(reviewReturnScreen);
        }, "apply-library");
        apply.IsEnabled = preview.CanApply; body.Add(apply);
        if (!preview.CanApply) body.Add(Ui.Text("Conflicting reference sources: cancel and resolve the source selection before importing again.", 14, colour: Ui.Muted));
        body.Add(Action("Cancel", Back, "cancel-library-preview", Ui.Surface));
        SetPage(Heading("Review import"), Ui.Scroll(body));
    }
}
