using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static Dataset WithDetachmentAuthority(IReadOnlyList<BsDataDocument> documents, Dataset data, List<string> warnings)
    {
        List<DetachmentSourceScope> scopes = [];
        foreach (var primary in ArmyCatalogues(documents))
        {
            var loaded = new HashSet<string>(); var complete = true;
            var nodes = new List<System.Text.Json.Nodes.JsonObject>();
            void Include(string id)
            {
                if (!loaded.Add(id)) return;
                var document = documents.FirstOrDefault(d => d.Id == id);
                if (document == null) { complete = false; return; }
                nodes.AddRange(Walk(document.Root));
                foreach (var link in Children(document.Root, "catalogueLinks")) Include(Text(link, "targetId"));
            }
            Include(primary.Id); Include(Text(primary.Root, "gameSystemId"));
            var ids = nodes.Select(n => Text(n, "id")).Where(n => n.Length > 0).ToHashSet();
            // References are allowed to omit prose but not unresolved entry links.
            if (nodes.Any(n => Text(n, "targetId") is { Length: > 0 } target && !ids.Contains(target))) complete = false;
            var faction = FactionName(primary);
            var definitions = (data.Detachments ?? []).Where(d => d.Faction == faction).ToList();
            var registry = nodes.Where(n => Text(n, "id").Length > 0).GroupBy(n => Text(n, "id")).ToDictionary(g => g.Key, g => g.First());
            var seen = new HashSet<string>();
            IEnumerable<System.Text.Json.Nodes.JsonObject> Visit(System.Text.Json.Nodes.JsonObject node)
            {
                var id = Text(node, "id");
                if (id.Length > 0 && !seen.Add(id)) yield break;
                yield return node;
                foreach (var child in Children(node, "selectionEntries").Concat(Children(node, "selectionEntryGroups")).Concat(Children(node, "entryLinks")))
                    foreach (var descendant in Visit(Text(child, "targetId").Length == 0 || registry.ContainsKey(Text(child, "targetId")) ? Resolve(child, registry) : child))
                        yield return descendant;
            }
            var tree = ReadDetachmentTree(NativeCatalogues(documents, primary).SelectMany(d => Walk(d.Root)).SelectMany(Visit), registry);
            if (!tree.Complete || tree.Choices.Any(c => !definitions.Any(d => d.Id == Text(c.Entry, "id") || d.Id == Text(c.Entry, "sourceLinkId")))) complete = false;
            if (!tree.HasContainer || definitions.Any(d => d.Id.Length == 0)
                || definitions.GroupBy(d => d.Id).Any(g => g.Count() > 1)
                || definitions.GroupBy(d => UnitIdentity.Normalize(d.Name)).Any(g => g.Count() > 1)) complete = false;
            if (!complete)
            {
                warnings.Add($"{faction}: detachment coverage is incomplete or ambiguous; missing detachments will not be retired.");
                continue;
            }
            var active = definitions.Where(d => d.Applicability == null || SimplifyCondition(d.Applicability) is not { Kind: "constant", Value: "0" })
                .Select(d => new DetachmentIdentity(d.Id, d.Name)).ToList();
            scopes.Add(new(faction, primary.Id, data.Source, active, []));
        }
        return data with { SchemaVersion = scopes.Count > 0 ? 4 : data.SchemaVersion,
            DetachmentSources = scopes.Count > 0 ? scopes : null, IsBsDataDownload = true };
    }
}
