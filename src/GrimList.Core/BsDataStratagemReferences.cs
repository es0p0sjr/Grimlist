using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    // Follows actual catalogue links/hierarchy, not names of factions or cards.
    private static List<ReferenceCard> ReadStratagemReferences(BsDataDocument document,
        Dictionary<string, JsonObject> registry, SourceInfo source, Dictionary<string, string> detachments)
    {
        var found = new List<ReferenceCard>(); var visited = new HashSet<(string, string, bool)>();
        IEnumerable<JsonObject> Conditions(JsonObject n) => Children(n, "conditions").Concat(Children(n, "conditionGroups").SelectMany(Conditions));
        void Visit(JsonObject node, string detachmentId, bool inheritedUnknown = false)
        {
            var id = Text(node, "id");
            if (detachments.ContainsKey(id)) detachmentId = id;
            if (id.Length > 0 && !visited.Add((id, detachmentId, inheritedUnknown))) return;
            var visibility = Children(node, "modifiers").Where(m => Text(m, "field") == "hidden").ToList();
            bool Supported(JsonObject m)
            {
                var conditions = Conditions(m).ToList();
                if (Text(m, "type") != "set" || conditions.Count != 1 || Children(m, "conditionGroups").Any()) return false;
                var c = conditions[0]; var hide = Text(m, "value").ToLowerInvariant();
                return detachments.ContainsKey(Text(c, "childId")) && Text(c, "value") == "1"
                    && (hide == "true" && Text(c, "type") == "lessThan" || hide == "false" && Text(c, "type") is "atLeast" or "equalTo");
            }
            var scoped = visibility.Where(Supported)
                .SelectMany(Conditions).Select(c => Text(c, "childId")).Where(detachments.ContainsKey).Distinct().ToList();
            // Conditional visibility involving more than a detachment is not guessed.
            var unknownConditions = inheritedUnknown || visibility.Any(m => !Supported(m)) || Flag(node, "hidden") && visibility.Count == 0;
            var scopes = detachmentId.Length > 0 ? new List<string> { detachmentId } : scoped;
            var profiles = Children(node, "profiles").Concat(Children(node, "infoLinks")
                .Where(l => Text(l, "type") == "profile" && registry.ContainsKey(Text(l, "targetId"))).Select(l => registry[Text(l, "targetId")]));
            foreach (var p in profiles.Where(p => ReferenceModel.Key(Text(p, "typeName")) is "STRATAGEM" or "STRATAGEMS"))
            {
                var fields = Children(p, "characteristics").GroupBy(c => ReferenceModel.Key(Text(c, "name")))
                    .ToDictionary(g => g.Key, g => string.Join("\n", g.Select(c => Clean(Text(c, "$text")))));
                string Field(params string[] names) => names.Select(n => fields.GetValueOrDefault(n, "")).FirstOrDefault(v => v.Length > 0) ?? "";
                var text = string.Join("\n", fields.Select(f => f.Key + ": " + f.Value));
                if (text.Length == 0) continue;
                var targets = scopes.Select(s => new ReferenceTarget(detachments[s], s)).ToList();
                var card = new ReferenceCard(FactionName(document), "Stratagems", Clean(Text(p, "name")), text,
                    targets.Count == 1 ? targets[0].Name : "", Field("CP", "CPCOST", "COST"), source,
                    Details: new(Edition: source.Edition, SourceId: "bsdata:" + source.Edition,
                        SourceCardId: Text(p, "id"), SourceType: "BSData", Scope: targets.Count > 0 ? "Detachment" : "Faction",
                        FactionId: Text(document.Root, "id"), Detachments: targets, Timing: Field("WHEN", "TIMING"), Target: Field("TARGET"),
                        Effect: Field("EFFECT"), Restrictions: Field("RESTRICTIONS"), Status: unknownConditions ? "Unresolved" : "Active"));
                found.Add(ReferenceModel.Canonical(card, source));
            }
            foreach (var key in new[] { "selectionEntries", "selectionEntryGroups", "entryLinks", "infoGroups" })
                foreach (var child in Children(node, key))
                    if (Text(child, "targetId").Length == 0 || registry.ContainsKey(Text(child, "targetId"))) Visit(Resolve(child, registry), detachmentId, unknownConditions);
        }
        Visit(document.Root, "");
        return found.GroupBy(c => c.Details!.Id).Select(g =>
        {
            var first = g.First();
            var targets = g.SelectMany(c => c.Details!.Detachments ?? []).DistinctBy(t => t.Id).ToList();
            return ReferenceModel.Canonical(first with { Detachment = targets.Count == 1 ? targets[0].Name : "",
                Details = first.Details! with { Detachments = targets, Scope = targets.Count > 0 ? "Detachment" : "Faction",
                    Status = g.Any(c => c.Details!.Status == "Unresolved") ? "Unresolved" : "Active" } }, source);
        }).ToList();
    }
}
