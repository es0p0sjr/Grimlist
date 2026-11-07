using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static bool IsEnhancementEntry(JsonObject node)
    {
        for (JsonNode? p = node.Parent; p != null; p = p.Parent)
            if (p is JsonObject parent && EnhancementGroup(parent)) return true;
        return false;
    }
    private static EffectCondition Both(EffectCondition left, EffectCondition right) => SimplifyCondition(new("and", Children: [left, right]));
    private static EffectCondition SimplifyCondition(EffectCondition condition)
    {
        EffectCondition Constant(bool value) => new("constant", value ? "1" : "0");
        if (condition.Kind == "constant" && int.TryParse(condition.Value, out var value))
            return condition.Comparison switch {
                "atLeast" => Constant(value >= condition.Count), "atMost" => Constant(value <= condition.Count),
                "equalTo" or "instanceOf" => Constant(value == condition.Count), "notEqualTo" or "notInstanceOf" => Constant(value != condition.Count),
                "lessThan" => Constant(value < condition.Count), "greaterThan" => Constant(value > condition.Count), _ => condition };
        var children = (condition.Children ?? []).Select(SimplifyCondition).ToList();
        if (condition.Kind == "not" && children.Count == 1)
            return children[0].Kind == "constant" ? Constant(children[0].Value == "0")
                : children[0].Kind == "not" ? children[0].Children!.Single() : condition with { Children = children };
        if (condition.Kind is not ("and" or "or")) return condition with { Children = condition.Children == null ? null : children };
        var and = condition.Kind == "and";
        if (children.Any(c => c.Kind == "constant" && c.Value == (and ? "0" : "1"))) return Constant(!and);
        children = children.Where(c => c.Kind != "constant").SelectMany(c => c.Kind == condition.Kind ? c.Children! : [c]).DistinctBy(c => DataJson.Write(c)).ToList();
        return children.Count == 0 ? Constant(and) : children.Count == 1 ? children[0] : condition with { Children = children };
    }
    private static bool Always(EffectCondition c) => c.Kind == "constant" && c.Value == "1"
        || c.Kind == "and" && (c.Children ?? []).All(Always);

    private static DatasheetSemantics ReadConditionalSemantics(JsonObject root, Dictionary<string, JsonObject> registry)
    {
        List<ScopedAbility> abilities = []; List<DatasheetModifier> modifiers = []; List<AttachmentPermission> permissions = [];
        var rootId = Text(root, "id"); var path = new SelectionPath();
        EffectCondition Permission(JsonObject container, bool group = false)
        {
            List<EffectCondition> conditions = [];
            foreach (var c in Children(container, "conditions"))
            {
                var test = EffectTest(new JsonObject { ["conditions"] = new JsonArray(c.DeepClone()) }, registry, rootId);
                conditions.Add(Flag(c, "queryFromSelf") ? new("owner", Children: [test]) : test);
            }
            conditions.AddRange(Children(container, "conditionGroups").Select(g => Permission(g, true)));
            if (Children(container, "localConditionGroups").Count > 0) conditions.Add(new("unknown"));
            return new(group && Text(container, "type") == "or" ? "or" : "and", Children: conditions);
        }
        foreach (var association in Children(root, "associations").Where(a => Text(a, "action") == "group" && Text(a, "childId") == "unit"))
            permissions.Add(new(Text(association, "id"), Permission(association)));

        void Rule(JsonObject definition, string section, EffectCondition parent, bool profile)
        {
            var condition = Both(parent, EffectVisibility(definition, registry, rootId));
            var node = BaseValues(definition, []);
            var name = Clean(Text(node, "name"));
            var text = profile ? string.Join("\n", Children(node, "characteristics").Select(c => Clean(Text(c, "$text")))) : Clean(Text(node, "description"));
            List<DatasheetModifier> changes = [];
            var descriptionIds = Children(definition, "characteristics").Select(c => Text(c, "typeId")).ToHashSet();
            var index = 0;
            foreach (var m in Modifiers(definition))
            {
                var test = EffectTest(m, registry, rootId); var field = Text(m, "field");
                if (Always(test) || Text(m, "type") is not ("set" or "append") || field is not ("name" or "description") && !descriptionIds.Contains(field)) continue;
                changes.Add(new(Text(definition, "id") + "/text/" + index++, name, "ability", field == "name" ? "Name" : "Text", Text(m, "type"),
                    (Text(m, "type") == "append" ? Text(m, "join") : "") + Clean(Text(m, "value")), Condition: test));
            }
            if (text.Length > 0) abilities.Add(new(new(name, text, section), condition, changes));
        }
        void Visit(JsonObject node, EffectCondition inherited, int depth = 0)
        {
            if (depth > 40 || SeparateSelection(node) || !path.Enter(node)) return;
            // The chosen root unit already exists in the roster. Its catalogue picker
            // visibility (including source editor display filters) is not rule scope.
            // Nested selectable branches and linked rule groups retain their conditions.
            var condition = depth == 0 ? inherited : Both(inherited, EffectVisibility(node, registry, rootId));
            var section = Text(node, "type") == "upgrade" ? "Equipment rules" : "Abilities";
            foreach (var r in Children(node, "rules")) Rule(r, section, condition, false);
            foreach (var p in Children(node, "profiles"))
                if (Text(p, "typeName") is not ("Unit" or "Melee Weapons" or "Ranged Weapons"))
                    Rule(p, Text(p, "typeName") == "Abilities" ? section : Text(p, "typeName"), condition, true);
            foreach (var p in Children(node, "profiles").Concat(Children(node, "infoLinks")
                .Where(l => Text(l, "type") == "profile" && registry.ContainsKey(Text(l, "targetId"))).Select(l => Resolve(l, registry))))
            {
                var type = Text(p, "typeName");
                if (type is not ("Unit" or "Melee Weapons" or "Ranged Weapons")) continue;
                var fields = Children(p, "characteristics").GroupBy(c => Text(c, "typeId")).ToDictionary(g => g.Key, g => Text(g.First(), "name"));
                var index = 0;
                foreach (var m in Modifiers(p))
                {
                    index++;
                    if (!fields.TryGetValue(Text(m, "field"), out var field) || Text(m, "type") is not ("set" or "increment" or "decrement" or "multiply" or "append")) continue;
                    var test = EffectTest(m, registry, rootId);
                    if (Always(test) && Text(m, "type") is "set" or "append") continue;
                    modifiers.Add(new(Text(p, "id") + "/" + index, Clean(Text(node, "name")), type == "Unit" ? "model" : "weapon", field,
                        Text(m, "type"), Text(m, "value"), Clean(Text(p, "name")), Condition: Both(condition, Both(EffectVisibility(p, registry, rootId), test))));
                }
            }
            foreach (var link in Children(node, "infoLinks"))
            {
                if (!registry.ContainsKey(Text(link, "targetId"))) continue;
                var target = Resolve(link, registry);
                if (Text(link, "type") == "rule") Rule(target, section, condition, false);
                else if (Text(link, "type") == "profile" && Text(target, "typeName") is not ("Unit" or "Melee Weapons" or "Ranged Weapons"))
                    Rule(target, Text(target, "typeName") == "Abilities" ? section : Text(target, "typeName"), condition, true);
                else if (Text(link, "type") == "infoGroup") Visit(target, condition, depth + 1);
            }
            // Structural modifiers on selection nodes are distinct from modifiers on
            // individual profiles. Keep their owning branch's applicability.
            modifiers.AddRange(ReadEnhancementModifiers(node, registry, "").Select(m => m with {
                Id = "selection/" + m.Id, Condition = m.Condition == null ? condition : Both(condition, m.Condition) }));
            foreach (var child in Children(node, "selectionEntries").Concat(Children(node, "selectionEntryGroups"))
                .Concat(Children(node, "entryLinks")).Concat(Children(node, "infoGroups")))
            {
                try
                {
                    var target = Resolve(child, registry);
                    var branch = condition;
                    if (Text(target, "type") == "upgrade" && !SeparateSelection(target))
                        branch = Both(branch, new("selection", Text(target, "id")));
                    Visit(target, branch, depth + 1);
                }
                catch (InvalidDataException) { /* Primary importer reports missing source links. */ }
            }
            path.Exit(node);
        }
        Visit(root, new("constant", "1"));
        // A rule can have both innate and conditional origins. Retain all origins for
        // scoped rules so an inactive duplicate never hides an independent innate grant.
        var scoped = abilities.GroupBy(a => (a.Ability.Name, a.Ability.Text))
            .Where(g => g.Any(a => !Always(a.Condition) || a.Modifiers?.Count > 0)).SelectMany(g => g).Distinct().ToList();
        return new([], modifiers, 2, scoped, permissions);
    }
}
