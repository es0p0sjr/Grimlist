using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace GrimList.Core;

/// <summary>
/// Read-only editing view of an imported dataset. Import/merge produces a new Dataset;
/// published dataset collections must not be mutated in place. Index lifetime is the
/// dataset object's lifetime, independent of armies, selections, filters and undo.
/// Weak identity keys also make record `with` copies build their own indexes.
/// </summary>
public sealed class DatasetSnapshot
{
    private static readonly ConditionalWeakTable<Dataset, DatasetSnapshot> snapshots = new();
    public static DatasetSnapshot For(Dataset data) => snapshots.GetValue(data, d => new(d));
    public Dataset Dataset { get; }
    public SourceInfo Source => Dataset.Source;
    public FrozenDictionary<string, UnitDefinition> UnitsByIdentity { get; }
    public IReadOnlyList<string> Factions { get; }
    public ReferenceIndex References { get; }
    private DatasetSnapshot(Dataset data)
    {
        Dataset = data;
        Dictionary<string, UnitDefinition> identities = new(StringComparer.Ordinal);
        foreach (var unit in data.Units)
        {
            // Preserve FindUnit's first matching entry semantics, including aliases.
            identities.TryAdd(unit.Key, unit); identities.TryAdd(unit.NameKey, unit);
            foreach (var alias in unit.Aliases ?? []) identities.TryAdd(alias, unit);
            _ = ImportedUnitFacts.For(unit).OriginFaction;
        }
        UnitsByIdentity = identities.ToFrozenDictionary(StringComparer.Ordinal);
        Factions = Array.AsReadOnly(data.Units.Select(u => u.Faction).Distinct().Order().ToArray());
        References = new(data);
    }
}

/// <summary>Imported-only facts. Never stores a detachment, army count or dynamic rule result.</summary>
public sealed class ImportedUnitFacts
{
    private static readonly ConditionalWeakTable<UnitDefinition, ImportedUnitFacts> facts = new();
    public static ImportedUnitFacts For(UnitDefinition unit) => facts.GetValue(unit, u => new(u));
    public string NameKey { get; }
    public FrozenSet<string> Membership { get; }
    private readonly Lazy<string> origin;
    public string OriginFaction => origin.Value;
    private ImportedUnitFacts(UnitDefinition unit)
    {
        NameKey = $"{unit.Faction.Trim().ToUpperInvariant()}|{unit.Name.Trim().ToUpperInvariant()}";
        var all = (unit.Profile?.Keywords ?? []).Select(UnitIdentity.Normalize)
            .Append(UnitIdentity.Normalize(unit.Category)).ToList();
        Membership = all.Concat(all.Where(k => k.StartsWith("FACTION", StringComparison.Ordinal)).Select(k => k[7..]))
            .ToFrozenSet(StringComparer.Ordinal);
        origin = new(() => AlliedRules.ResolveOriginFaction(unit));
    }
}
