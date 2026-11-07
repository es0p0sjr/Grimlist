using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GrimList.Core;

namespace GrimList.Application;

public sealed class MfmMirrorClient(HttpClient client)
{
    public const string Repository = "BSData/wh40k-11e-mfm";

    public async Task<MfmDownload> Download(string cacheRoot, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var github = new BsDataClient(client);
        progress?.Report("Checking the MFM points snapshot…");
        var metadata = JsonNode.Parse(await github.Fetch($"https://api.github.com/repos/{Repository}/commits/main", 1_000_000, cancellation));
        var sha = metadata?["sha"]?.ToString() ?? "";
        if (!Regex.IsMatch(sha, "^[a-fA-F0-9]{40}$")) throw new InvalidDataException("MFM mirror returned an invalid revision.");
        var bytes = await github.Fetch($"https://codeload.github.com/{Repository}/zip/{sha}", 15_000_000, cancellation);
        var source = new SourceInfo("Munitorum Field Manual · BSData mirror", sha, "", $"https://github.com/{Repository}/blob/{sha}",
            Edition: "11", ContentHash: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var parsed = ReadArchive(bytes, source, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var directory = Path.Combine(cacheRoot, "mfm-11-" + sha); Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "repository.zip"), bytes, cancellation);
        await File.WriteAllTextAsync(Path.Combine(directory, "dataset.json"), DataJson.Write(parsed.Dataset), cancellation);
        await File.WriteAllTextAsync(Path.Combine(directory, "source.json"), DataJson.Write(parsed.Dataset.Source), cancellation);
        return new(parsed.Dataset, parsed.Warnings, directory);
    }

    public static ImportPreview ReadArchive(byte[] bytes, SourceInfo source, CancellationToken cancellation = default)
    {
        if (source.Edition != "11") throw new InvalidDataException("The MFM mirror is for 11th edition only.");
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var files = archive.Entries.Where(e => Regex.IsMatch(e.FullName, @"^[^/]+/data/[a-z0-9-]+\.yaml$") && e.Name != "meta.yaml").ToList();
        if (files.Count == 0 || files.Count > 100) throw new InvalidDataException("MFM mirror has no recognizable faction files.");
        var manifests = archive.Entries.Where(e => Regex.IsMatch(e.FullName, @"^[^/]+/data/meta\.yaml$")).ToList();
        if (manifests.Count != 1 || manifests[0].Length > 1_000_000) throw new InvalidDataException("MFM mirror manifest is missing or invalid.");
        using (var reader = new StreamReader(manifests[0].Open(), Encoding.UTF8))
        {
            var manifest = reader.ReadToEnd().Replace("\r", "");
            var section = Regex.Match(manifest, @"(?m)^factions:\n((?:  - [a-z0-9-]+\n?)+)");
            var expected = Regex.Matches(section.Groups[1].Value, @"  - ([a-z0-9-]+)").Select(m => m.Groups[1].Value + ".yaml").ToList();
            if (!section.Success || expected.Count != expected.Distinct().Count() || files.Count != expected.Count
                || !expected.ToHashSet().SetEquals(files.Select(f => f.Name)))
                throw new InvalidDataException("MFM mirror is missing faction files from its manifest; nothing applied.");
        }
        List<UnitDefinition> units = []; List<string> warnings = []; List<SourceInfo> sources = [];
        long length = 0;
        foreach (var file in files)
        {
            cancellation.ThrowIfCancellationRequested();
            length += file.Length;
            if (file.Length > 2_000_000 || length > 15_000_000) throw new InvalidDataException("MFM mirror exceeds the extraction limit.");
            using var reader = new StreamReader(file.Open(), Encoding.UTF8);
            var parsed = MfmMirrorParser.Parse(reader.ReadToEnd(), source);
            if (!parsed.Dataset.Source.Url.EndsWith("/" + file.Name, StringComparison.Ordinal))
                throw new InvalidDataException("MFM faction filename does not match its metadata.");
            units.AddRange(parsed.Dataset.Units); warnings.AddRange(parsed.Warnings); sources.Add(parsed.Dataset.Source);
        }
        var versions = sources.Select(s => s.Version.Split(" · ")[0]).Distinct().Order();
        var combined = source with { Version = string.Join(", ", versions) + " · " + source.Version,
            Date = sources.Select(s => s.Date).Order().Last() };
        var data = new Dataset(combined, units); data.Validate();
        warnings.Insert(0, "MFM prices are a community snapshot of the official website. Per-faction versions and observation dates are retained; a successful download does not guarantee the mirror is current.");
        return new(data, warnings);
    }
}
