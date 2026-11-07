using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Application;

/// <summary>Shared by Windows and Android; download/preview never applies a library.</summary>
public sealed class LibraryDownloadClient(HttpClient client, IPdfTextExtractor? pdfExtractor = null)
{
    public async Task<MfmDownload> Download(bool community, string edition, string? faction, string cache,
        IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        if (edition is not ("10" or "11")) throw new InvalidDataException("Choose edition 10 or 11.");
        if (!community)
        {
            if (edition == "10") return await new OfficialMfmClient(client, pdfExtractor).Download(faction, cache, progress, cancellation);
            var mirror = await new MfmMirrorClient(client).Download(cache, progress, cancellation);
            if (faction == null) return mirror;
            var units = mirror.Dataset.Units.Where(u => UnitIdentity.Normalize(u.Faction) == UnitIdentity.Normalize(faction)).ToList();
            if (units.Count == 0) throw new InvalidDataException($"No MFM points found for {faction}.");
            return mirror with { Dataset = mirror.Dataset with { Units = units } };
        }
        var catalogue = await new BsDataClient(client).Download(edition, cache, progress, cancellation);
        if (edition != "11") return catalogue;
        try
        {
            var mirror = await new MfmMirrorClient(client).Download(cache, progress, cancellation);
            progress?.Report("Combining MFM unit points with BSData datasheets…");
            var overlay = MfmPoints.Overlay(catalogue.Dataset, mirror.Dataset);
            catalogue = catalogue with { Dataset = overlay.Dataset,
                Warnings = overlay.Warnings.Take(1).Concat(mirror.Warnings).Concat(catalogue.Warnings).Concat(overlay.Warnings.Skip(1)).ToList() };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException
            || ex is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            cancellation.ThrowIfCancellationRequested();
            catalogue = catalogue with { Warnings = catalogue.Warnings.Prepend("MFM points could not be refreshed: " + ex.Message
                + " Existing compatible MFM prices will be retained when applied; other units use BSData prices. Review before applying.").ToList() };
        }
        return await new StratagemReferenceClient(client).Refresh(catalogue, cache, progress, cancellation);
    }
}
