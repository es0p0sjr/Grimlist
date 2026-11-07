using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static List<DetachmentKeywordRule> ReadDetachmentKeywords(JsonObject root, Dictionary<string, JsonObject> registry, List<string> warnings)
    {
        var detachments = registry.Values.Where(n => Text(n, "name") == "Detachment")
            .SelectMany(n => Children(n, "selectionEntries").Concat(Children(n, "entryLinks")))
            .Select(n => Text(n, "targetId") is { Length: > 0 } target ? registry.GetValueOrDefault(target) : n)
            .OfType<JsonObject>().Where(n => Text(n, "type") == "upgrade")
            .GroupBy(n => Text(n, "id")).ToDictionary(g => g.Key, g => Text(g.First(), "name"));
        DetachmentCondition Parse(JsonObject node, bool group = false)
        {
            if (Flag(node, "unsupportedParentCondition") || Children(node, "repeats").Count > 0 || Children(node, "localConditionGroups").Count > 0 || Text(node, "affects").Length > 0)
                throw new InvalidDataException("unsupported scope or repeat");
            var kind = group ? Text(node, "type").ToLowerInvariant() : "and";
            if (kind is not ("and" or "or")) throw new InvalidDataException("unsupported condition group");
            List<DetachmentCondition> children = [];
            foreach (var c in Children(node, "conditions"))
            {
                if (Text(c, "field") != "selections" || Text(c, "scope") is not ("force" or "roster") || Flag(c, "percentValue")
                    || !detachments.TryGetValue(Text(c, "childId"), out var name)
                    || Text(c, "type") is not ("atLeast" or "atMost" or "equalTo" or "notEqualTo" or "lessThan" or "greaterThan"))
                    throw new InvalidDataException("unsupported detachment condition");
                children.Add(new("count", name, Text(c, "type"), Number(c, "value")));
            }
            children.AddRange(Children(node, "conditionGroups").Select(c => Parse(c, true)));
            return new(kind, Children: children);
        }
        List<DetachmentKeywordRule> rules = [];
        void Add(JsonObject modifier, string keyword, bool remove)
        {
            if (InactiveGameMode(modifier, registry)) return;
            try { rules.Add(new(keyword, remove, Parse(modifier))); }
            catch (InvalidDataException ex) { warnings.Add($"Conditional keyword {keyword} requires source review: {ex.Message}."); }
        }
        foreach (var m in Modifiers(root).Where(m => Text(m, "field") == "category"))
        {
            if (Text(m, "type") is not ("set-primary" or "add" or "remove") || !registry.TryGetValue(Text(m, "value"), out var category)) continue;
            Add(m, Text(category, "name"), Text(m, "type") == "remove");
        }
        foreach (var link in Children(root, "categoryLinks"))
        foreach (var m in Modifiers(link).Where(m => Text(m, "field") == "hidden" && Text(m, "type") == "set" && Text(m, "value") is "true" or "false"))
        {
            var keyword = registry.TryGetValue(Text(link, "targetId"), out var category) ? Text(category, "name") : Text(link, "name");
            Add(m, keyword, Text(m, "value") == "true");
        }
        return rules;
    }
}
