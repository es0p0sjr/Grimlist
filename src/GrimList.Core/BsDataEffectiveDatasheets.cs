using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Dictionary<string, JsonObject>, Dictionary<string, string>> characteristicNames = new();
    private static EffectCondition EffectTest(JsonObject node, Dictionary<string, JsonObject> registry, string rootId, bool group = false)
    {
        if (Flag(node, "unsupportedParentCondition") || Children(node, "repeats").Count + Children(node, "localConditionGroups").Count > 0)
            return new("unknown", node.ToJsonString(), Label: "Unsupported source condition");
        List<EffectCondition> children = [];
        foreach (var c in Children(node, "conditions"))
        {
            var id = Text(c, "childId"); var scope = Text(c, "scope"); var comparison = Text(c, "type");
            registry.TryGetValue(id, out var target);
            var name = target == null ? "" : Text(target, "name");
            var membership = comparison is "instanceOf" or "notInstanceOf";
            var count = membership ? 1 : int.TryParse(Text(c, "value"), out var value) ? value : -1;
            if (count < 0 || Flag(c, "percentValue")) { children.Add(new("unknown", c.ToJsonString())); continue; }
            if ((membership || Text(c, "field") == "forces") && name is "Army Roster" or "Crusade Force" or "Boarding Actions")
                children.Add(new("constant", name == "Army Roster" ? "1" : "0", comparison, count));
            else if (scope == "primary-catalogue") children.Add(new("catalogue", id, comparison, count));
            else if (Text(c, "field") == "associations" && scope is "self" or "root-entry" or "unit" or "unit-self")
                children.Add(new("association", id, comparison, count, Label: name));
            else if (Text(c, "field") == "selections" && Flag(c, "queryFromSelf") && target != null && IsEnhancementEntry(target))
                children.Add(new("enhancement", name, comparison, count));
            else if (Text(c, "field") == "selections" && Flag(c, "traverseAssociationGroup") && scope is "self" or "root-entry" or "ancestor")
                children.Add(new(id == "model" ? "attached-models" : target != null && IsEnhancementEntry(target) ? "attached-enhancement" : "attached-keyword",
                    target != null && IsEnhancementEntry(target) ? name : id, comparison, count, Label: name));
            else if (Text(c, "field") == "selections" && scope is "force" or "roster" && target != null
                && (Children(target, "costs").Any(cost => Text(cost, "name") == "Detachment Points")
                    || target.Parent?.Parent is JsonObject parent && ReferenceModel.Key(Text(parent, "name")) is "DETACHMENT" or "DETACHMENTS"))
                children.Add(new("detachment", name, comparison, count));
            else if (Text(c, "field") == "selections" && scope is "force" or "roster" && target != null)
                children.Add(new(Text(target, "type") is "unit" or "model" ? "roster-unit" : "roster-keyword", id, comparison, count, Label: name));
            else if (Text(c, "field") == "selections" && (scope is "ancestor" or "parent" or "root-entry" or "self" || scope == rootId))
            {
                if (membership && target != null) children.Add(new(Text(target, "type") is "unit" or "model" ? "unit" : Text(target, "type").Length == 0 ? "keyword" : "unknown",
                    Text(target, "type") is "unit" or "model" ? id : name, comparison, count));
                else if (id == "model") children.Add(new("models", Comparison: comparison, Count: count));
                else if (target != null && IsEnhancementEntry(target)) children.Add(new("enhancement", name, comparison, count));
                else children.Add(new("selection", id, comparison, count));
            }
            else children.Add(new("unknown", c.ToJsonString(), Label: name.Length > 0 ? name : "Unresolved source condition"));
        }
        children.AddRange(Children(node, "conditionGroups").Select(g => EffectTest(g, registry, rootId, true)));
        return SimplifyCondition(new(group && Text(node, "type") == "or" ? "or" : "and", Children: children));
    }
    private static EffectCondition EffectVisibility(JsonObject node, Dictionary<string, JsonObject> registry, string rootId)
    {
        EffectCondition result = new("constant", Flag(node, "hidden") ? "0" : "1");
        foreach (var m in Modifiers(node).Where(m => Text(m, "field") == "hidden"))
        {
            if (Text(m, "type") != "set") return new("unknown");
            var test = EffectTest(m, registry, rootId);
            result = Flag(m, "value") ? new("and", Children: [result, new("not", Children: [test])]) : new("or", Children: [result, test]);
        }
        return SimplifyCondition(result);
    }
    private static List<string> ChoiceKeywords(JsonObject node, Dictionary<string, JsonObject> registry) => Keywords(node, registry)
        .Concat(Modifiers(node).Where(m => Text(m, "field") == "category" && Text(m, "scope") == "root-entry" && Text(m, "type") == "add"
            && Children(m, "conditions").Count + Children(m, "conditionGroups").Count == 0)
            .Select(m => registry.TryGetValue(Text(m, "value"), out var category) ? Text(category, "name") : ""))
        .Where(k => k.Length > 0).Distinct().ToList();
    private static List<JsonObject> ChoiceNodes(JsonObject group, Dictionary<string, JsonObject> registry)
        => Children(group, "selectionEntries").Concat(Children(group, "entryLinks")).Select(c => Resolve(c, registry)).ToList();
    private static bool IsDatasheetChoiceGroup(JsonObject group, Dictionary<string, JsonObject> registry)
    {
        if (SeparateSelection(group) || !Children(group, "constraints").Any(c => Text(c, "field") == "selections" && Text(c, "scope") == "parent"
            && Text(c, "type") == "max" && Text(c, "value") == "1")) return false;
        var options = ChoiceNodes(group, registry);
        return options.Count >= 2 && options.All(o => Text(o, "type") == "upgrade" && ChoiceKeywords(o, registry).Count > 0
            && !Children(o, "profiles").Concat(Children(o, "infoLinks").Where(l => registry.ContainsKey(Text(l, "targetId"))).Select(l => Resolve(l, registry)))
                .Any(p => Text(p, "typeName") is "Melee Weapons" or "Ranged Weapons"));
    }
    private static DatasheetSemantics ReadDatasheetSemantics(JsonObject root, Dictionary<string, JsonObject> registry)
    {
        List<DatasheetChoiceGroup> choices = []; List<DatasheetModifier> modifiers = [];
        var rootId = Text(root, "id"); var path = new SelectionPath();
        void Visit(JsonObject node, int depth = 0)
        {
            if (depth > 30 || SeparateSelection(node) || !path.Enter(node)) return;
            if (IsDatasheetChoiceGroup(node, registry))
            {
                var options = ChoiceNodes(node, registry).Select(o => new DatasheetChoiceOption(Text(o, "id"), Clean(Text(o, "name")), ChoiceKeywords(o, registry),
                    Children(o, "profiles").Concat(Children(o, "infoLinks").Where(l => registry.ContainsKey(Text(l, "targetId"))).Select(l => Resolve(l, registry)))
                        .Where(p => Text(p, "typeName") is "Abilities" or "Ability")
                        .Select(p => new AbilityProfile(Clean(Text(p, "name")), string.Join("\n", Children(p, "characteristics").Select(c => Clean(Text(c, "$text")))), "Roster choice")).ToList(),
                    EffectVisibility(o, registry, rootId))).ToList();
                var minimum = Children(node, "constraints").Where(c => Text(c, "field") == "selections" && Text(c, "scope") == "parent" && Text(c, "type") == "min")
                    .Select(c => Number(c, "value")).DefaultIfEmpty(0).Max();
                choices.Add(new(Text(node, "id"), Clean(Text(node, "name")), minimum, options, EffectVisibility(node, registry, rootId)));
            }
            var profiles = Children(node, "profiles").Concat(Children(node, "infoLinks").Where(l => Text(l, "type") == "profile" && registry.ContainsKey(Text(l, "targetId"))).Select(l => Resolve(l, registry)));
            foreach (var profile in profiles)
            {
                var type = Text(profile, "typeName");
                if (type is not ("Unit" or "Melee Weapons" or "Ranged Weapons")) continue;
                var fields = Children(profile, "characteristics").Where(c => Text(c, "typeId").Length > 0)
                    .GroupBy(c => Text(c, "typeId")).ToDictionary(g => g.Key, g => Text(g.Last(), "name"));
                var index = 0;
                foreach (var modifier in Modifiers(profile))
                {
                    index++;
                    if (!fields.TryGetValue(Text(modifier, "field"), out var field) || Text(modifier, "type") is not ("set" or "increment" or "decrement" or "multiply" or "append" or "add" or "remove")) continue;
                    var test = EffectTest(modifier, registry, rootId);
                    if (Always(test) && Text(modifier, "type") is "set" or "append") continue; // Already present in imported base values.
                    modifiers.Add(new(Text(profile, "id") + "/" + index, Clean(Text(node, "name")), type == "Unit" ? "model" : "weapon", field,
                        Text(modifier, "type"), Text(modifier, "value"), Clean(Text(profile, "name")), Condition: test,
                        WeaponPredicate: type == "Unit" ? null : new("source-id", Text(profile, "id"))));
                }
            }
            foreach (var child in Children(node, "selectionEntries").Concat(Children(node, "selectionEntryGroups")).Concat(Children(node, "entryLinks")))
            {
                try { Visit(Resolve(child, registry), depth + 1); } catch (InvalidDataException) { /* Existing importer reports missing references. */ }
            }
            path.Exit(node);
        }
        Visit(root);
        // Resolve source labels from the selections on which a profile change depends.
        IEnumerable<string> Selections(EffectCondition? c) => c == null ? [] : (c.Kind == "selection" ? new[] { c.Value } : [])
            .Concat((c.Children ?? []).SelectMany(Selections));
        modifiers = modifiers.Select(m => m with { Source = string.Join("; ", Selections(m.Condition).SelectMany(id => choices
            .SelectMany(g => g.Options.Where(o => o.Id == id).Select(o => o.Name + " — " + g.Name))).DefaultIfEmpty(m.Source)) }).ToList();
        var conditional = ReadConditionalSemantics(root, registry);
        return new(choices.DistinctBy(g => g.Id).ToList(), conditional.Modifiers.Concat(modifiers).DistinctBy(m => m.Id).ToList(), 3,
            conditional.Abilities, conditional.Attachments);
    }
    private static List<DatasheetModifier> ReadEnhancementModifiers(JsonObject node, Dictionary<string, JsonObject> registry, string description, bool rosterRule = false)
    {
        var name = Clean(Text(node, "name")); var id = Text(node, "id"); List<DatasheetModifier> result = [];
        var fields = characteristicNames.GetValue(registry, entries => entries.Values.SelectMany(p => Children(p, "characteristics")).Where(c => Text(c, "typeId").Length > 0)
            .GroupBy(c => Text(c, "typeId")).ToDictionary(g => g.Key, g => Text(g.First(), "name")));
        var index = 0;
        foreach (var modifier in Modifiers(node))
        {
            index++;
            var affects = Text(modifier, "affects");
            // Exact supported selectors only. Category-filtered or arbitrary recursive
            // selectors require their own predicate, never broaden them to all weapons.
            var type = BsDataWeaponSelectors.Type(affects);
            WeaponPredicate? predicate = type.Length == 0 && affects.Length > 0 ? new("unknown", affects) : null;
            var selector = System.Text.RegularExpressions.Regex.Match(affects, @"^self\.entries(?:\.recursive)?\.(?<id>[a-f0-9-]+)\.profiles\.(?<type>Melee|Ranged) Weapons$");
            if (selector.Success && registry.TryGetValue(selector.Groups["id"].Value, out var selectedCategory)
                && selectedCategory.Parent?.Parent is JsonObject categoryOwner && Children(categoryOwner, "categoryEntries").Contains(selectedCategory))
            {
                type = selector.Groups["type"].Value;
                predicate = new("source-category", selector.Groups["id"].Value);
            }
            if (Text(modifier, "field") == "category" && affects.Length == 0 && Text(modifier, "scope") is "" or "self" or "model" or "root-entry"
                && Text(modifier, "type") is "add" or "remove" && registry.TryGetValue(Text(modifier, "value"), out var category))
                result.Add(new(id + "/" + index, name, "keyword", "", Text(modifier, "type"), Clean(Text(category, "name")), Condition: EffectTest(modifier, registry, Text(node, "id"))));
            if (Text(modifier, "field") == "add-info" && Text(modifier, "type") is "add" or "remove"
                && registry.TryGetValue(Text(modifier, "value"), out var rule))
            {
                // 'group.<category>' selects members of an attached unit, not weapons
                // and not the source unit. Preserve that recipient separately from
                // source conditions; only profile traversals are weapon selectors.
                var weapon = type is "Melee" or "Ranged" or "All" || affects.Contains("profiles", StringComparison.Ordinal);
                var recipient = weapon || affects.Length == 0 ? null : ReadEffectRecipient(node, modifier, rule, registry);
                result.Add(new(id + "/" + index, name, weapon ? "weapon" : "ability",
                    weapon ? "Keywords" : Clean(Text(rule, "description")) is { Length: > 0 } descriptionText ? descriptionText
                    : string.Join("\n", Children(rule, "characteristics").Select(c => Clean(Text(c, "$text")))), Text(modifier, "type"), Clean(Text(rule, "name")),
                    WeaponType: type is "Melee" or "Ranged" ? type : "", WeaponPredicate: weapon ? predicate : null, Recipient: recipient,
                    Condition: Both(EffectTest(modifier, registry, Text(node, "id")), EffectVisibility(rule, registry, Text(node, "id")))));
                continue;
            }
            if (type.Length == 0 && predicate == null || !fields.TryGetValue(Text(modifier, "field"), out var field)
                || Text(modifier, "type") is not ("set" or "increment" or "decrement" or "multiply" or "append" or "add" or "remove")) continue;
            result.Add(new(id + "/" + index, name, type == "Model" ? "model" : "weapon", field, Text(modifier, "type"), Text(modifier, "value"),
                WeaponType: type is "Melee" or "Ranged" ? type : "", Condition: EffectTest(modifier, registry, Text(node, "id")), WeaponPredicate: predicate,
                Recipient: affects == ModelStatModifiers.UnitGroupSelector ? new("attached-unit", SourceSelector: affects) : null));
        }
        var parsed = rosterRule ? DatasheetRuleText.WeaponAbilities(name, description, true) : DatasheetRuleText.Enhancement(name, description);
        // Prefer structural modifiers per target/characteristic; prose supplies missing
        // effects and battlefield conditions that BSData cannot encode as selections.
        result.AddRange(parsed.Where(p => p.Condition?.Kind == "battlefield" || !result.Any(m => ModelStatModifiers.Represents(m, p))));
        return result;
    }
}
