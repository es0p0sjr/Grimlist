using System.IO.Compression;
using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Application;

// Offline recovery for every frontend. Reuse the shared reference identities and matcher; never
// replace an existing rule with an older bundled version or resurrect a tombstone.
public static class ReferenceLibraryRecovery
{
    public static Dataset ReadBundled()
    {
        using var resource = typeof(ReferenceLibraryRecovery).Assembly.GetManifestResourceStream(
            "GrimList.Application.Resources.references-11.json.gz")
            ?? throw new InvalidDataException("The bundled Stratagem references are missing.");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var pack = DataJson.Read<Dataset>(reader.ReadToEnd());
        pack.Validate();
        if (!pack.ReferenceOnly || pack.Units.Count != 0 || pack.Source.Edition != "11"
            || (pack.ReferenceCards ?? []).Any(c => c.Section != "Stratagems" || !ReferenceModel.DisplayOnly(c)))
            throw new InvalidDataException("Invalid Stratagem reference supplement.");
        return pack;
    }

    public static bool Restore(IArmyRepository repository, Action<string>? progress = null)
        => OpenLibrary(repository, progress).Changed;

    public static string RecoveryVersion => LibraryRecoveryVersion.References;
    public static (LoadedLibrary Library, bool Changed) OpenLibrary(IArmyRepository repository, Action<string>? progress = null)
        => RestoreLibrary(repository, repository.LoadLibrary(), progress);

    public static (LoadedLibrary Library, bool Changed) RestoreLibrary(IArmyRepository repository, LoadedLibrary loaded, Action<string>? progress = null)
    {
        var current = loaded.Dataset;
        if (current == null || current.Game != "Warhammer 40,000" || current.Source.Edition != "11") return (loaded, false);
        if (repository.HasLibraryCheck("stratagem-references", RecoveryVersion, loaded.Revision)) return (loaded, false);
        var result = Supplement(ArmyRuleLibraryRecovery.Recover(current, repository.DirectoryPath, progress), ReadBundled());
        // Compatibility normalization can undo a proposed addition. Do not persist
        // an identical replacement or churn source snapshots on every startup.
        if (!ReferenceEquals(current, result) && DataJson.Write(current) == DataJson.Write(result)) result = current;
        var changed = !ReferenceEquals(current, result);
        if (changed)
        {
            progress?.Invoke("Installing missing Stratagem references…");
            // ApplyImport keeps the previous snapshot and commits the replacement atomically.
            repository.ApplyImport(result, DataJson.Write(current));
            loaded = repository.LoadLibrary();
        }
        repository.RecordLibraryCheck("stratagem-references", RecoveryVersion, loaded.Revision);
        return (loaded, changed);
    }

    public static Dataset Supplement(Dataset current, Dataset pack)
    {
        current = DetachmentLifecycle.Apply(current);
        if (current.Game != pack.Game || current.Source.Edition != pack.Source.Edition) return current;
        current = DetachmentCatalogue.Apply(current);
        pack = DetachmentCatalogue.Apply(pack);
        current = SpaceMarineArmyRule.Apply(current);
        pack = SpaceMarineArmyRule.Apply(pack);
        current = SpaceMarineStratagems.Apply(current);
        pack = SpaceMarineStratagems.Apply(pack);
        current = ReferenceRevisionRecovery.CheckLegacy(current, pack);
        // The bundled supplement predates installed codex packs. It must not bring
        // back explicitly superseded detachments or rules after restarting the app.
        if (current.RulesPacks is { Count: > 0 } overrides)
            pack = pack with {
                Detachments = pack.Detachments?.Where(d => !overrides.Any(p => RulesPacks.OwnsDetachment(p, d))).ToList(),
                ReferenceCards = pack.ReferenceCards == null ? null : RulesPacks.WithoutOverrides(pack.ReferenceCards, overrides),
                ReferenceCoverage = pack.ReferenceCoverage?.Where(c => !overrides.Any(p => p.Factions.Any(f => ReferenceModel.Key(f) == ReferenceModel.Key(c.Faction)))).ToList()
            };
        static string Scope(string faction, string detachment) => ReferenceModel.Key(faction) + "|" + ReferenceModel.Key(detachment);
        var factions = current.Units.Select(u => ReferenceModel.Key(u.Faction)).ToHashSet();
        var definitions = (pack.Detachments ?? []).GroupBy(d => Scope(d.Faction, d.Name))
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var detachments = (current.Detachments ?? []).Select(d =>
        {
            if (!definitions.TryGetValue(Scope(d.Faction, d.Name), out var supplied)) return d;
            // Fill absent matching metadata only. Preserve costs, restrictions and dispositions.
            return d with { Id = d.Id.Length == 0 ? supplied.Id : d.Id,
                FactionId = d.FactionId.Length == 0 ? supplied.FactionId : d.FactionId };
        }).ToList();
        var knownScopes = detachments.Select(d => Scope(d.Faction, d.Name)).ToHashSet();
        // A custom catalogue may reuse an official faction's name. Its explicit
        // catalogue identity takes precedence over name-only recovery. Legacy
        // libraries without that metadata still receive their missing definitions.
        foreach (var d in pack.Detachments ?? [])
            if (DetachmentLifecycle.Allows(current, d) && factions.Contains(ReferenceModel.Key(d.Faction))
                && !(current.FactionContexts ?? []).Any(c => ReferenceModel.Key(c.Faction) == ReferenceModel.Key(d.Faction)
                    && c.CatalogueId.Length > 0 && d.FactionId.Length > 0 && c.CatalogueId != d.FactionId)
                && knownScopes.Add(Scope(d.Faction, d.Name))) detachments.Add(d);
        var matching = current with { Detachments = detachments };
        var existing = (current.ReferenceCards ?? []).Select(c => ReferenceModel.Canonical(c, current.Source)).ToList();
        var logical = existing.Select(ReferenceModel.LogicalKey).ToHashSet();
        var identities = existing.Select(c => (c.Details!.Edition, c.Details.SourceId, c.Details.Id)).ToHashSet();
        var suppliedCards = (pack.ReferenceCards ?? []).Select(c => ReferenceModel.Canonical(c, pack.Source)).ToList();
        var missing = suppliedCards.Where(c => !logical.Contains(ReferenceModel.LogicalKey(c))
            && !identities.Contains((c.Details!.Edition, c.Details.SourceId, c.Details.Id))
            && !(current.ReferenceCoverage ?? []).Any(s => s.Authoritative && s.State is "Complete" or "VerifiedComplete"
                && ReferenceMerge.OwnedScope(c, s))).ToList();
        var imported = ReferenceMerge.Prepare(matching with { ReferenceCards = [], ReferenceCoverage = [] },
            pack with { ReferenceCards = missing, ReferenceCoverage = [] });
        var added = imported.Cards.Where(c => c.Details!.Status == "Active"
            && c.Details.Match is not ("Unresolved" or "Ambiguous")).ToList();
        var cards = (current.ReferenceCards ?? []).Concat(added).ToList();
        var active = cards.Select(c => ReferenceMatching.Match(ReferenceModel.Canonical(c, current.Source), matching))
            .Where(c => c.Details!.Status == "Active" && c.Details.Match is not ("Unresolved" or "Ambiguous"))
            .GroupBy(ReferenceModel.LogicalKey).ToDictionary(g => g.Key, g => g.ToList());
        var coverage = (current.ReferenceCoverage ?? []).ToList();
        var coverageKeys = coverage.Select(ReferenceMerge.CoverageKey).ToHashSet();
        foreach (var declaration in pack.ReferenceCoverage ?? [])
        {
            if (!knownScopes.Contains(Scope(declaration.Faction, declaration.Detachment))
                || !coverageKeys.Add(ReferenceMerge.CoverageKey(declaration))) continue;
            var complete = suppliedCards.Where(c => ReferenceMerge.OwnedScope(c, declaration)).All(c =>
                active.TryGetValue(ReferenceModel.LogicalKey(c), out var matches)
                && matches.Any(m => m.Details!.SemanticHash == c.Details!.SemanticHash));
            coverage.Add(complete ? declaration : declaration with { State = "Partial", Mode = "Partial", Authoritative = false });
        }
        if (added.Count == 0 && coverage.Count == (current.ReferenceCoverage?.Count ?? 0)
            && DataJson.Write(detachments) == DataJson.Write(current.Detachments)) return current;
        return ReferenceRevisionRecovery.CheckLegacy(DetachmentLifecycle.Apply(current with { SchemaVersion = Math.Max(2, current.SchemaVersion), Detachments = detachments, ReferenceCards = cards, ReferenceCoverage = coverage }), pack);
    }

    public static bool HasCompleteCoverage(Dataset data, string faction, string detachment) =>
        DatasetSnapshot.For(data).References.Coverage.Factions
            .Where(f => ReferenceModel.Key(f.Faction) == ReferenceModel.Key(faction)).SelectMany(f => f.Detachments)
            .Any(d => ReferenceModel.Key(d.Detachment) == ReferenceModel.Key(detachment)
                && d.Coverage is "Complete" or "VerifiedComplete");
}
