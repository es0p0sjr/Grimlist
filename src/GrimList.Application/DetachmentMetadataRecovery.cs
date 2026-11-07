using System.IO.Compression;
using GrimList.Core;

namespace GrimList.Application;

public static class DetachmentMetadataRecovery
{
    public record Pack(string SourceHash, List<DetachmentDefinition> Detachments, List<FactionContext> Contexts);
    public static Pack Create(Dataset data) => new(data.Source.ContentHash, data.Detachments ?? [], data.FactionContexts ?? []);
    private static readonly Lazy<List<Pack>> bundled = new(() =>
    {
        using var stream = typeof(DetachmentMetadataRecovery).Assembly.GetManifestResourceStream("GrimList.Application.Resources.detachment-metadata.json.gz");
        if (stream == null) return [];
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return DataJson.Read<List<Pack>>(reader.ReadToEnd());
    });
    public static Dataset ApplyBundled(Dataset data) => Apply(data, bundled.Value);
    public static Dataset Apply(Dataset data, IEnumerable<Pack> packs)
    {
        var contexts = (data.FactionContexts ?? []).ToDictionary(c => c.Faction);
        var metadata = packs.Where(p => p.SourceHash.Length > 0).ToDictionary(p => p.SourceHash, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var detachments = (data.Detachments ?? []).Select(d =>
        {
            if (!metadata.TryGetValue((d.Source ?? data.Source).ContentHash, out var pack)) return d;
            var match = pack.Detachments.FirstOrDefault(m => m.Faction == d.Faction && UnitIdentity.Normalize(m.Name) == UnitIdentity.Normalize(d.Name));
            if (match == null) return d;
            if (!contexts.ContainsKey(d.Faction) && pack.Contexts.FirstOrDefault(c => c.Faction == d.Faction) is { } context)
            { contexts.Add(context.Faction, context); changed = true; }
            if (d.Applicability != null && d.Cost.Length > 0) return d;
            changed = true;
            return d with { Cost = d.Cost.Length > 0 ? d.Cost : match.Cost, Applicability = d.Applicability ?? match.Applicability };
        }).ToList();
        return changed ? data with { Detachments = detachments, FactionContexts = contexts.Values.ToList() } : data;
    }
}
