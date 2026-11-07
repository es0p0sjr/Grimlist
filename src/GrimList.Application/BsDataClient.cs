using GrimList.Core;
using GrimList.Platform;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace GrimList.Application;

public sealed class BsDataClient(HttpClient client)
{
    public async Task<MfmDownload> Download(string edition, string cacheRoot, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        if (edition is not ("10" or "11")) throw new InvalidDataException("Choose edition 10 or 11.");
        var repository = $"BSData/wh40k-{edition}e";
        progress?.Report("Checking the BSData repository version…");
        var metadata = JsonNode.Parse(await Fetch($"https://api.github.com/repos/{repository}/commits/main", 1_000_000, cancellation))
            ?? throw new InvalidDataException("GitHub returned no version metadata.");
        var sha = metadata["sha"]?.ToString() ?? "";
        if (!Regex.IsMatch(sha, "^[a-fA-F0-9]{40}$")) throw new InvalidDataException("GitHub returned an invalid revision.");
        var timestamp = metadata["commit"]?["committer"]?["date"]?.ToString();
        if (!DateTimeOffset.TryParse(timestamp, out var date)) throw new InvalidDataException("GitHub returned no revision date.");
        progress?.Report("Downloading all armies and shared catalogues…");
        // Pin the whole package to one commit so linked entries cannot mix revisions.
        var bytes = await Fetch($"https://codeload.github.com/{repository}/zip/{sha}", 50_000_000, cancellation);
        progress?.Report("Reading categories, points and datasheets…");
        var source = new SourceInfo("BSData · All armies", sha, date.UtcDateTime.ToString("yyyy-MM-dd"),
            $"https://github.com/{repository}/tree/{sha}", Edition: edition, ContentHash: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var parsed = await Task.Run(() => BsDataImporter.ImportAll(BsDataDocument.ReadArchive(bytes), source, progress, cancellation, authoritativeDownload: true), cancellation);
        cancellation.ThrowIfCancellationRequested();
        progress?.Report("Caching downloaded catalogues and datasheets…");
        var directory = Path.Combine(cacheRoot, $"bsdata-{edition}-{sha}"); Directory.CreateDirectory(directory);
        // Keep the raw package too: a normalized failed entry cannot be re-parsed.
        await File.WriteAllBytesAsync(Path.Combine(directory, "repository.zip"), bytes, cancellation);
        await File.WriteAllTextAsync(Path.Combine(directory, "source.json"), DataJson.Write(source), cancellation);
        // Stream the normalized cache too: this can be much larger than the ZIP.
        await using (var output = File.Create(Path.Combine(directory, "dataset.json")))
            await JsonSerializer.SerializeAsync(output, parsed.Dataset, DataJson.Options, cancellation);
        await File.WriteAllTextAsync(Path.Combine(directory, "warnings.json"), DataJson.Write(parsed.Warnings), cancellation);
        return new(parsed.Dataset, parsed.Warnings, directory);
    }
    public static string? CachedArchive(Dataset current, string cacheRoot)
    {
        if (!Directory.Exists(cacheRoot)) return null;
        return Directory.EnumerateDirectories(cacheRoot, $"bsdata-{current.Source.Edition}-*")
            .Where(d => File.Exists(Path.Combine(d, "repository.zip")) && File.Exists(Path.Combine(d, "source.json")))
            .OrderByDescending(d => Path.GetFileName(d) == $"bsdata-{current.Source.Edition}-{current.Source.Version}")
            .ThenByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "repository.zip")))
            .Select(d => Path.Combine(d, "repository.zip")).FirstOrDefault();
    }
    public static async Task<ImportPreview> ReadCached(string archive, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var source = DataJson.Read<SourceInfo>(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(archive)!, "source.json"), cancellation));
        var bytes = await File.ReadAllBytesAsync(archive, cancellation);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(source.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cached repository does not match its recorded source. Download it again or choose a local repository ZIP.");
        return await Task.Run(() => BsDataImporter.ImportAll(BsDataDocument.ReadArchive(bytes), source, progress, cancellation), cancellation);
    }
    internal async Task<byte[]> Fetch(string url, int limit, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("GrimList/0.1");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.RequestMessage?.RequestUri != request.RequestUri)
            throw new InvalidDataException("Unexpected GitHub redirect.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub returned {(int)response.StatusCode}. You can also save the BSData repository ZIP in a browser and choose it here.");
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("BSData response exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var output = new MemoryStream(); var buffer = new byte[64 * 1024]; int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("BSData response exceeds the size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
        }
        return output.ToArray();
    }
}
