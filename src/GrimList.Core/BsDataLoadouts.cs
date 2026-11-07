using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static LoadoutDefinition ReadLoadout(JsonObject root, Dictionary<string, JsonObject> registry)
    {
        var warnings = new HashSet<string>();
        var rootId = Text(root, "id");
        var roles = new List<LoadoutRole>();
        var modelGroups = new List<LoadoutModelGroup>();
        var unitGroups = new List<EquipmentGroup>();
        var path = new HashSet<string>();
        JsonObject? Resolved(JsonObject node)
        {
            try { return BaseValues(Resolve(node, registry), warnings); }
            catch (InvalidDataException) { warnings.Add($"Equipment reference missing: {Text(node, "name")}. Import the repository ZIP."); return null; }
        }
        bool ModelCondition(JsonObject condition) => Text(condition, "childId") == "model" && Text(condition, "field") == "selections"
            && (Text(condition, "scope") == rootId || Text(condition, "scope") == "parent");
        LoadoutBound Bound(JsonObject node, string type, int fallback, bool unitScope = false)
        {
            if (Children(node, "constraints").Any(c => Text(c, "field") == "selections" && Text(c, "scope") is not ("parent" or "self") && Text(c, "scope") != rootId))
                warnings.Add($"Selection scope for {Text(node, "name")} needs source review.");
            var constraints = Children(node, "constraints").Where(c => Text(c, "type") == type && Text(c, "field") == "selections"
                && (unitScope ? Text(c, "scope") == rootId : Text(c, "scope") is "parent" or "self")).ToList();
            if (constraints.Count == 0) return type == "max" ? SelectionErrorCaps(node, rootId, new(fallback)) : new(fallback);
            var constraint = constraints[0];
            if (constraints.Count > 1) warnings.Add($"Multiple selection limits for {Text(node, "name")}; review this loadout against the source.");
            var adjustments = new List<LoadoutAdjustment>();
            foreach (var modifier in Modifiers(node).Where(m => Text(m, "field") == Text(constraint, "id") && Text(constraint, "id").Length > 0))
            {
                if (InactiveGameMode(modifier, registry)) continue;
                if (Text(node, "type") == "model" && RoleAdjustment(modifier, root, registry) is { } dependent)
                {
                    adjustments.Add(dependent); continue;
                }
                var conditions = Children(modifier, "conditions"); var repeats = Children(modifier, "repeats");
                var supported = !Flag(modifier, "unsupportedParentCondition") && Text(modifier, "affects").Length == 0
                    && Text(modifier, "type") is "set" or "increment" or "decrement"
                    && Children(modifier, "conditionGroups").Count + Children(modifier, "localConditionGroups").Count == 0
                    && conditions.Count <= 1 && conditions.All(c => ModelCondition(c) && Text(c, "type") is "atLeast" or "greaterThan" or "atMost" or "lessThan" or "equalTo" or "notEqualTo")
                    && repeats.Count <= 1 && repeats.All(r => ModelCondition(r) && Text(r, "roundUp") != "true" && Text(r, "repeats") is "" or "1" && Text(r, "value") != "0");
                if (!supported) { warnings.Add($"Conditional limit for {Text(node, "name")} needs source review; its base limit is shown."); continue; }
                adjustments.Add(new(Text(modifier, "type"), Number(modifier, "value"), conditions.Count == 0 ? "" : Text(conditions[0], "type"),
                    conditions.Count == 0 ? 0 : Number(conditions[0], "value"), repeats.Count == 0 ? 0 : Number(repeats[0], "value")));
            }
            var bound = new LoadoutBound(Number(constraint, "value"), adjustments);
            return type == "max" ? SelectionErrorCaps(node, rootId, bound) : bound;
        }
        (List<string> Weapons, List<string> Abilities, List<string> WeaponIds) Profiles(JsonObject node)
        {
            var weapons = new HashSet<string>(); var abilities = new HashSet<string>(); var visited = new HashSet<string>(); var weaponIds = new HashSet<string>();
            void Visit(JsonObject value, int depth)
            {
                value = BaseValues(value, warnings);
                if (depth > 30 || Flag(value, "hidden")) return;
                var id = Text(value, "id"); if (id.Length > 0 && !visited.Add(id)) return;
                var type = Text(value, "typeName");
                if (type is "Ranged Weapons" or "Melee Weapons") { weapons.Add(Clean(Text(value, "name"))); if (id.Length > 0) weaponIds.Add(id); }
                else if (type.Length > 0 && type != "Unit" || value.ContainsKey("description")) abilities.Add(Clean(Text(value, "name")));
                foreach (var child in Children(value, "profiles").Concat(Children(value, "rules")).Concat(Children(value, "infoGroups"))) Visit(child, depth + 1);
                foreach (var link in Children(value, "infoLinks")) if (Resolved(link) is { } resolved) Visit(resolved, depth + 1);
                foreach (var child in Children(value, "selectionEntries").Concat(Children(value, "entryLinks")))
                    if (Resolved(child) is { } resolved && !SeparateSelection(resolved) && Bound(resolved, "min", 0).Value > 0) Visit(resolved, depth + 1);
                if (Children(value, "selectionEntryGroups").Count > 0)
                    warnings.Add($"Nested equipment choices in {Text(value, "name")} need source review.");
            }
            Visit(node, 0); return (weapons.ToList(), abilities.ToList(), weaponIds.ToList());
        }
        EquipmentOption? Option(JsonObject node)
        {
            if (Flag(node, "hidden") || Text(node, "name") == "Warlord" || SeparateSelection(node)) return null;
            var (weapons, abilities, weaponIds) = Profiles(node);
            if (weapons.Count + abilities.Count == 0)
            {
                if (!Children(node, "costs").Any(c => Text(c, "name") == "pts" && Number(c, "value") > 0)) return null;
                warnings.Add($"No normalized profile for priced selection {Text(node, "name")}; its source price and selection limits are retained.");
            }
            return new(Text(node, "id"), Clean(Text(node, "name")), weapons, abilities, Bound(node, "min", 0), Bound(node, "max", 1),
                Children(node, "constraints").Any(c => Text(c, "type") == "max" && Text(c, "scope") == rootId) ? Bound(node, "max", 1000, true) : null, weaponIds);
        }
        List<EquipmentGroup> Groups(JsonObject node, int depth = 0, HashSet<string>? trail = null)
        {
            List<EquipmentGroup> groups = [];
            if (depth > 30) { warnings.Add("Equipment group depth exceeded."); return groups; }
            trail ??= [];
            var nodeId = Text(node, "id");
            if (nodeId.Length > 0 && !trail.Add(nodeId)) { warnings.Add("A cyclic equipment group was omitted."); return groups; }
            foreach (var raw in Children(node, "selectionEntries").Concat(Children(node, "entryLinks")))
            {
                var item = Resolved(raw); if (item == null || Text(item, "type") is "model" or "unit") continue;
                if (Text(raw, "type") == "selectionEntryGroup") continue;
                if (Option(item) is { } option) groups.Add(new("fixed:" + option.Id, option.Name, [option], option.Minimum, option.Maximum, option.Id));
            }
            foreach (var raw in Children(node, "selectionEntryGroups").Concat(Children(node, "entryLinks").Where(l => Text(l, "type") == "selectionEntryGroup")))
            {
                var group = Resolved(raw); if (group == null || Flag(group, "hidden") || SeparateSelection(group) || IsDatasheetChoiceGroup(group, registry)) continue;
                var rawChildren = Children(group, "selectionEntries").Concat(Children(group, "entryLinks")).ToList();
                var children = rawChildren.Select(Resolved).Where(n => n != null).Cast<JsonObject>().ToList();
                if (children.Any(n => Text(n, "type") == "model")) continue;
                var options = children.Select(Option).Where(o => o != null).Cast<EquipmentOption>().ToList();
                var defaultId = Text(group, "defaultSelectionEntryId");
                var defaultLink = rawChildren.FirstOrDefault(n => defaultId.Length > 0 && Text(n, "id") == defaultId && Text(n, "targetId").Length > 0);
                if (defaultLink != null) defaultId = Text(defaultLink, "targetId");
                // A group without a maximum is a container, not an exclusive choice.
                // Each option's own limits still apply (e.g. a pistol and rifle are both mandatory).
                if (options.Count > 0) groups.Add(new(Text(group, "id"), Clean(Text(group, "name")), options, Bound(group, "min", 0), Bound(group, "max", 1000), defaultId));
                groups.AddRange(Groups(group, depth + 1, trail).Where(g => !g.Id.StartsWith("fixed:")));
            }
            trail.Remove(nodeId);
            return groups;
        }
        void Models(JsonObject node, string defaultId = "", int depth = 0)
        {
            if (depth > 30) { warnings.Add("Equipment selection depth exceeded."); return; }
            var id = Text(node, "id"); if (id.Length > 0 && !path.Add(id)) return;
            if (Flag(node, "hidden")) { path.Remove(id); return; }
            var start = roles.Count;
            if (Text(node, "type") == "model")
            {
                var groups = Groups(node);
                // Weapons attached directly to a model are fixed equipment, not replacement choices.
                var direct = (JsonObject)node.DeepClone();
                direct.Remove("selectionEntries"); direct.Remove("selectionEntryGroups"); direct.Remove("entryLinks");
                var (weapons, _, weaponIds) = Profiles(direct);
                if (weapons.Count > 0)
                {
                    var option = new EquipmentOption("innate:" + id, "Standard equipment", weapons, [], new(1), new(1), WeaponSourceIds: weaponIds);
                    groups.Insert(0, new("innate:" + id, "Standard equipment", [option], new(1), new(1), option.Id));
                }
                roles.Add(new(id, Clean(Text(node, "name")) is { Length: > 0 } name ? name : Clean(Text(root, "name")),
                    Bound(node, "min", node == root ? 1 : 0), Bound(node, "max", node == root ? 1 : 1000), groups, id == defaultId));
            }
            else foreach (var raw in Children(node, "selectionEntries").Concat(Children(node, "selectionEntryGroups")).Concat(Children(node, "entryLinks")))
            {
                var child = Resolved(raw); if (child == null || Text(child, "type") == "upgrade" || SeparateSelection(child)) continue;
                var preferred = Text(node, "defaultSelectionEntryId");
                Models(child, preferred.Length > 0 && Text(raw, "id") == preferred ? Text(child, "id") : preferred, depth + 1);
            }
            if (node != root && Text(node, "type").Length == 0 && roles.Count > start)
                modelGroups.Add(new(Clean(Text(node, "name")), roles.Skip(start).Select(r => r.Id).ToList(), Bound(node, "min", 0), Bound(node, "max", 1000)));
            path.Remove(id);
        }
        Models(root);
        if (Text(root, "type") != "model") unitGroups = Groups(root);
        return new(roles.DistinctBy(r => r.Id).ToList(), unitGroups, warnings.ToList(), modelGroups);
    }

    // Some catalogues express a size-dependent limit as an error rather than modifying the maximum.
    // Only translate the exact conjunction: unit model count condition AND this selection exceeds N.
    private static LoadoutBound SelectionErrorCaps(JsonObject node, string rootId, LoadoutBound bound)
    {
        var adjustments = (bound.Adjustments ?? []).ToList();
        foreach (var modifier in Modifiers(node).Where(m => Text(m, "field") == "error" && Text(m, "type") == "add"))
        {
            if (Flag(modifier, "unsupportedParentCondition") || Text(modifier, "affects").Length > 0
                || Children(modifier, "repeats").Count + Children(modifier, "localConditionGroups").Count > 0) continue;
            var conditions = Children(modifier, "conditions").ToList(); var groups = Children(modifier, "conditionGroups");
            if (groups.Any(g => Text(g, "type") != "and" || Children(g, "conditionGroups").Count + Children(g, "localConditionGroups").Count > 0)) continue;
            conditions.AddRange(groups.SelectMany(g => Children(g, "conditions")));
            if (conditions.Count != 2 || conditions.Any(c => Text(c, "field") != "selections" || Text(c, "scope") != rootId || !Flag(c, "includeChildSelections"))) continue;
            var size = conditions.FirstOrDefault(c => Text(c, "childId") == "model");
            var count = conditions.FirstOrDefault(c => Text(c, "childId") == Text(node, "id"));
            if (size == null || count == null || Text(size, "type") is not ("lessThan" or "atMost" or "equalTo" or "notEqualTo" or "greaterThan" or "atLeast")
                || Text(count, "type") is not ("greaterThan" or "atLeast")) continue;
            var maximum = Number(count, "value") - (Text(count, "type") == "atLeast" ? 1 : 0);
            if (maximum >= 0) adjustments.Add(new("cap", maximum, Text(size, "type"), Number(size, "value")));
        }
        var distinct = adjustments.Distinct().ToList();
        return distinct.SequenceEqual(bound.Adjustments ?? []) ? bound : bound with { Adjustments = distinct };
    }

    public static UnitDefinition RepairRoleLimits(UnitDefinition unit, BsDataDocument document)
    {
        if (unit.Profile?.Loadout is not { } loadout) return unit;
        var prefix = $"bsdata:11:{document.Id}:";
        var alias = unit.Aliases?.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));
        if (alias == null) return unit;
        var nodes = Walk(document.Root).Where(n => Text(n, "id").Length > 0).GroupBy(n => Text(n, "id")).ToDictionary(g => g.Key, g => g.First());
        var roles = loadout.Roles.Select(r => nodes.TryGetValue(r.Id, out var node)
            ? r with { Maximum = SelectionErrorCaps(node, alias[prefix.Length..], r.Maximum) } : r).ToList();
        var groups = loadout.ModelGroups?.Select(g =>
        {
            var node = nodes.Values.FirstOrDefault(n => Clean(Text(n, "name")) == g.Name
                && Walk(n).Where(c => Text(c, "type") == "model").Select(c => Text(c, "id")).ToHashSet().SetEquals(g.RoleIds));
            return node == null ? g : g with { Maximum = SelectionErrorCaps(node, alias[prefix.Length..], g.Maximum) };
        }).ToList();
        return unit with { Profile = unit.Profile with { Loadout = loadout with { Roles = roles, ModelGroups = groups } } };
    }
}
