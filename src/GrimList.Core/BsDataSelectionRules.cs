using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static string SelectionIdentity(string id, Dictionary<string, JsonObject> registry)
    {
        HashSet<string> seen = [];
        while (registry.TryGetValue(id, out var link) && Text(link, "targetId") is { Length: > 0 } target)
        {
            if (!seen.Add(id)) throw new InvalidDataException("Cyclic selection identity: " + id);
            id = target;
        }
        return id;
    }

    private static SelectionQuery SelectionQuery(JsonObject node, string origin, Dictionary<string, JsonObject> registry)
    {
        if (Flag(node, "percentValue")) throw new InvalidDataException("Percentage selection queries need source review.");
        var field = Text(node, "field");
        if (field != "selections") throw new InvalidDataException("Unsupported selection query field: " + field);
        return new(SelectionIdentity(Text(node, "scope"), registry), SelectionIdentity(Text(node, "childId"), registry), field,
            Flag(node, "includeChildSelections"), Flag(node, "includeChildForces"), origin, Text(node, "shared") != "false");
    }

    private static SelectionTest SelectionTest(JsonObject node, string origin, Dictionary<string, JsonObject> registry,
        HashSet<string>? copyIds = null, SortedSet<int>? copyBreaks = null)
    {
        if (Flag(node, "unsupportedParentCondition") || Text(node, "affects").Length > 0)
            throw new InvalidDataException("Unsupported inherited selection condition.");
        List<SelectionTest> children = [];
        foreach (var condition in Children(node, "conditions"))
        {
            var query = SelectionQuery(condition, origin, registry); var comparison = Text(condition, "type");
            if (query.Scope == "primary-catalogue" && comparison is "instanceOf" or "notInstanceOf")
                comparison = comparison == "instanceOf" ? "equalTo" : "notEqualTo";
            if (comparison is not ("atLeast" or "atMost" or "equalTo" or "notEqualTo" or "lessThan" or "greaterThan"))
                throw new InvalidDataException("Unsupported selection comparison: " + comparison);
            children.Add(new("query", query, comparison, Number(condition, "value")));
        }
        foreach (var local in Children(node, "localConditionGroups"))
        {
            var conditions = Children(local, "conditions");
            if (copyIds == null || Text(local, "scope") != "parent" || Text(local, "field") != "selections"
                || Text(local, "type") != "atLeast" || conditions.Count != 2
                || Children(local, "conditionGroups").Count + Children(local, "localConditionGroups").Count != 0
                || !conditions.Any(c => Text(c, "type") == "before" && Text(c, "childId") == "any" && Text(c, "scope") == "self" && Text(c, "value") == "1")
                || !conditions.Any(c => Text(c, "type") == "instanceOf" && copyIds.Contains(SelectionIdentity(Text(c, "childId"), registry)) && Text(c, "scope") == "self"))
                throw new InvalidDataException("Unsupported local selection query.");
            var threshold = Number(local, "value"); copyBreaks?.Add(threshold + 1);
            // Local condition groups filter/count candidates. 'repeats' belongs to
            // the query schema; only a modifier's repeats apply it multiple times.
            children.Add(new("query", new("parent", "any", "copy-before", Flag(local, "includeChildSelections"), Flag(local, "includeChildForces"), origin), "atLeast", threshold));
        }
        children.AddRange(Children(node, "conditionGroups").Select(g => SelectionTest(g, origin, registry, copyIds, copyBreaks)));
        return new(Text(node, "type") == "or" ? "or" : "and", Children: children);
    }

    private static List<NumericModifier> NumericModifiers(JsonObject node, string field, string origin,
        Dictionary<string, JsonObject> registry, HashSet<string>? copyIds = null, SortedSet<int>? copyBreaks = null)
    {
        List<NumericModifier> result = []; var order = 0;
        foreach (var modifier in Modifiers(node))
        {
            var index = order++;
            if (Text(modifier, "field") != field || InactiveGameMode(modifier, registry)) continue;
            var operation = Text(modifier, "type");
            if (operation is not ("set" or "increment" or "decrement" or "multiply")) throw new InvalidDataException("Unsupported numeric modifier: " + operation);
            var repeats = Children(modifier, "repeats").Select(r => new NumericRepeat(SelectionQuery(r, origin, registry), Number(r, "value"),
                Text(r, "repeats").Length == 0 ? 1 : Number(r, "repeats"), Flag(r, "roundUp"))).ToList();
            result.Add(new(operation, Number(modifier, "value"), SelectionTest(modifier, origin, registry, copyIds, copyBreaks), repeats, index,
                Text(modifier, "id") is { Length: > 0 } id ? id : origin + "/modifier/" + index));
        }
        return result;
    }

    private static LoadoutDefinition RecursiveLoadout(JsonObject root, Dictionary<string, JsonObject> registry)
    {
        Dictionary<string, JsonObject> modelNodes = [];
        List<SelectionError> errors = [];
        SelectionNode? Build(JsonObject node, string key, bool top = false, int depth = 0)
        {
            if (depth > 30) throw new InvalidDataException("Recursive composition exceeds supported depth.");
            var kind = Text(node, "type"); var id = Text(node, "id");
            if (Flag(node, "hidden")) return null;
            var children = DirectChildren(node, registry).Select((c, i) => Build(c, key + "/" + i, depth: depth + 1)).OfType<SelectionNode>().ToList();
            if (kind != "model" && children.Count == 0) return null;
            foreach (var error in Modifiers(node).Where(m => Text(m, "field") == "error" && Text(m, "type") == "add" && !InactiveGameMode(m, registry)))
                errors.Add(new(Clean(Text(error, "value")), SelectionTest(error, key, registry), Text(error, "id")));
            if (kind == "model") modelNodes.TryAdd(id, node);
            List<SelectionLimit> limits = [];
            if (!top) foreach (var c in Children(node, "constraints").Where(c => Text(c, "field") == "selections" && Text(c, "scope") is "parent" or "self"))
            {
                if (Text(c, "type") is not ("min" or "max")) throw new InvalidDataException("Unsupported selection limit.");
                limits.Add(new(Text(c, "id"), Text(c, "type"), Number(c, "value"), NumericModifiers(node, Text(c, "id"), key, registry),
                    Text(c, "scope"), Flag(c, "includeChildSelections"), Flag(c, "includeChildForces")));
            }
            var aliases = registry.Where(p => Text(p.Value, "targetId") == id).Select(p => p.Key).Append(id).Distinct().ToList();
            var points = Children(node, "costs").Where(c => Text(c, "name") == "pts").Sum(c => Number(c, "value"));
            return new(key, id, id, Clean(Text(node, "name")), kind, aliases, limits, children,
                SelectionIdentity(Text(node, "defaultSelectionEntryId"), registry), points);
        }
        var tree = new SelectionComposition(Build(root, "root", true) ?? throw new InvalidDataException("No model selections found."), errors);
        var states = SelectionCompositions.States(tree);
        if (Text(root, "type") == "model" && tree.Root.Children.Count == 0) return ReadLoadout(root, registry) with { Composition = tree };
        var projection = (JsonObject)root.DeepClone();
        if (Text(root, "type") == "model") { projection["type"] = "unit"; projection["id"] = "projection:" + Text(root, "id"); }
        projection.Remove("selectionEntries"); projection.Remove("selectionEntryGroups"); projection.Remove("entryLinks");
        var entries = new JsonArray();
        foreach (var pair in modelNodes)
        {
            var model = (JsonObject)pair.Value.DeepClone();
            model["constraints"] = new JsonArray(
                new JsonObject { ["type"] = "min", ["field"] = "selections", ["scope"] = "parent", ["value"] = 0 },
                new JsonObject { ["type"] = "max", ["field"] = "selections", ["scope"] = "parent", ["value"] = states.Max(s => s.Roles.GetValueOrDefault(pair.Key)) });
            model.Remove("modifiers"); model.Remove("modifierGroups"); entries.Add(model);
        }
        foreach (var child in DirectChildren(root, registry).Where(n => Text(n, "type") == "upgrade"
            && !Descendants(n, registry, new SelectionPath()).Any(m => Text(m, "type") == "model"))) entries.Add(child.DeepClone());
        projection["selectionEntries"] = entries;
        var mapped = ReadLoadout(projection, registry);
        var defaults = SelectionCompositions.Traverse(tree.Root).Select(n => n.DefaultId).ToHashSet();
        var roles = mapped.Roles.Select(r => r with { Default = defaults.Contains(r.Id) }).ToList();
        return mapped with { Roles = roles, Composition = tree,
            ModelGroups = [new(Clean(Text(root, "name")), roles.Select(r => r.Id).ToList(), new(0), new(100))] };
    }
}
