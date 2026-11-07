using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static EffectCondition DetachmentTest(JsonObject node, Dictionary<string, JsonObject> registry, bool group = false)
    {
        if (Flag(node, "unsupportedParentCondition") || Children(node, "repeats").Count + Children(node, "localConditionGroups").Count > 0) return new("unknown");
        List<EffectCondition> children = [];
        foreach (var c in Children(node, "conditions"))
        {
            var id = Text(c, "childId"); var comparison = Text(c, "type");
            var count = comparison is "instanceOf" or "notInstanceOf" ? 1 : int.TryParse(Text(c, "value"), out var value) ? value : -1;
            if (count < 0 || Flag(c, "percentValue")) { children.Add(new("unknown")); continue; }
            if (Text(c, "scope") == "primary-catalogue") children.Add(new("catalogue", id, comparison, count));
            else if (registry.TryGetValue(id, out var target) && Text(target, "type").Length == 0 && Text(c, "scope") is "force" or "roster")
                children.Add(new("factionKeyword", Text(target, "name"), comparison, count));
            else children.Add(new("unknown", Label: "Unresolved detachment selection condition"));
        }
        children.AddRange(Children(node, "conditionGroups").Select(c => DetachmentTest(c, registry, true)));
        return new(group && Text(node, "type") == "or" ? "or" : "and", Children: children);
    }
    private static EffectCondition DetachmentVisibility(JsonObject node, Dictionary<string, JsonObject> registry)
    {
        EffectCondition visible = new("constant", Flag(node, "hidden") ? "0" : "1");
        foreach (var modifier in Modifiers(node).Where(m => Text(m, "field") == "hidden"))
        {
            if (Text(modifier, "type") != "set") return new("unknown");
            var condition = DetachmentTest(modifier, registry);
            visible = Flag(modifier, "value") ? new("and", Children: [visible, new("not", Children: [condition])]) : new("or", Children: [visible, condition]);
        }
        return visible;
    }

    // Metadata-only import uses the same resolver and semantics as the production importer.
    public static Dataset ImportDetachmentMetadata(IReadOnlyList<BsDataDocument> documents, SourceInfo source, bool authoritativeDownload = false)
    {
        List<DetachmentDefinition> detachments = []; List<FactionContext> contexts = [];
        foreach (var primary in ArmyCatalogues(documents))
        {
            var registry = new Dictionary<string, JsonObject>(); var seen = new HashSet<string>();
            void Include(BsDataDocument doc)
            {
                if (!seen.Add(doc.Id)) return;
                foreach (var node in Walk(doc.Root)) if (Text(node, "id") is { Length: > 0 } id) registry.TryAdd(id, node);
                foreach (var link in Children(doc.Root, "catalogueLinks"))
                    if (documents.FirstOrDefault(d => d.Id == Text(link, "targetId")) is { } dependency) Include(dependency);
            }
            Include(primary);
            if (documents.FirstOrDefault(d => d.Id == Text(primary.Root, "gameSystemId")) is { } game) Include(game);
            var result = ReadReferences(primary, registry, source, NativeCatalogues(documents, primary).ToList());
            detachments.AddRange(result.Detachments ?? []); contexts.AddRange(result.FactionContexts ?? []);
        }
        var data = new Dataset(source, [], Detachments: detachments, ReferenceOnly: true, FactionContexts: contexts);
        return authoritativeDownload ? WithDetachmentAuthority(documents, data, []) : data;
    }
}
