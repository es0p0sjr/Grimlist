using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static AssociationDefinition ReadAssociations(JsonObject root, Dictionary<string, JsonObject> registry)
    {
        var entryId = Text(root, "id");
        var identities = Children(root, "categoryLinks").Where(c => !Flag(c, "hidden")).Select(c => Text(c, "targetId"))
            .Append(entryId).Where(id => id.Length > 0).Distinct().ToList();
        List<AssociationConstraint> constraints = []; List<string> warnings = [];
        string Name(JsonObject node) => registry.TryGetValue(Text(node, "childId"), out var target) ? Text(target, "name")
            : Text(node, "childName") is { Length: > 0 } name ? name : Text(node, "childId");
        bool Scope(string scope) => scope == entryId || scope is "self" or "root-entry" or "unit" or "unit-self" or "roster" or "force";
        AssociationCondition Parse(JsonObject node, bool group = false)
        {
            if (Children(node, "localConditionGroups").Count + Children(node, "repeats").Count > 0
                || Flag(node, "unsupportedParentCondition") || Text(node, "affects").Length > 0)
                throw new InvalidDataException("Unsupported association condition scope/repeat.");
            var kind = group ? Text(node, "type").ToLowerInvariant() : "and";
            if (kind is not ("and" or "or")) throw new InvalidDataException("Unsupported association condition group.");
            List<AssociationCondition> conditions = [];
            foreach (var c in Children(node, "conditions"))
            {
                var field = Text(c, "field");
                // Some association limits also depend on the bodyguard's own model count.
                var modelCount = field == "selections" && Text(c, "childId") == "model"
                    && Text(c, "scope") is "self" or "root-entry" or "unit" or "unit-self";
                if (field != "associations" && !modelCount || !Scope(Text(c, "scope")) || Flag(c, "percentValue") || Flag(c, "includeChildForces")
                    || Text(c, "childId").Length == 0 || Text(c, "type") is not ("atLeast" or "atMost" or "greaterThan" or "lessThan" or "equalTo" or "notEqualTo"))
                    throw new InvalidDataException("Unsupported association condition: " + c.ToJsonString());
                conditions.Add(new("count", Text(c, "scope"), Text(c, "childId"), Name(c), Text(c, "type"), Number(c, "value"), Field: field));
            }
            conditions.AddRange(Children(node, "conditionGroups").Select(c => Parse(c, true)));
            return new(kind, Children: conditions);
        }
        foreach (var constraint in Children(root, "constraints").Where(c => Text(c, "field") == "associations"))
        {
            try
            {
                var id = Text(constraint, "id"); var scope = Text(constraint, "scope");
                if (id.Length == 0 || !Scope(scope) || Text(constraint, "type") is not ("min" or "max"))
                    throw new InvalidDataException("Unsupported association constraint: " + constraint.ToJsonString());
                List<AssociationModifier> modifiers = [];
                var order = 0;
                foreach (var m in Modifiers(root).Where(m => Text(m, "field") == id && !InactiveGameMode(m, registry)))
                {
                    if (Text(m, "type") is not ("set" or "increment" or "decrement" or "multiply")) throw new InvalidDataException("Unsupported association modifier: " + Text(m, "type"));
                    List<NumericRepeat> repeats = [];
                    foreach (var r in Children(m, "repeats"))
                    {
                        var field = Text(r, "field"); var queryScope = Text(r, "scope");
                        if (!Scope(queryScope) || Flag(r, "percentValue") || Flag(r, "includeChildForces") || Number(r, "value") == 0
                            || field != "associations" && !(field == "selections" && Text(r, "childId") == "model" && queryScope is "self" or "root-entry" or "unit" or "unit-self"))
                            throw new InvalidDataException("Unsupported association repeat query.");
                        repeats.Add(new(new(queryScope, Text(r, "childId"), field, Flag(r, "includeChildSelections"), Flag(r, "includeChildForces"), entryId),
                            Number(r, "value"), Text(r, "repeats").Length == 0 ? 1 : Number(r, "repeats"), Flag(r, "roundUp")));
                    }
                    var condition = (JsonObject)m.DeepClone(); condition.Remove("repeats");
                    modifiers.Add(new(Text(m, "type"), Number(m, "value"), Parse(condition), repeats, order++, Text(m, "id")));
                }
                var childId = Text(constraint, "childId");
                constraints.Add(new(id, scope, childId.Length == 0 ? "any" : childId, childId.Length == 0 ? "All attachments" : Name(constraint),
                    Text(constraint, "type"), Number(constraint, "value"), modifiers));
            }
            catch (InvalidDataException ex) { warnings.Add($"{Text(constraint, "id")}: {ex.Message}"); }
        }
        return new(entryId, identities, constraints, warnings);
    }
}
