using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static bool ImpossibleChildCopy(JsonObject modifier, JsonObject child, JsonObject root, Dictionary<string, JsonObject> registry)
    {
        // A local query on a child group searches that group's siblings. It cannot
        // count the containing unit itself (a redundant pattern in some catalogues).
        if (!DirectChildren(root, registry).Any(n => Text(n, "id") == Text(child, "id"))) return false;
        bool Impossible(JsonObject node)
        {
            var locals = Children(node, "localConditionGroups").Select(local => Text(local, "field") == "selections" && Text(local, "scope") == "parent" && Text(local, "type") == "atLeast"
                && Number(local, "value") > 0 && Children(local, "conditions").Any(c => Text(c, "field") == "selections" && Text(c, "scope") == "self" && Text(c, "type") == "instanceOf" && Text(c, "value") == "1"
                    && !DirectChildren(root, registry).Any(n => Text(n, "id") == Text(c, "childId") || Children(n, "categoryLinks").Any(k => Text(k, "targetId") == Text(c, "childId"))))).ToList();
            locals.AddRange(Children(node, "conditionGroups").Select(Impossible));
            return Text(node, "type") == "or" ? Children(node, "conditions").Count == 0 && locals.Count > 0 && locals.All(b => b) : locals.Any(b => b);
        }
        return Impossible(modifier);
    }

    private static List<DetachmentUnitCost> DetachmentPrices(JsonObject root, Dictionary<string, JsonObject> registry)
    {
        var detachments = registry.Values.Where(n => Text(n, "name") == "Detachment")
            .SelectMany(n => DirectChildren(n, registry)).Where(n => Text(n, "type") == "upgrade")
            .DistinctBy(n => Text(n, "id")).ToDictionary(n => Text(n, "id"), n => Text(n, "name"));
        List<DetachmentUnitCost> result = [];
        foreach (var node in Descendants(root, registry, new SelectionPath()).Where(n => Flag(n, "hidden") && Text(n, "type") == "upgrade").DistinctBy(n => Text(n, "id")))
        {
            var costs = Children(node, "costs").Where(c => Text(c, "name") == "pts").ToList();
            if (costs.Count != 1 || Number(costs[0], "value") == 0) continue;
            var limits = Children(node, "constraints").Where(c => Text(c, "field") == "selections" && Text(c, "scope") == "parent").ToList();
            var minima = limits.Where(c => Text(c, "type") == "min").ToList();
            var maxima = limits.Where(c => Text(c, "type") == "max").ToList();
            if (minima.Count != 1 || maxima.Count != 1 || Text(minima[0], "value") is not ("0" or "-1") || Text(maxima[0], "value") is not ("0" or "1")) continue;
            var mods = Modifiers(node).ToList();
            var activation = mods.Where(m => Text(m, "field") == Text(minima[0], "id")).ToList();
            if (activation.Count != 1 || Text(activation[0], "type") != "set" || Text(activation[0], "value") != "1") continue;
            string? Detachment(JsonObject modifier)
            {
                List<JsonObject> conditions = [];
                bool Read(JsonObject value) => Text(value, "type") != "or" && !Flag(value, "unsupportedParentCondition") && Text(value, "affects").Length == 0
                    && Children(value, "repeats").Count + Children(value, "localConditionGroups").Count == 0
                    && Add(value) && Children(value, "conditionGroups").All(Read);
                bool Add(JsonObject value) { conditions.AddRange(Children(value, "conditions")); return true; }
                if (!Read(modifier) || conditions.Count != 1) return null;
                var c = conditions[0];
                return Text(c, "field") == "selections" && Text(c, "scope") is "roster" or "force" && Text(c, "type") == "atLeast" && Text(c, "value") == "1"
                    && detachments.ContainsKey(Text(c, "childId")) ? Text(c, "childId") : null;
            }
            var id = Detachment(activation[0]);
            if (id == null || !mods.Any(m => Text(m, "field") == "hidden" && Text(m, "type") == "set" && Text(m, "value") == "false" && Detachment(m) == id)
                || mods.Any(m => Text(m, "field") == Text(costs[0], "typeId"))
                || mods.Where(m => limits.Any(c => Text(c, "id") == Text(m, "field"))).Any(m => Text(m, "type") != "set" || Text(m, "value") != "1")
                || Text(maxima[0], "value") == "0" && !mods.Any(m => Text(m, "field") == Text(maxima[0], "id") && Text(m, "type") == "set" && Text(m, "value") == "1")
                || mods.Where(m => Text(m, "field") == "hidden").Any(m => Text(m, "type") != "set" || Text(m, "value") != "false")
                || mods.Where(m => Text(m, "field") == "hidden" || limits.Any(c => Text(c, "id") == Text(m, "field"))).Any(m => Detachment(m) != id)
                || Descendants(node, registry, new SelectionPath()).Any(n => Children(n, "costs").Any(c => Text(c, "name") == "pts" && Number(c, "value") != 0))) continue;
            result.Add(new(detachments[id], Text(node, "name"), Number(costs[0], "value"), Text(node, "id")));
        }
        return result;
    }

    private static string CopyCategory(JsonObject root)
    {
        IEnumerable<JsonObject> Local(JsonObject node) => Children(node, "localConditionGroups")
            .Concat(Children(node, "conditionGroups").SelectMany(Local));
        var fields = Children(root, "costs").Where(c => Text(c, "name") == "pts").Select(c => Text(c, "typeId")).ToHashSet();
        var targets = Modifiers(root).Where(m => fields.Contains(Text(m, "field"))).SelectMany(Local).SelectMany(g => Children(g, "conditions"))
            .Where(c => Text(c, "type") == "instanceOf" && Text(c, "scope") == "self" && Text(c, "childId") != Text(root, "id"))
            .Select(c => Text(c, "childId")).Distinct().ToList();
        if (targets.Count == 0) return "";
        if (targets.Count != 1) throw new InvalidDataException("Multiple copy-count categories need source review.");
        return Children(root, "categoryLinks").FirstOrDefault(c => Text(c, "targetId") == targets[0]) is { } category
            ? Text(category, "name") : throw new InvalidDataException("Copy-count category is missing.");
    }
}
