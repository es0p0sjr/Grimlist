using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    public static List<BsDataDocument> ArmyCatalogues(IReadOnlyList<BsDataDocument> documents) => documents
        .Where(d => !Flag(d.Root, "library") && !d.Name.Contains("Library", StringComparison.OrdinalIgnoreCase)
            && Text(d.Root, "gameSystemId").Length > 0)
        .OrderBy(FactionName).ToList();

    public static string FactionName(BsDataDocument document)
    {
        var name = document.Name;
        foreach (var prefix in new[] { "Imperium - Adeptus Astartes - ", "Imperium - ", "Chaos - ", "Aeldari - ", "Xenos - ", "Library - " })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { name = name[prefix.Length..]; break; }
        name = name.Replace(" - Library", "", StringComparison.OrdinalIgnoreCase).Replace(" Library", "", StringComparison.OrdinalIgnoreCase).Trim();
        name = name switch
        {
            "Craftworlds" => "Aeldari", "Daemons" => "Chaos Daemons", "Agents of the Imperium" => "Imperial Agents",
            "Adeptus Titanicus" => "Titan Legions", "Titanicus Traitoris" => "Chaos Titan Legions",
            "Adeptus Astartes" => "Space Marines", _ => name
        };
        return MfmImporter.Factions.FirstOrDefault(f => UnitIdentity.Normalize(f) == UnitIdentity.Normalize(name)) ?? name;
    }

    // A chapter inherits its shared marine roster. Allied catalogues supply definitions,
    // but their root units must not become unconditional native choices in every army.
    // Also exposed for offline source audits: reuse the importer's native/allied
    // boundary instead of inventing a second faction mapping in audit tooling.
    public static IEnumerable<BsDataDocument> NativeCatalogues(IReadOnlyList<BsDataDocument> documents, BsDataDocument primary)
    {
        var seen = new HashSet<string>();
        IEnumerable<BsDataDocument> Visit(BsDataDocument document)
        {
            if (!seen.Add(document.Id)) yield break;
            yield return document;
            foreach (var link in Children(document.Root, "catalogueLinks").Where(l => Flag(l, "importRootEntries")))
            {
                var dependency = documents.FirstOrDefault(d => d.Id == Text(link, "targetId"));
                // Local single-catalogue repairs may omit unrelated allied libraries.
                var linkedFaction = dependency == null ? FactionName(new("", new() { ["name"] = Text(link, "name") })) : FactionName(dependency);
                if (dependency == null && (linkedFaction == FactionName(primary) || linkedFaction == "Space Marines" && MarineChapters.Contains(FactionName(primary))))
                    throw new InvalidDataException($"{primary.Name}: missing catalogue '{Text(link, "name")}'. Choose the complete repository ZIP.");
                if (dependency == null) continue;
                var sameFaction = FactionName(dependency) == FactionName(primary);
                var marineParent = FactionName(dependency) == "Space Marines" && MarineChapters.Contains(FactionName(primary));
                if (sameFaction || marineParent)
                    foreach (var inherited in Visit(dependency)) yield return inherited;
            }
        }
        return Visit(primary);
    }

    private static readonly HashSet<string> MarineChapters = ["Black Templars", "Blood Angels", "Dark Angels", "Deathwatch",
        "Imperial Fists", "Iron Hands", "Raven Guard", "Salamanders", "Space Wolves", "Ultramarines", "White Scars"];

    public static ImportPreview ImportAll(IReadOnlyList<BsDataDocument> documents, SourceInfo source,
        IProgress<string>? progress = null, CancellationToken cancellation = default, bool authoritativeDownload = false)
    {
        if (source.Edition is not ("10" or "11")) throw new InvalidDataException("Select BSData edition 10 or 11 explicitly.");
        var catalogues = ArmyCatalogues(documents);
        if (catalogues.Count == 0) throw new InvalidDataException("No army catalogues found. Choose a complete BSData repository ZIP.");
        if (catalogues.Any(c => Path.GetExtension(c.FileName).Equals(source.Edition == "10" ? ".json" : ".cat", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The selected edition does not match the catalogue format: 10e uses CAT and 11e uses JSON.");
        if (documents.GroupBy(d => d.Id).Any(g => g.Count() > 1)) throw new InvalidDataException("Duplicate catalogue IDs in the source.");
        List<FactionContext> contexts = []; List<UnitDefinition> units = []; List<DetachmentDefinition> detachments = []; List<ReferenceCard> cards = []; List<string> warnings = [];
        for (var i = 0; i < catalogues.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var primary = catalogues[i];
            progress?.Report($"Importing {FactionName(primary)} ({i + 1}/{catalogues.Count})…");
            ImportPreview imported;
            try { imported = ImportCatalogue(documents, primary, source, cancellation); }
            catch (InvalidDataException ex) { throw new InvalidDataException($"{primary.Name}: {ex.Message}", ex); }
            units.AddRange(imported.Dataset.Units);
            contexts.AddRange(imported.Dataset.FactionContexts ?? []);
            detachments.AddRange(imported.Dataset.Detachments ?? []);
            cards.AddRange(imported.Dataset.ReferenceCards ?? []);
            warnings.AddRange(imported.Warnings);
        }
        cancellation.ThrowIfCancellationRequested();
        var result = new Dataset(source, units, Detachments: detachments, ReferenceCards: cards, FactionContexts: contexts);
        if (authoritativeDownload) result = WithDetachmentAuthority(documents, result, warnings);
        result.Validate();
        return new(result, warnings.Distinct().ToList());
    }
}
