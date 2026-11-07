using GrimList.Core;

namespace GrimList.Application;

public sealed partial class FactionLibrary
{
    internal record DependencyRow(string Input, bool Available);
    internal record DependencyCache(string Version, Dictionary<string, DependencyRow> Rows);

    private static string RecoveryEvidenceRevision(string directory)
    {
        var root = Path.Combine(directory, "sources");
        if (!Directory.Exists(root)) return "none";
        try
        {
            // Downloaded archives are immutable, commit-pinned files. Discover new
            // or replaced evidence without reading all archive bytes on every launch.
            return ContentHash(Directory.EnumerateDirectories(root, "bsdata-*").Order(StringComparer.Ordinal).Select(d => new {
                Name = Path.GetFileName(d), Files = new[] { "source.json", "repository.zip" }.Select(name => {
                    var file = new FileInfo(Path.Combine(d, name));
                    return new { name, Length = file.Exists ? file.Length : -1, Written = file.Exists ? file.LastWriteTimeUtc.Ticks : 0 };
                }).ToArray()
            }).ToArray());
        }
        catch (Exception error) when (CacheError(error)) { return Guid.NewGuid().ToString("N"); } // Do not certify inaccessible evidence.
    }

    // Display dates/archive identity are not inputs to runtime pricing/eligibility.
    // Recovery checks still consume the original complete sources before hashing.
    private static SourceInfo? SemanticSource(SourceInfo? source) => source == null ? null : new(source.Name, "", "", "", Edition: source.Edition);
    private static string Owner(string faction) => Key(faction) is "CORE" or "GLOBAL" ? "" : Key(faction);
    private static Dataset MetadataFor(Dataset data, string owner, bool semantic = true) => data with {
        Source = semantic ? SemanticSource(data.Source)! : data.Source, Units = [], ReferenceCards = [],
        Detachments = data.Detachments?.Where(d => Owner(d.Faction) == owner).Select(d => semantic ? d with { Source = SemanticSource(d.Source) } : d).ToList(),
        FactionContexts = data.FactionContexts?.Where(c => Owner(c.Faction) == owner).ToList(),
        DetachmentSources = data.DetachmentSources?.Where(s => Owner(s.Faction) == owner).Select(s => semantic ? s with {
            Source = SemanticSource(s.Source)!, Retired = s.Retired.Select(r => r with { RetiredBy = SemanticSource(r.RetiredBy)! }).ToList() } : s).ToList(),
        ReferenceCoverage = data.ReferenceCoverage?.Where(c => Owner(c.Faction) == owner).Select(c => semantic ? c with { Source = SemanticSource(c.Source) } : c).ToList(),
        // Keep whole multi-faction packs: they explicitly declare shared ownership.
        RulesPacks = data.RulesPacks?.Where(p => p.Factions.Count == 0 ? owner == "" : p.Factions.Any(f => Owner(f) == owner)).ToList()
    };

    private Dictionary<string, string[]> BuildDependencies(Dataset data, List<string> factions)
    {
        var path = Path.Combine(Root, "routes.json.gz");
        var contract = LibraryRecoveryVersion.Availability;
        DependencyCache previous;
        try { previous = Read<DependencyCache>(path); if (previous.Version != contract) previous = new(contract, []); }
        catch (Exception error) when (CacheError(error)) { previous = new(contract, []); }
        var next = new DependencyCache(contract, []);
        var groups = data.Units.GroupBy(u => Key(u.Faction)).ToDictionary(g => g.Key, g => g.ToList());
        // Exactly the facts read by UnitAvailability/AlliedRules; a price or weapon
        // edit cannot change which factions may be offered as allies.
        var candidates = groups.ToDictionary(g => g.Key, g => ContentHash(g.Value.Select(u => new {
            u.Faction, u.Name, u.Category, u.Profile?.Keywords, u.RequiredDetachments,
            ProfileEdition = u.ProfileSource?.Edition, PointsEdition = u.PointsSource?.Edition
        }).ToArray()));
        var requiredNames = data.Units.SelectMany(u => u.RequiredDetachments ?? []).Distinct().ToArray();
        Dictionary<string, string[]> result = [];
        foreach (var faction in factions)
        {
            var key = Key(faction);
            var names = (data.Detachments ?? []).Where(d => d.Faction == faction).Select(d => d.Name)
                .Concat(requiredNames).Distinct().ToArray();
            // Includes all detachment routes, so undo or changing detachment needs
            // no further library load. Preserve the full evaluator as the oracle.
            var contexts = new[] { Array.Empty<string>(), names }.Concat(names.Select(n => new[] { n }))
                .Select(ds => ArmyList.New(faction) with { Game = data.Game, Limit = 2000, Detachments = ds.ToList() }).ToArray();
            var host = ContentHash(new { Metadata = MetadataFor(data, key), Global = MetadataFor(data, ""), Names = names });
            List<string> available = [];
            foreach (var (candidate, units) in groups)
            {
                var pair = key + "/" + candidate;
                var input = Hash(contract + "/" + host + "/" + candidates[candidate]);
                if (previous.Rows.TryGetValue(pair, out var row) && row.Input == input) DependencyReuses++;
                else
                {
                    DependencyCalculations++;
                    row = new(input, candidate == key || units.Any(u => contexts.Any(a => UnitAvailability.Problem(a, u, data) == null)));
                }
                next.Rows[pair] = row;
                if (row.Available) available.Add(candidate);
            }
            result[key] = available.Append(key).Distinct().Order(StringComparer.Ordinal).ToArray();
        }
        // One bounded table of the current host/candidate pairs; stale pairs drop out.
        try { Write(path, next); } catch (Exception error) when (CacheError(error)) { }
        return result;
    }

    private string RelativeOrder(string[] keys)
    {
        var parts = keys.Where(manifest.Parts.ContainsKey).Select(k => (Key: k, Part: manifest.Parts[k])).ToArray();
        return ContentHash(new {
            Units = parts.SelectMany(p => p.Part.Units.Select((u, i) => (u.Position, Id: p.Key + "/" + i))).OrderBy(u => u.Position).Select(u => u.Id).ToArray(),
            Cards = parts.SelectMany(p => p.Part.Cards.Select((c, i) => (c.Position, Id: p.Key + "/" + i))).OrderBy(c => c.Position).Select(c => c.Id).ToArray()
        });
    }
}
