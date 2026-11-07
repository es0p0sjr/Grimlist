using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static EffectRecipient ReadEffectRecipient(JsonObject owner, JsonObject modifier, JsonObject rule, Dictionary<string, JsonObject> registry)
    {
        var selector = Text(modifier, "affects");
        if (selector != "group" && !selector.StartsWith("group.", StringComparison.Ordinal)) return new("unknown", SourceSelector: selector);
        if (Text(modifier, "scope") is not ("" or "self" or "parent" or "root-entry")) return new("unknown", SourceSelector: selector);
        EffectCondition? predicate = null;
        if (selector != "group")
        {
            var id = selector[6..];
            if (!registry.TryGetValue(id, out var category)) return new("unknown", SourceSelector: selector);
            if (category.Parent?.Parent is JsonObject parent && Children(parent, "categoryEntries").Contains(category))
                predicate = new("keyword", Clean(Text(category, "name")));
            else if (Text(category, "type") is "unit" or "model") predicate = new("unit", id);
            else return new("unknown", SourceSelector: selector);
        }
        // A broad source information link sometimes lacks the restriction stated in
        // its owning ability. Narrow it only through a complete, matching grant clause.
        var definitions = Children(owner, "profiles").Concat(Children(owner, "rules"))
            .Concat(Children(owner, "infoLinks").Where(l => registry.ContainsKey(Text(l, "targetId"))).Select(l => Resolve(l, registry)));
        var prose = definitions.Select(d => new AbilityProfile(Clean(Text(d, "name")),
            Clean(Text(d, "description")) is { Length: > 0 } description ? description
                : string.Join("\n", Children(d, "characteristics").Select(c => Clean(Text(c, "$text"))))))
            .SelectMany(DatasheetRuleText.Attached).Where(m => m.Target == "ability" && m.Operation == Text(modifier, "type")
                && EffectiveDatasheets.Key(m.Value) == EffectiveDatasheets.Key(Text(rule, "name"))).ToList();
        if (prose.Count == 1 && prose[0].Recipient is { } narrowed)
            return narrowed with { SourceSelector = selector, Predicate = predicate == null ? narrowed.Predicate
                : narrowed.Predicate == null ? predicate : Both(predicate, narrowed.Predicate) };
        return new("attached-unit", predicate, selector);
    }
}
