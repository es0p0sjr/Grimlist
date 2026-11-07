using GrimList.Core;
using GrimList.Platform;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GrimList.Application;

public record MfmDownload(Dataset Dataset, List<string> Warnings, string CacheDirectory);

// Uses public HTML/PDF documents only. No credentials, app endpoints or search-engine caches.
public sealed class OfficialMfmClient(HttpClient client, IPdfTextExtractor? pdfExtractor = null)
{
    public async Task<MfmDownload> Download(string? faction, string cacheRoot, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        progress?.Report("Finding the official MFM…");
        var downloads = await Read(new Uri(OfficialSources.DownloadsUrl), cancellation);
        var date = PublicationDate(downloads);
        var links = Links(downloads, new Uri(OfficialSources.DownloadsUrl));
        var pdf = links.FirstOrDefault(u => u.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(u.AbsoluteUri, "munitorum|field.manual", RegexOptions.IgnoreCase));
        var web = links.FirstOrDefault(u => u.Host == "mfm.warhammer-community.com") ?? new Uri(OfficialSources.MfmUrl);
        List<(string Name, string Text)> originals = [("downloads.html", downloads)];
        List<UnitDefinition> units = [];
        List<string> warnings = [];
        SourceInfo source;
        if (pdf != null && !links.Any(u => u.Host == "mfm.warhammer-community.com"))
        {
            progress?.Report("Downloading the official MFM PDF…");
            var bytes = await ReadBytes(pdf, cancellation);
            var temporary = Path.Combine(cacheRoot, "working"); Directory.CreateDirectory(temporary);
            var path = Path.Combine(temporary, Guid.NewGuid() + ".pdf");
            try
            {
                await File.WriteAllBytesAsync(path, bytes, cancellation);
                var text = await (pdfExtractor ?? throw new NotSupportedException("PDF import is not configured on this platform.")).ExtractAsync(path, temporary, cancellation);
                source = new("Munitorum Field Manual", MfmImporter.GuessVersion(text), date, pdf.AbsoluteUri, Edition: Edition(text), ContentHash: Hash(bytes));
                var parsed = MfmImporter.ParseText(text, source);
                units = parsed.Dataset.Units.Where(u => faction == null || u.Faction == faction).Select(u => u with { PointsSource = source }).ToList();
                if (units.Count == 0) throw new InvalidDataException("The official PDF did not contain the requested faction.");
                warnings.AddRange(parsed.Warnings);
                var cache = Path.Combine(cacheRoot, source.ContentHash); Directory.CreateDirectory(cache);
                File.Copy(path, Path.Combine(cache, "source.pdf"), true);
                await File.WriteAllTextAsync(Path.Combine(cache, "source.json"), DataJson.Write(source), cancellation);
                return new(new(source, units), warnings, cache);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        var index = await Read(web, cancellation); originals.Add(("index.html", index));
        var version = MfmImporter.GuessVersion(MfmImporter.HtmlToText(index));
        var edition = Edition(MfmImporter.HtmlToText(index));
        var pages = DiscoverFactions(index, web);
        if (faction != null) pages = pages.Where(p => p.Key == faction).ToDictionary();
        if (pages.Count == 0) throw new InvalidDataException("The public MFM page did not expose faction links. Import a downloaded PDF or copied text instead.");
        foreach (var page in pages)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report($"Downloading {page.Key} ({units.Select(u => u.Faction).Distinct().Count() + 1}/{pages.Count})…");
            var html = await Read(page.Value, cancellation);
            originals.Add((Slug(page.Key) + ".html", html));
            var plain = MfmImporter.HtmlToText(html);
            var pageVersion = MfmImporter.GuessVersion(plain);
            if (version != "Unspecified" && pageVersion != "Unspecified" && version != pageVersion)
                throw new InvalidDataException("The MFM changed version during download. Retry to fetch a consistent set.");
            var pageSource = new SourceInfo("Munitorum Field Manual", pageVersion == "Unspecified" ? version : pageVersion, date, page.Value.AbsoluteUri,
                Edition: edition, ContentHash: Hash(Encoding.UTF8.GetBytes(html)));
            var parsed = ParseFactionHtml(html, page.Key, pageSource);
            units.AddRange(parsed.Dataset.Units.Select(u => u with { PointsSource = pageSource }));
            warnings.AddRange(parsed.Warnings);
        }
        var digest = Hash(Encoding.UTF8.GetBytes(string.Join("\n", originals.Select(p => p.Text))));
        source = new("Munitorum Field Manual", version, date, web.AbsoluteUri, Edition: edition, ContentHash: digest);
        var dataset = new Dataset(source, units); dataset.Validate();
        var directory = Path.Combine(cacheRoot, digest); Directory.CreateDirectory(directory);
        foreach (var item in originals) await File.WriteAllTextAsync(Path.Combine(directory, item.Name), item.Text, cancellation);
        await File.WriteAllTextAsync(Path.Combine(directory, "dataset.json"), DataJson.Write(dataset), cancellation);
        warnings.Add("Downloaded public MFM unit prices only. Equipment, enhancement and detachment costs are not included. Legends status is not classified.");
        if (date.Length == 0) warnings.Add("The publication date could not be read. No date has been guessed.");
        return new(dataset, warnings.Distinct().ToList(), directory);
    }

    public static ImportPreview ParseFactionHtml(string html, string faction, SourceInfo source)
    {
        var text = MfmImporter.HtmlToText(html);
        // Ignore site navigation. Nested spans/divs sometimes split a single price across lines.
        var start = Regex.Match(text, @"^\s*UNITS\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!start.Success) throw new InvalidDataException($"{faction}: the page has no recognizable Units section. Nothing applied.");
        text = text[(start.Index + start.Length)..];
        text = Regex.Replace(text, @"(\d+)\s+models?\s+(\d[\d,]*)\s+pts?\b", "$1 models $2 pts", RegexOptions.IgnoreCase);
        var parsed = MfmImporter.ParseText(faction + "\nUNITS\n" + text, source, faction);
        if (parsed.Dataset.Units.Any(u => u.Faction != faction)) throw new InvalidDataException("Faction navigation leaked into unit data. Import stopped.");
        var expectedUnits = Regex.Matches(text, @"YOUR\s+(?:UNIT\s+COSTS?|1ST\b[^\r\n]*COSTS?)", RegexOptions.IgnoreCase).Count;
        if (expectedUnits == 0 || parsed.Dataset.Units.Count != expectedUnits || parsed.Warnings.Any(w => w.StartsWith("Skipped points", StringComparison.Ordinal) || (w.StartsWith("Not imported", StringComparison.Ordinal) && Regex.IsMatch(w, @"\bmodels?\b", RegexOptions.IgnoreCase))))
            throw new InvalidDataException($"{faction}: some prices could not be assigned to a unit. Import stopped to protect existing points.");
        return parsed;
    }

    public static Dictionary<string, Uri> DiscoverFactions(string html, Uri baseUri)
    {
        var links = Links(html, baseUri);
        return MfmImporter.Factions.Select(f => (Faction: f, Uri: links.FirstOrDefault(u => u.Host == "mfm.warhammer-community.com" && u.AbsolutePath.TrimEnd('/').Equals("/en/" + Slug(f), StringComparison.OrdinalIgnoreCase))))
            .Where(p => p.Uri != null).ToDictionary(p => p.Faction, p => p.Uri!);
    }
    public static string Slug(string faction) => Regex.Replace(faction.ToLowerInvariant().Replace("’", "").Replace("'", ""), @"[^a-z0-9]+", "-").Trim('-');
    public static string PublicationDate(string html)
    {
        var match = Regex.Match(MfmImporter.HtmlToText(html), @"Munitorum Field Manual[\s\S]{0,1000}?updated\s+(\d{2}/\d{2}/\d{4})", RegexOptions.IgnoreCase);
        return match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "dd/MM/yyyy", out var date) ? date.ToString("yyyy-MM-dd") : "";
    }
    private static string Edition(string text)
    {
        var m = Regex.Match(text, @"\b(\d+)(?:st|nd|rd|th)?\s+edition\b", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool PublicOfficial(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.Host is "www.warhammer-community.com" or "warhammer-community.com" or "assets.warhammer-community.com" or "mfm.warhammer-community.com";
    private static List<Uri> Links(string html, Uri baseUri) => Regex.Matches(html, "<a\\b[^>]*?href\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)
        .Select(m => Uri.TryCreate(baseUri, WebUtility.HtmlDecode(m.Groups[1].Value), out var uri) ? uri : null)
        .Where(u => u != null && PublicOfficial(u)).Select(u => u!).Distinct().ToList();
    private async Task<string> Read(Uri uri, CancellationToken cancellation) => Encoding.UTF8.GetString(await ReadBytes(uri, cancellation));
    private async Task<byte[]> ReadBytes(Uri uri, CancellationToken cancellation)
    {
        for (var redirect = 0; redirect < 5; redirect++)
        {
            if (!PublicOfficial(uri)) throw new InvalidDataException("The download link is not a public Warhammer Community document.");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location != null)
            { uri = new Uri(uri, response.Headers.Location); continue; }
            if (!PublicOfficial(response.RequestMessage?.RequestUri ?? uri)) throw new InvalidDataException("The source redirected outside Warhammer Community.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new HttpRequestException("The official site declined automated access (403). Use a downloaded PDF or paste its public MFM text. Your local data is unchanged.");
            response.EnsureSuccessStatusCode();
            const int limit = 50 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The official document exceeds 50 MB.");
            using var input = await response.Content.ReadAsStreamAsync(cancellation);
            using var buffer = new MemoryStream(); var chunk = new byte[81920]; int count;
            while ((count = await input.ReadAsync(chunk, cancellation)) > 0)
            {
                if (buffer.Length + count > limit) throw new InvalidDataException("The official document exceeds 50 MB.");
                buffer.Write(chunk, 0, count);
            }
            return buffer.ToArray();
        }
        throw new HttpRequestException("Too many redirects from the official source.");
    }
}
