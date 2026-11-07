using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private sealed record DetachmentBranch(JsonObject Entry, List<JsonObject> Ancestors, List<DetachmentGroupLimit> Limits, bool Unresolved);
    private sealed record DetachmentTree(List<DetachmentBranch> Choices, bool Complete, bool HasContainer);
    private static bool DetachmentContainer(JsonObject node) => ReferenceModel.Key(Text(node, "name")) is "DETACHMENT" or "DETACHMENTS";

    // Walk selection groups, not all descendants: weapons, enhancements and other
    // upgrades inside a detachment are not additional detachment choices.
    private static DetachmentTree ReadDetachmentTree(IEnumerable<JsonObject> nodes, Dictionary<string, JsonObject> registry)
    {
        List<DetachmentBranch> choices = [];
        HashSet<string> visited = []; var complete = true; var found = false;
        void Visit(JsonObject group, List<JsonObject> ancestors, List<DetachmentGroupLimit> inherited, HashSet<string> path, bool unresolved)
        {
            var id = Text(group, "id");
            // Partial/local reference inputs may omit container IDs. Retain their
            // readable choices, but never treat them as complete source authority.
            if (id.Length == 0) complete = false;
            else
            {
                if (!path.Add(id)) { complete = false; return; }
                visited.Add(id);
            }
            var limits = inherited.ToList();
            // The outer Detachment entry is a selector, not a selected detachment.
            // Overall DP/battle-size constraints remain owned by DetachmentRules.
            if (!DetachmentContainer(group))
            {
                foreach (var constraint in Children(group, "constraints"))
                {
                    if (Text(constraint, "field") != "selections") continue;
                    var kind = Text(constraint, "type"); var raw = Text(constraint, "value");
                    var constraintId = Text(constraint, "id");
                    if (Modifiers(group).Any(m => Text(m, "field") == constraintId))
                    { complete = false; unresolved = true; continue; }
                    if ((kind == "min" && raw == "0") || (kind == "max" && raw == "-1")) continue;
                    if (id.Length == 0 || kind != "max" || Text(constraint, "scope") != "parent" || Flag(constraint, "percentValue")
                        || !int.TryParse(raw, out var maximum) || maximum < 0)
                    { complete = false; unresolved = true; continue; }
                    limits.Add(new(id, Clean(Text(group, "name")), maximum));
                }
            }
            var parents = ancestors.Append(group).ToList();
            foreach (var kind in new[] { "selectionEntries", "selectionEntryGroups", "entryLinks" })
            foreach (var child in Children(group, kind))
            {
                var target = Text(child, "targetId");
                if (target.Length > 0 && !registry.ContainsKey(target)) { complete = false; continue; }
                var entry = Resolve(child, registry);
                var isGroup = kind == "selectionEntryGroups" || kind == "entryLinks" && Text(child, "type") == "selectionEntryGroup";
                if (DetachmentContainer(entry) || isGroup && Text(entry, "type").Length == 0)
                    Visit(entry, parents, limits, new(path), unresolved);
                else if (!isGroup && Text(entry, "type") == "upgrade" && Text(entry, "id").Length > 0)
                    choices.Add(new(entry, parents, limits, unresolved));
                else complete = false;
            }
        }
        foreach (var root in nodes.Where(DetachmentContainer))
        {
            found = true;
            if (!visited.Contains(Text(root, "id"))) Visit(root, [], [], [], false);
        }
        return new(choices, complete, found);
    }
}
