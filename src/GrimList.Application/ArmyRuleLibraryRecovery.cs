using System.IO.Compression;
using System.Security.Cryptography;
using GrimList.Core;

namespace GrimList.Application;

public static class ArmyRuleLibraryRecovery
{
    private static readonly Lazy<List<ArmyRuleScope>> bundled = new(() =>
    {
        using var stream = typeof(ArmyRuleLibraryRecovery).Assembly.GetManifestResourceStream("GrimList.Application.Resources.army-rule-recovery.json.gz");
        if (stream == null) throw new InvalidDataException("Missing army-rule recovery evidence.");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return DataJson.Read<List<ArmyRuleScope>>(reader.ReadToEnd());
    });
    public static IReadOnlyList<ArmyRuleScope> BundledEvidence => bundled.Value;
    public static Dataset ApplyBundled(Dataset data) => Apply(data, BundledEvidence);
    public static Dataset Recover(Dataset data, string directory, Action<string>? diagnostic = null)
    {
        var evidence = BundledEvidence.ToList();
        var covered = Scopes(data, evidence).Select(s => s.Faction).ToHashSet();
        var missing = data.Units.Where(u => !covered.Contains(u.Faction)).Select(u => u.ProfileSource ?? data.Source)
            .Where(Pinned).Select(Revision).ToHashSet();
        var cache = Path.Combine(directory, "sources");
        if (missing.Count > 0 && Directory.Exists(cache))
            foreach (var folder in Directory.EnumerateDirectories(cache, "bsdata-*"))
            {
                var metadata = Path.Combine(folder, "source.json");
                var archive = Path.Combine(folder, "repository.zip");
                if (!File.Exists(metadata) || !File.Exists(archive)) continue;
                try
                {
                    var source = DataJson.Read<SourceInfo>(File.ReadAllText(metadata));
                    if (missing.Contains(Revision(source))) evidence.AddRange(ReadArchive(archive, source));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { diagnostic?.Invoke("Army-rule recovery skipped a cached source: " + ex.Message); }
            }
        return Apply(data, evidence, diagnostic);
    }
    private static string Revision(SourceInfo source) => source.Edition + "/" + source.ContentHash.ToUpperInvariant();
    private static bool Pinned(SourceInfo source) => source.Name.StartsWith("BSData", StringComparison.OrdinalIgnoreCase)
        && source.ContentHash.Length == 64;
    private static string Identity(ReferenceCard card) => card.Details?.SourceCardId ?? "";
    private static bool Owned(Dataset data, ReferenceCard card) => (data.RulesPacks ?? []).Any(p => RulesPacks.OwnsCard(p, card));
    private static bool SameSource(ReferenceCard card, SourceInfo source) => card.Source is { } s && Pinned(s) && Revision(s) == Revision(source);
    private static bool Excluded(ReferenceCard card, ArmyRuleScope scope)
    {
        if (Identity(card).Length > 0) return scope.Excluded.Contains(Identity(card)) && !scope.Unresolved.Contains(Identity(card))
            && scope.Cards.All(c => Identity(c) != Identity(card));
        // Older bundles omitted source entry IDs. A unique source name can bridge
        // that legacy format only when no positive rule has the same name.
        return scope.Cards.All(c => ReferenceModel.Key(c.Name) != ReferenceModel.Key(card.Name))
            && (scope.ExcludedNames ?? []).Count(p => scope.Excluded.Contains(p.Key) && !scope.Unresolved.Contains(p.Key)
                && ReferenceModel.Key(p.Value) == ReferenceModel.Key(card.Name)) == 1;
    }

    public static IEnumerable<ArmyRuleScope> Scopes(Dataset data, IEnumerable<ArmyRuleScope> evidence)
    {
        if (data.Game != "Warhammer 40,000") yield break;
        foreach (var group in evidence.GroupBy(s => (Revision(s.Source), s.Faction)))
        {
            // Conflicting evidence is not resolved by file order.
            var versions = group.DistinctBy(s => DataJson.Write(s)).ToList();
            if (versions.Count != 1) continue;
            var scope = versions[0];
            if (!Pinned(scope.Source) || scope.Source.Edition != data.Source.Edition) continue;
            var profiles = data.Units.Where(u => u.Faction == scope.Faction).Select(u => u.ProfileSource ?? data.Source).Distinct().ToList();
            if (profiles.Count == 0 || profiles.Any(p => !Pinned(p) || Revision(p) != Revision(scope.Source))) continue;
            var ids = (data.FactionContexts ?? []).Where(c => c.Faction == scope.Faction).Select(c => c.CatalogueId)
                .Concat((data.Detachments ?? []).Where(d => d.Faction == scope.Faction).Select(d => d.FactionId))
                .Where(id => id.Length > 0).Distinct().ToList();
            if (ids.Count == 0 || ids.Count == 1 && ids[0] == scope.CatalogueId) yield return scope;
        }
    }

    public static Dataset Apply(Dataset data, IEnumerable<ArmyRuleScope> evidence, Action<string>? diagnostic = null)
    {
        var scopes = Scopes(data, evidence).ToList();
        var contexts = (data.FactionContexts ?? []).ToList();
        foreach (var scope in scopes.Where(s => s.ContextKeywords != null))
            if (!contexts.Any(c => c.Faction == scope.Faction)) contexts.Add(new(scope.Faction, scope.CatalogueId, scope.ContextKeywords!));
        var changed = contexts.Count != (data.FactionContexts?.Count ?? 0);
        if (changed) data = data with { FactionContexts = contexts };
        var cards = (data.ReferenceCards ?? []).ToList();
        foreach (var scope in scopes)
        {
            var conflicts = scope.Cards.GroupBy(Identity).Where(g => g.Key.Length == 0 || g.Count() != 1).Select(g => g.Key).ToHashSet();
            foreach (var existing in cards.Where(c => c.Section == "Army Rules" && c.Faction == scope.Faction && SameSource(c, scope.Source)).ToList())
            {
                if (Owned(data, existing) || existing.Details?.Status is "Removed" or "Obsolete") continue;
                if (Excluded(existing, scope))
                {
                    cards[cards.IndexOf(existing)] = ReferenceModel.Canonical(existing, data.Source) with {
                        Details = ReferenceModel.Canonical(existing, data.Source).Details! with { Status = "Obsolete" } };
                    changed = true;
                }
            }
            foreach (var expected in scope.Cards)
            {
                if (conflicts.Contains(Identity(expected)) || scope.Unresolved.Contains(Identity(expected))
                    || scope.Excluded.Contains(Identity(expected)) || Owned(data, expected)) continue;
                var peers = cards.Where(c => c.Section == "Army Rules" && c.Faction == scope.Faction
                    && (Identity(c).Length > 0 && Identity(c) == Identity(expected)
                        || ReferenceModel.Key(c.Name) == ReferenceModel.Key(expected.Name))).ToList();
                // Manual cards, tombstones, different revisions and ambiguous identities win.
                if (peers.Count > 1) { diagnostic?.Invoke($"Ambiguous army rule: {scope.Faction}/{expected.Name}"); continue; }
                if (peers.SingleOrDefault() is { } existing)
                {
                    if (!SameSource(existing, scope.Source) || Owned(data, existing)
                        || existing.Details?.Status is "Removed" or "Obsolete"
                        || Identity(existing).Length > 0 && Identity(existing) != Identity(expected)
                        || existing.Applicability != null && DataJson.Write(existing.Applicability) != DataJson.Write(expected.Applicability)
                        || existing.Details?.FactionId is { Length: > 0 } factionId && factionId != scope.CatalogueId) continue;
                    var details = existing.Details ?? expected.Details!;
                    var recovered = existing with { Details = details with { SourceCardId = Identity(expected),
                        FactionId = scope.CatalogueId, Edition = scope.Source.Edition, SourceId = "bsdata:" + scope.Source.Edition,
                        SourceType = "BSData", Scope = "Faction", Detachments = [], Status = "Active" }, Detachment = "",
                        Applicability = existing.Applicability ?? expected.Applicability };
                    recovered = ReferenceMatching.Match(ReferenceModel.Canonical(recovered, data.Source), data);
                    if (recovered.Details!.Match is "Unresolved" or "Ambiguous") continue;
                    if (DataJson.Write(existing) == DataJson.Write(recovered)) continue;
                    cards[cards.IndexOf(existing)] = recovered; changed = true;
                }
                else
                {
                    var recovered = ReferenceMatching.Match(ReferenceModel.Canonical(expected, scope.Source), data);
                    if (recovered.Details!.Match is "Unresolved" or "Ambiguous") continue;
                    cards.Add(recovered); changed = true;
                }
            }
        }
        return changed ? data with { ReferenceCards = cards } : data;
    }

    // Release validation compares source identities and displayability, not card prose.
    public static List<string> ValidateCandidate(Dataset data, IEnumerable<ArmyRuleScope> evidence)
    {
        List<string> errors = [];
        var scopes = Scopes(data, evidence).ToList();
        var expectedData = data with { ReferenceCards = scopes.SelectMany(s => s.Cards).ToList() };
        foreach (var faction in data.Units.Where(u => Pinned(u.ProfileSource ?? data.Source)).Select(u => u.Faction).Distinct())
            if (!scopes.Any(s => s.Faction == faction)) errors.Add(faction + ": no unambiguous pinned army-rule evidence.");
        foreach (var scope in scopes)
        {
            if (scope.Cards.GroupBy(Identity).Any(g => g.Key.Length == 0 || g.Count() != 1)
                || scope.Cards.Any(c => scope.Excluded.Contains(Identity(c)) || scope.Unresolved.Contains(Identity(c))))
                errors.Add(scope.Faction + ": conflicting army-rule source evidence.");
            var actual = (data.ReferenceCards ?? []).Where(c => c.Section == "Army Rules" && c.Faction == scope.Faction).ToList();
            foreach (var expected in scope.Cards)
            {
                if (Owned(data, expected)) continue;
                var found = actual.Where(c => Identity(c) == Identity(expected)).ToList();
                if (found.Count != 1 || !SameSource(found[0], scope.Source)
                    || found[0].Details?.FactionId != scope.CatalogueId
                    || DataJson.Write(found[0].Applicability) != DataJson.Write(expected.Applicability)
                    || found[0].Details?.Status is "Removed" or "Obsolete" or "Unresolved")
                    errors.Add(scope.Faction + ": missing/conflicting army-rule identity " + expected.Name);
                else if (ReferenceMatching.Match(ReferenceModel.Canonical(found[0], data.Source), data).Details!.Match is "Unresolved" or "Ambiguous")
                    errors.Add(scope.Faction + ": undisplayable army rule " + expected.Name);
            }
            foreach (var card in actual.Where(c => SameSource(c, scope.Source) && !Owned(data, c)
                && c.Details?.Status is not ("Removed" or "Obsolete")))
                if (Excluded(card, scope) || !scope.Unresolved.Contains(Identity(card))
                    && !scope.Cards.Any(c => Identity(c) == Identity(card)))
                    errors.Add(scope.Faction + ": wrongly attributed or unverified army rule " + card.Name);
            // Exercise the same shared display path used by Windows and Android,
            // including established compatibility overrides for older source revisions.
            var expectedDisplay = ArmyReferences.ForArmy(ArmyList.New(scope.Faction), expectedData).Where(c => c.Section == "Army Rules");
            var display = ArmyReferences.ForArmy(ArmyList.New(scope.Faction), data).Where(c => c.Section == "Army Rules").ToList();
            foreach (var card in expectedDisplay)
                if (!Owned(data, card) && !display.Any(c => ReferenceModel.LogicalKey(c) == ReferenceModel.LogicalKey(card)))
                    errors.Add(scope.Faction + ": source rule absent from army display " + card.Name);
        }
        return errors;
    }

    public static List<ArmyRuleScope> ReadArchive(string archive, SourceInfo source)
    {
        var bytes = File.ReadAllBytes(archive);
        if (!Pinned(source) || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(source.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Army-rule archive does not match its pinned source.");
        return BsDataImporter.ImportArmyRules(BsDataDocument.ReadArchive(bytes), source);
    }
}
