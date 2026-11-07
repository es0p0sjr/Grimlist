using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

// Pinned source evidence includes negative and unresolved decisions. Absence alone
// is never authorization to remove an installed reference.
public record ArmyRuleScope(SourceInfo Source, string Faction, string CatalogueId,
    List<ReferenceCard> Cards, List<string> Excluded, List<string> Unresolved, List<string>? ContextKeywords = null,
    Dictionary<string, string>? ExcludedNames = null);

public static partial class BsDataImporter
{
    public static List<ArmyRuleScope> ImportArmyRules(IReadOnlyList<BsDataDocument> documents, SourceInfo source)
    {
        List<ArmyRuleScope> result = [];
        foreach (var primary in ArmyCatalogues(documents))
        {
            var registry = new Dictionary<string, JsonObject>();
            var seen = new HashSet<string>();
            void Include(BsDataDocument doc)
            {
                if (!seen.Add(doc.Id)) return;
                foreach (var node in Walk(doc.Root))
                    if (Text(node, "id") is { Length: > 0 } id) registry.TryAdd(id, node);
                foreach (var link in Children(doc.Root, "catalogueLinks"))
                    if (documents.FirstOrDefault(d => d.Id == Text(link, "targetId")) is { } dependency) Include(dependency);
            }
            Include(primary);
            if (documents.FirstOrDefault(d => d.Id == Text(primary.Root, "gameSystemId")) is { } game) Include(game);
            ReadReferences(primary, registry, source, NativeCatalogues(documents, primary).ToList(), result);
        }
        return result;
    }

    private static ArmyRuleScope ReadArmyRuleScope(BsDataDocument primary, List<BsDataDocument> native,
        Dictionary<string, JsonObject> registry, SourceInfo source, HashSet<string> scopedRules)
    {
        var faction = FactionName(primary);
        var nativeIds = native.Select(d => d.Id).ToHashSet();
        var roots = new List<JsonObject>(); var visited = new HashSet<string>();
        void Visit(JsonObject root)
        {
            if (!visited.Add(Text(root, "id"))) return;
            roots.Add(root);
            foreach (var link in Children(root, "catalogueLinks"))
                if (registry.TryGetValue(Text(link, "targetId"), out var dependency)) Visit(dependency);
        }
        Visit(primary.Root);
        // A shared library can contain detachments that are not native roster roots.
        // Their linked rules must not accidentally become faction-wide references.
        foreach (var group in roots.SelectMany(Walk).Where(n => ReferenceModel.Key(Text(n, "name")) is "DETACHMENT" or "DETACHMENTS"))
            foreach (var entry in Children(group, "selectionEntries").Concat(Children(group, "entryLinks")))
            {
                if (Text(entry, "targetId").Length > 0 && !registry.ContainsKey(Text(entry, "targetId"))) continue;
                var node = Resolve(entry, registry);
                foreach (var rule in Children(node, "rules")) scopedRules.Add(Text(rule, "id"));
                foreach (var link in Children(node, "infoLinks").Where(l => Text(l, "type") == "rule")) scopedRules.Add(Text(link, "targetId"));
            }
        List<(JsonObject Rule, JsonObject? Link, bool Owned)> candidates = [];
        foreach (var root in roots)
        {
            var owned = nativeIds.Contains(Text(root, "id")) || FactionName(new("", root)) == faction;
            candidates.AddRange(Children(root, "rules").Concat(Children(root, "sharedRules")).Select(r => (r, (JsonObject?)null, owned)));
            foreach (var link in Children(root, "infoLinks").Where(l => Text(l, "type") == "rule"))
                if (registry.TryGetValue(Text(link, "targetId"), out var rule)) candidates.Add((rule, link, owned));
        }
        List<ReferenceCard> cards = []; HashSet<string> excluded = [], unresolved = [];
        foreach (var group in candidates.GroupBy(c => Text(c.Rule, "id")))
        {
            var id = group.Key;
            if (id.Length == 0) continue;
            if (scopedRules.Contains(id)) { excluded.Add(id); continue; }
            var accepted = new List<(JsonObject Rule, EffectCondition Visibility)>(); var uncertain = false;
            foreach (var (rule, link, owned) in group)
            {
                // A shared library may serve several factions. Explicit catalogue
                // conditions, or a root reference in an owned catalogue, establish scope.
                var explicitScope = Modifiers(rule).Concat(link == null ? [] : Modifiers(link))
                    .Where(m => Text(m, "field") == "hidden").SelectMany(RuleNodes)
                    .Any(c => Text(c, "scope") == "primary-catalogue" && nativeIds.Contains(Text(c, "childId")));
                if (!owned && !explicitScope) continue;
                var visibility = ArmyRuleVisibility(rule, nativeIds, registry);
                if (link != null) visibility = Both(visibility, ArmyRuleVisibility(link, nativeIds, registry));
                var visible = CatalogueTest(visibility, nativeIds);
                if (visible == false) continue;
                if (!KnownRuleVisibility(visibility)) uncertain = true;
                else if (Text(rule, "description").Length > 0) accepted.Add((rule, visibility));
            }
            if (uncertain) { unresolved.Add(id); continue; }
            if (accepted.Count == 0) { excluded.Add(id); continue; }
            var definitions = accepted.DistinctBy(r => r.Rule.ToJsonString()).ToList();
            if (definitions.Count != 1) { unresolved.Add(id); continue; }
            var definition = definitions[0].Rule;
            var applicability = SimplifyCondition(new("or", Children: accepted.Select(a => a.Visibility).ToList()));
            var text = Clean(Text(definition, "description"));
            var effectVisibility = EffectVisibility(definition, registry, primary.Id);
            var effects = ReadEnhancementModifiers(definition, registry, text, rosterRule: true)
                .Select(m => m with { Condition = m.Condition == null ? effectVisibility : Both(effectVisibility, m.Condition) }).ToList();
            cards.Add(new(faction, "Army Rules", Clean(Text(definition, "name")), text, Source: source,
                Details: new(Id: "ref-" + ReferenceModel.Hash(DataJson.Write(new[] { source.Edition, primary.Id, id, "" })),
                    Edition: source.Edition, SourceId: "bsdata:" + source.Edition, SourceCardId: id, SourceType: "BSData",
                    Scope: "Faction", FactionId: primary.Id), Modifiers: effects,
                Applicability: applicability.Kind == "constant" && applicability.Value == "1" ? null : applicability));
        }
        var excludedNames = candidates.Where(c => excluded.Contains(Text(c.Rule, "id")))
            .GroupBy(c => Text(c.Rule, "id")).Where(g => g.Select(c => Clean(Text(c.Rule, "name"))).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => Clean(Text(g.First().Rule, "name")));
        return new(source, faction, primary.Id, cards, excluded.Order().ToList(), unresolved.Order().ToList(), Keywords(primary.Root, registry), excludedNames);
    }

    private static bool? And(bool? a, bool? b) => a == false || b == false ? false : a == true && b == true ? true : null;
    private static IEnumerable<JsonObject> RuleNodes(JsonObject node)
    {
        yield return node;
        foreach (var value in node.Select(p => p.Value))
            if (value is JsonObject child) { foreach (var n in RuleNodes(child)) yield return n; }
            else if (value is JsonArray array)
                foreach (var item in array.OfType<JsonObject>()) foreach (var n in RuleNodes(item)) yield return n;
    }
    private static bool KnownRuleVisibility(EffectCondition c) => c.Kind is "constant" or "catalogue" or "detachment"
        || c.Kind is "and" or "or" or "not" && (c.Children ?? []).All(KnownRuleVisibility);
    private static EffectCondition ResolveRuleCatalogueTests(EffectCondition condition, HashSet<string> catalogues)
    {
        if (condition.Kind == "catalogue" && CatalogueTest(condition, catalogues) is { } value)
            return new("constant", value ? "1" : "0");
        return SimplifyCondition(condition with { Children = condition.Children?.Select(c => ResolveRuleCatalogueTests(c, catalogues)).ToList() });
    }
    private static EffectCondition ArmyRuleVisibility(JsonObject node, HashSet<string> catalogues, Dictionary<string, JsonObject> registry)
    {
        EffectCondition visible = new("constant", Flag(node, "hidden") ? "0" : "1");
        foreach (var modifier in Modifiers(node).Where(m => Text(m, "field") == "hidden"))
        {
            var conditions = RuleNodes(modifier).Where(c => Text(c, "scope").Length > 0).ToList();
            // A rule's per-model exception does not hide the faction's reference card.
            // Mixed or roster conditions must instead be resolved, never ignored.
            if (conditions.Count > 0 && conditions.All(c => Text(c, "scope") is "model-or-unit" or "self" or "unit" or "model" or "ancestor" or "parent" or "root-entry")) continue;
            if (Text(modifier, "type") != "set") return new("unknown");
            var condition = ResolveRuleCatalogueTests(EffectTest(modifier, registry, ""), catalogues);
            var evaluated = CatalogueTest(condition, catalogues);
            var test = evaluated.HasValue ? new EffectCondition("constant", evaluated.Value ? "1" : "0") : condition;
            var hide = Flag(modifier, "value");
            visible = hide ? Both(visible, new("not", Children: [test])) : SimplifyCondition(new("or", Children: [visible, test]));
        }
        return visible;
    }
    private static bool? CatalogueTest(EffectCondition condition, HashSet<string> catalogues)
    {
        var children = condition.Children ?? [];
        if (condition.Kind == "not" && children.Count == 1) return !CatalogueTest(children[0], catalogues);
        if (condition.Kind == "and") return children.Aggregate((bool?)true, (value, c) => And(value, CatalogueTest(c, catalogues)));
        if (condition.Kind == "or")
        {
            var values = children.Select(c => CatalogueTest(c, catalogues)).ToList();
            return values.Contains(true) ? true : values.All(v => v == false) ? false : null;
        }
        if (condition.Kind is not ("catalogue" or "constant")) return null;
        var count = condition.Kind == "catalogue" ? catalogues.Contains(condition.Value) ? 1 : 0 : condition.Value == "1" ? 1 : 0;
        return condition.Comparison switch
        {
            "instanceOf" => count > 0, "notInstanceOf" => count == 0,
            "equalTo" => count == condition.Count, "notEqualTo" => count != condition.Count,
            "atLeast" => count >= condition.Count, "atMost" => count <= condition.Count,
            "greaterThan" => count > condition.Count, "lessThan" => count < condition.Count,
            "" => count > 0, _ => null
        };
    }
}
