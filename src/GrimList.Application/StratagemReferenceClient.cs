using System.Security.Cryptography;
using System.Text;
using GrimList.Core;

namespace GrimList.Application;

// Runs only as part of the explicit BSData download/preview. Both frontends use
// this path; opening an offline library never makes a network request.
public sealed class StratagemReferenceClient(HttpClient client)
{
    public async Task<MfmDownload> Refresh(MfmDownload download, string cache, IProgress<string>? progress = null,
        CancellationToken cancellation = default, Dataset? sourceManifest = null)
    {
        var data = download.Dataset;
        if (data.Source.Edition != "11") return download;
        var manifest = sourceManifest ?? ReferenceLibraryRecovery.ReadBundled();
        var groups = (data.Detachments ?? []).GroupBy(d => d.Faction).ToList();
        var downloads = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<byte[]>>>();
        using var concurrency = new SemaphoreSlim(3);
        var results = await Task.WhenAll(groups.Select(async group =>
        {
            await concurrency.WaitAsync(cancellation);
            string? sourceUrl = null;
            try
            {
                var ids = group.Select(d => d.FactionId).Where(id => id.Length > 0).ToHashSet();
                var mapped = (manifest.ReferenceCards ?? []).Where(c => c.Details?.Edition == "11"
                    && ids.Contains(c.Details.FactionId) && ReferenceModel.Key(c.Faction) == ReferenceModel.Key(group.Key))
                    .Select(c => c.Source?.Url).OfType<string>().Distinct().Where(AllowedUrl).ToList();
                if (mapped.Count != 1) return (Pack: (Dataset?)null, Warning: $"{group.Key}: stratagem refresh needs an unambiguous edition-specific source mapping. Existing references retained; coverage is unverified.");
                sourceUrl = mapped[0];
                progress?.Report("Refreshing Stratagem references: " + group.Key);
                // Several catalogue contexts can point to one shared page. Fetch
                // that observation once, including failures, then validate each scope.
                var body = await downloads.GetOrAdd(sourceUrl, url => new(() => Fetch(url, cancellation))).Value;
                var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
                var source = new SourceInfo("wahapedia reference source", hash + " | parser=" + StratagemHtmlReader.Version, "", mapped[0],
                    DateTimeOffset.UtcNow.ToString("O"), "11", hash);
                var pack = StratagemHtmlReader.Parse(new UTF8Encoding(false, true).GetString(body), data, group.Key, source, allowPartial: true);
                var obsolete = (pack.ReferenceCoverage ?? []).Where(c => c.Authoritative && ReferenceCatalogueBaseline.Bundled.Any(b =>
                    b.CoverageKey == ReferenceCatalogueBaseline.Key(c) && b.ReferenceHash == hash
                    && b.Fingerprint != ReferenceCatalogueCompatibility.Fingerprint(data, c.Faction, c.Detachment))).ToList();
                if (obsolete.Count > 0) pack = pack with {
                    ReferenceCards = pack.ReferenceCards!.Where(c => !obsolete.Any(s => ReferenceMerge.OwnedScope(c, s))).ToList(),
                    ReferenceCoverage = pack.ReferenceCoverage!.Select(c => obsolete.Contains(c)
                        ? c with { State = "Partial", Mode = "Partial", Authoritative = false, Source = null, CatalogueFingerprint = null,
                            ReviewReason = "The source still serves a known older snapshot for " + c.Detachment + ". Existing references retained; current rules remain unverified." } : c).ToList()
                };
                cancellation.ThrowIfCancellationRequested();
                // One last validated observation per owner, bounded storage. User
                // history remains in repository snapshots, not unbounded HTTP caches.
                var folder = Path.Combine(cache, "stratagem-references", ReferenceModel.Owner(source));
                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(Path.Combine(folder, "raw.html"), body, cancellation);
                await File.WriteAllTextAsync(Path.Combine(folder, "source.json"), DataJson.Write(source), cancellation);
                await File.WriteAllTextAsync(Path.Combine(folder, "coverage.json"), DataJson.Write(pack.ReferenceCoverage), cancellation);
                var incomplete = pack.ReferenceCoverage!.Where(c => !c.Authoritative).Select(c => c.Detachment + ": " + c.ReviewReason).ToList();
                return (Pack: (Dataset?)pack, Warning: incomplete.Count == 0 ? null : group.Key + ": " + string.Join(" ", incomplete));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Text.DecoderFallbackException
                or System.Text.RegularExpressions.RegexMatchTimeoutException || ex is OperationCanceledException && !cancellation.IsCancellationRequested)
            {
                cancellation.ThrowIfCancellationRequested();
                var warning = $"{group.Key}: Stratagem references could not be refreshed: {ex.Message} Existing references retained; coverage is unverified. No empty scope was inferred.";
                var failure = sourceUrl == null ? null : data with { Units = [], ReferenceCards = [], ReferenceCoverage = group.Select(d =>
                    new ReferenceCoverage("source-" + ReferenceModel.Hash(sourceUrl.TrimEnd('/')), "11", "Stratagems", group.Key, d.Name,
                        "Partial", "Partial", false, ReviewReason: warning)).ToList() };
                return (Pack: (Dataset?)failure, Warning: (string?)warning);
            }
            finally { concurrency.Release(); }
        }));
        return download with { Dataset = data with {
            ReferenceCards = (data.ReferenceCards ?? []).Concat(results.Where(r => r.Pack != null).SelectMany(r => r.Pack!.ReferenceCards ?? [])).ToList(),
            ReferenceCoverage = (data.ReferenceCoverage ?? []).Concat(results.Where(r => r.Pack != null).SelectMany(r => r.Pack!.ReferenceCoverage ?? [])).ToList()
        }, Warnings = download.Warnings.Concat(results.Select(r => r.Warning).OfType<string>()).ToList() };
    }

    private async Task<byte[]> Fetch(string url, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && final.AbsoluteUri.TrimEnd('/') != url.TrimEnd('/'))
            throw new InvalidDataException("Reference page redirected to a different source.");
        if (response.Content.Headers.ContentLength > StratagemHtmlReader.MaximumBytes) throw new InvalidDataException("Reference page exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream(); var bytes = new byte[32768];
        int read;
        while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0)
        {
            if (buffer.Length + read > StratagemHtmlReader.MaximumBytes) throw new InvalidDataException("Reference page exceeds the size limit.");
            buffer.Write(bytes, 0, read);
        }
        return buffer.ToArray();
    }

    private static bool AllowedUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "wahapedia.ru" && uri.IsDefaultPort
        && uri.AbsolutePath.StartsWith("/wh40k11ed/factions/", StringComparison.Ordinal)
        && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
