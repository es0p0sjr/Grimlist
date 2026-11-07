using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static LoadoutAdjustment? RoleAdjustment(JsonObject modifier, JsonObject root, Dictionary<string, JsonObject> registry)
    {
        if (Flag(modifier, "unsupportedParentCondition") || Text(modifier, "affects").Length > 0
            || Text(modifier, "type") is not ("set" or "increment" or "decrement")
            || Children(modifier, "conditionGroups").Count + Children(modifier, "localConditionGroups").Count + Children(modifier, "repeats").Count != 0
            || Children(modifier, "conditions") is not { Count: 1 } conditions) return null;
        var condition = conditions[0]; var scope = Text(condition, "scope"); var child = Text(condition, "childId");
        if (Text(condition, "field") != "selections" || Flag(condition, "includeChildForces") || Flag(condition, "percentValue")
            || Text(condition, "type") is not ("atLeast" or "atMost" or "greaterThan" or "lessThan" or "equalTo" or "notEqualTo")
            || scope != Text(root, "id") && scope is not ("unit" or "unit-self") || child == "model") return null;
        var nodes = Descendants(root, registry, new SelectionPath()).ToList();
        var targets = nodes.Where(n => Text(n, "id") == child).ToList();
        if (targets.Count != 1) return null;
        var target = targets[0];
        // A structural group can stand for model selections only when descendants
        // are explicitly included. Bundles have a separate selection contribution.
        bool PureModels(JsonObject n, int depth = 0) => Text(n, "type") == "model" || depth < 20 && Text(n, "type") == ""
            && DirectChildren(n, registry).ToList() is { Count: > 0 } children && children.All(c => PureModels(c, depth + 1));
        if (!PureModels(target) || Text(target, "type") != "model" && !Flag(condition, "includeChildSelections")) return null;
        var roles = (Text(target, "type") == "model" ? [target] : Descendants(target, registry, new SelectionPath()).Where(n => Text(n, "type") == "model"))
            .Select(n => Text(n, "id")).Distinct().ToList();
        if (roles.Any(id => nodes.Count(n => Text(n, "type") == "model" && Text(n, "id") == id) != 1)) return null;
        return new(Text(modifier, "type"), Number(modifier, "value"), Text(condition, "type"), Number(condition, "value"),
            Query: new(scope, child, Flag(condition, "includeChildSelections"), roles));
    }

    // An envelope is used only to enumerate candidate squad sizes. The original
    // conditions stay in LoadoutBound and are evaluated against selected models.
    private static Dictionary<string, int> CompositionMaximums(JsonObject root, Dictionary<string, JsonObject> registry, LoadoutDefinition? loadout)
    {
        Dictionary<string, int> result = [];
        if (loadout == null) return result;
        foreach (var node in Descendants(root, registry, new SelectionPath()).Where(n => Text(n, "type") == "model"))
        {
            var limits = Children(node, "constraints").Where(c => Text(c, "type") == "max" && Text(c, "field") == "selections" && Text(c, "scope") == "parent").ToList();
            if (limits.Count != 1 || Text(limits[0], "id").Length == 0) continue;
            var limit = limits[0]; var id = Text(limit, "id");
            var modifiers = Modifiers(node).Where(m => Text(m, "field") == id && !InactiveGameMode(m, registry)).ToList();
            var role = loadout.Roles.SingleOrDefault(r => r.Id == Text(node, "id"));
            if (modifiers.Count == 0 || role?.Maximum.Adjustments?.Count != modifiers.Count) continue;
            var adjustments = modifiers.Select(m => RoleAdjustment(m, root, registry)).ToList();
            if (adjustments.Any(a => a == null) || role.Maximum.Adjustments.Any(a => a.Query == null)) continue;
            var maximum = Number(limit, "value");
            foreach (var adjustment in adjustments.Cast<LoadoutAdjustment>())
                maximum = adjustment.Operation switch { "set" => Math.Max(maximum, adjustment.Value), "increment" => checked(maximum + adjustment.Value), _ => maximum };
            if (maximum is >= 0 and <= 100) result[id] = maximum;
        }
        return result;
    }
}
