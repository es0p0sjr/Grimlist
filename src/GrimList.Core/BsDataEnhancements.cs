using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    // BSData's entry type 'upgrade' also covers ordinary enhancements and equipment.
    // Within an Enhancement group, the roster cap and shared choice cost distinguish Upgrades.
    // Preserve this distinction in IsUpgrade even when the card text has no UPGRADE
    // label or bearer restriction: losing it incorrectly makes ordinary units ineligible.
    private static bool ReadUpgrade(JsonObject node, Dictionary<string, JsonObject> registry)
    {
        if (Children(node, "categoryLinks").Any(c => UnitIdentity.Normalize(Text(c, "name")) == "UPGRADE"
            || registry.TryGetValue(Text(c, "targetId"), out var category) && UnitIdentity.Normalize(Text(category, "name")) == "UPGRADE")) return true;
        return Children(node, "constraints").Any(c => Text(c, "field") == "selections" && Text(c, "scope") is "roster" or "force"
            && Text(c, "type") == "max" && Number(c, "value") == 3)
            && Children(node, "costs").Any(c => Text(c, "name") == "Enhancements"
                && Modifiers(node).Any(m => Text(m, "field") == Text(c, "typeId") && Text(m, "type") == "divide"));
    }
    private static EnhancementCondition? ReadEnhancementEligibility(JsonObject group, JsonObject node,
        Dictionary<string, JsonObject> registry, Dictionary<string, string> detachments, string catalogueId)
    {
        EnhancementCondition Condition(JsonObject c)
        {
            if (Flag(c, "percentValue") || !int.TryParse(Text(c, "value"), out var count)) return new("unknown");
            var id = Text(c, "childId"); var scope = Text(c, "scope"); var comparison = Text(c, "type");
            if (comparison is not ("equalTo" or "notEqualTo" or "lessThan" or "greaterThan" or "atLeast" or "atMost" or "instanceOf" or "notInstanceOf")) return new("unknown");
            // Membership predicates have no numeric threshold. BSData writes both 0 and 1
            // in their unused value field; normalize them to the semantic Boolean value.
            if (comparison is "instanceOf" or "notInstanceOf") count = 1;
            if (scope == "primary-catalogue" && comparison is "instanceOf" or "notInstanceOf")
                return new("constant", id == catalogueId ? "1" : "0", comparison, count);
            if (Text(c, "field") == "selections" && scope is "force" or "roster" && detachments.TryGetValue(id, out var detachment))
                return new("detachment", detachment, comparison, count);
            if (!registry.TryGetValue(id, out var target)) return new("unknown");
            var name = Text(target, "name");
            // The editor models an ordinary Army Roster, not Crusade or Boarding Actions.
            if (Text(c, "field") == "forces" && scope == "roster" && name is "Crusade Force" or "Army Roster"
                || scope == "force" && comparison is "instanceOf" or "notInstanceOf" && name == "Boarding Actions")
                return new("constant", name == "Army Roster" ? "1" : "0", comparison, count);
            if (Text(c, "field") != "selections" || scope is not ("ancestor" or "parent" or "root-entry")
                || comparison is not ("instanceOf" or "notInstanceOf") || count != 1) return new("unknown");
            if (Text(target, "type") is "unit" or "model") return new("unit", id, comparison, count);
            // Only category definitions are keywords, never arbitrary selected upgrades.
            if (Text(target, "type").Length > 0 || Text(target, "typeId").Length > 0 || name.Length == 0) return new("unknown");
            return new("keyword", name.StartsWith("Faction:", StringComparison.OrdinalIgnoreCase) ? name[8..].Trim() : name, comparison, count);
        }
        EnhancementCondition Conditions(JsonObject container, bool nested = false)
        {
            if (Flag(container, "unsupportedParentCondition") || Children(container, "repeats").Count > 0
                || Children(container, "localConditionGroups").Count > 0 || Text(container, "affects").Length > 0) return new("unknown");
            var kind = nested ? Text(container, "type") : "and";
            if (kind is not ("and" or "or")) return new("unknown");
            return new(kind, Children: Children(container, "conditions").Select(Condition)
                .Concat(Children(container, "conditionGroups").Select(g => Conditions(g, true))).ToList());
        }
        EnhancementCondition Visible(JsonObject entry)
        {
            EnhancementCondition visible = new("constant", Flag(entry, "hidden") ? "0" : "1");
            foreach (var modifier in Modifiers(entry).Where(m => Text(m, "field") == "hidden"))
            {
                if (Text(modifier, "type") != "set" || Text(modifier, "value") is not ("true" or "false")) return new("unknown");
                var condition = Conditions(modifier);
                visible = Flag(modifier, "value") ? new("and", Children: [visible, new("not", Children: [condition])])
                    : new("or", Children: [visible, condition]);
            }
            return visible;
        }
        var result = new EnhancementCondition("and", Children: [Visible(group), Visible(node)]);
        // An inline Enhancement group is only offered on its owning unit in BSData.
        // Retain that placement restriction when projecting the group into reference cards.
        for (JsonNode? parent = group.Parent; parent != null; parent = parent.Parent)
            if (parent is JsonObject owner && Text(owner, "type") is "unit" or "model")
            {
                result.Children!.Add(new("unit", Text(owner, "id"), "instanceOf"));
                break;
            }
        bool Known(EnhancementCondition c) => c.Kind != "unknown" && (c.Children ?? []).All(Known);
        return Known(result) ? result : null;
    }

    // Re-read references without rebuilding units, prices, equipment or saved armies.
    public static List<ReferenceCard> ImportEnhancementReferences(IReadOnlyList<BsDataDocument> documents, SourceInfo source)
    {
        List<ReferenceCard> cards = [];
        foreach (var primary in ArmyCatalogues(documents))
        {
            var registry = new Dictionary<string, JsonObject>(); var loaded = new HashSet<string>();
            void Include(BsDataDocument document)
            {
                if (!loaded.Add(document.Id)) return;
                foreach (var entry in Walk(document.Root))
                    if (Text(entry, "id") is { Length: > 0 } id) registry.TryAdd(id, entry);
                foreach (var link in Children(document.Root, "catalogueLinks"))
                    if (documents.FirstOrDefault(d => d.Id == Text(link, "targetId")) is { } dependency) Include(dependency);
            }
            Include(primary);
            if (documents.FirstOrDefault(d => d.Id == Text(primary.Root, "gameSystemId")) is { } game) Include(game);
            cards.AddRange((ReadReferences(primary, registry, source, NativeCatalogues(documents, primary).ToList()).ReferenceCards ?? [])
                .Where(c => c.Section == "Enhancements"));
        }
        return cards;
    }
}
