using System.Runtime.CompilerServices;

namespace GrimList.Core;

public record SelectionLimit(string Id, string Type, int Value, List<NumericModifier> Modifiers,
    string Scope = "parent", bool IncludeChildSelections = false, bool IncludeChildForces = false);
public record SelectionNode(string Key, string SourceId, string TargetId, string Name, string Kind,
    List<string> IdentityIds, List<SelectionLimit> Limits, List<SelectionNode> Children,
    string DefaultId = "", int Points = 0);
public record SelectionError(string Message, SelectionTest Condition, string SourceId);
public record SelectionComposition(SelectionNode Root, List<SelectionError>? Errors = null);
public record CompositionState(Dictionary<string, int> Counts, Dictionary<string, int> Roles, int Models);

// The tree is retained in the library. Candidate enumeration is only a bounded,
// cached search over that tree, never a replacement for its dynamic constraints.
public static class SelectionCompositions
{
    private static readonly ConditionalWeakTable<SelectionComposition, List<CompositionState>> cache = new();
    public static IEnumerable<SelectionNode> Traverse(SelectionNode node) => new[] { node }.Concat(node.Children.SelectMany(Traverse));
    public static List<CompositionState> States(SelectionComposition definition) => cache.GetValue(definition, Enumerate);

    public static void Validate(SelectionComposition definition, IEnumerable<string> roleIds)
    {
        HashSet<string> keys = []; var roles = roleIds.ToHashSet();
        void Check(SelectionNode n, int depth)
        {
            if (depth > 30 || n.Key.Length == 0 || !keys.Add(n.Key) || n.SourceId.Length == 0 || n.Points < 0
                || n.Kind is not ("" or "unit" or "model" or "upgrade") || n.Kind == "model" && !roles.Contains(n.SourceId))
                throw new InvalidDataException("Invalid recursive selection definition: " + n.Key);
            foreach (var l in n.Limits)
            {
                if (l.Value < 0 || l.Type is not ("min" or "max")) throw new InvalidDataException("Invalid selection limit: " + l.Id);
                RuleEvaluation.Validate(l.Modifiers);
            }
            foreach (var c in n.Children) Check(c, depth + 1);
        }
        Check(definition.Root, 0);
        foreach (var error in definition.Errors ?? [])
            RuleEvaluation.Validate([new("set", 0, error.Condition, [], SourceId: error.SourceId)]);
    }

    private static List<CompositionState> Enumerate(SelectionComposition definition)
    {
        var root = definition.Root;
        int Envelope(SelectionNode n, string type, int fallback)
        {
            var limits = n.Limits.Where(l => l.Type == type).ToList();
            if (limits.Count == 0) return fallback;
            int Bound(SelectionLimit l)
            {
                var value = l.Value;
                foreach (var m in l.Modifiers)
                {
                    // Safe search envelope only. Exact conditions/repeats run below.
                    if (type == "min") { if (m.Operation is "decrement" or "set" or "multiply") value = 0; }
                    else value = m.Operation switch
                    {
                        "set" => Math.Max(value, m.Value),
                        "increment" => m.Repeats.Count > 0 ? 100 : Math.Min(100, value + m.Value),
                        "multiply" => Math.Min(100, value * Math.Max(1, m.Value)), _ => value
                    };
                }
                return Math.Clamp(value, 0, 100);
            }
            return type == "min" ? limits.Max(Bound) : limits.Min(Bound);
        }
        List<Dictionary<string, int>> Generate(SelectionNode n, int cap)
        {
            var min = n == root ? 1 : Envelope(n, "min", n.Kind == "unit" ? 1 : 0);
            var max = n == root ? 1 : Envelope(n, "max", n.Kind == "model" || n.Kind == "" ? cap : 1);
            if (n.Kind == "model" && n.Children.Count == 0) return Enumerable.Range(min, Math.Max(0, Math.Min(cap, max) - min + 1))
                .Select(count => new Dictionary<string, int> { [n.Key] = count }).ToList();
            List<Dictionary<string, int>> combinations = [new()];
            foreach (var child in n.Children)
            {
                var children = Generate(child, child.Kind is "upgrade" or "unit" ? 100 : n.Kind == "" ? Math.Min(cap, max) : cap);
                List<Dictionary<string, int>> next = [];
                foreach (var a in combinations)
                    foreach (var b in children)
                    {
                        var combined = new Dictionary<string, int>(a);
                        foreach (var pair in b) combined.Add(pair.Key, pair.Value);
                        if (n.Kind == "" && ChildSelections(n, combined) > max) continue;
                        next.Add(combined);
                        if (next.Count > 50000) throw new InvalidDataException("Composition search exceeds 50,000 candidates; selection constraints need additional support.");
                    }
                combinations = next;
            }
            List<Dictionary<string, int>> result = [];
            if (n.Kind == "")
            {
                foreach (var c in combinations)
                {
                    var count = ChildSelections(n, c);
                    if (count >= min && count <= max) { c[n.Key] = count; result.Add(c); }
                }
            }
            else
            {
                if (max > 1 && n != root && n.Kind != "model") throw new InvalidDataException("Repeated model containers need separate instance selections.");
                if (min == 0) result.Add(Traverse(n).ToDictionary(c => c.Key, _ => 0));
                for (var quantity = Math.Max(1, min); quantity <= max; quantity++)
                    foreach (var c in combinations)
                    {
                        var selected = c.ToDictionary(p => p.Key, p => checked(p.Value * quantity));
                        selected[n.Key] = quantity; result.Add(selected);
                    }
            }
            return result;
        }
        var nodes = Traverse(root).ToList();
        var parents = nodes.SelectMany(n => n.Children.Select(c => (c.Key, Parent: n))).ToDictionary(p => p.Key, p => p.Parent);
        List<CompositionState> states = [];
        foreach (var counts in Generate(root, 100))
        {
            var roles = nodes.Where(n => n.Kind == "model").GroupBy(n => n.SourceId)
                .ToDictionary(g => g.Key, g => g.Sum(n => counts.GetValueOrDefault(n.Key)));
            var state = new CompositionState(counts, roles, roles.Values.Sum());
            if (state.Models is < 1 or > 100) continue;
            var context = new SelectionContext(definition, state);
            bool Valid(SelectionNode n, bool active)
            {
                if (!active) return true;
                var count = counts.GetValueOrDefault(n.Key);
                foreach (var limit in n.Limits)
                {
                    var bound = RuleEvaluation.Effective(limit.Value, limit.Modifiers, context.Count);
                    var parent = parents.GetValueOrDefault(n.Key);
                    while (parent?.Kind == "") parent = parents.GetValueOrDefault(parent.Key);
                    if (parent?.Kind == "model") bound = checked(bound * counts.GetValueOrDefault(parent.Key));
                    if (limit.Type == "min" ? count < bound : count > bound) return false;
                }
                return n.Children.All(c => Valid(c, n.Kind == "" || count > 0));
            }
            if (Valid(root, true)) states.Add(state);
        }
        if (states.Count == 0) throw new InvalidDataException("No valid composition satisfies the imported selection constraints.");
        return states;
    }

    private static int ChildSelections(SelectionNode n, Dictionary<string, int> counts) => n.Children.Sum(c =>
        c.Kind == "" ? ChildSelections(c, counts) : counts.GetValueOrDefault(c.Key));

    public static CompositionState? State(SelectionComposition definition, UnitLoadout loadout)
    {
        var roles = loadout.Models.GroupBy(m => m.RoleId).ToDictionary(g => g.Key, g => g.Count());
        return States(definition).FirstOrDefault(s => s.Models == loadout.Models.Count
            && roles.All(r => s.Roles.GetValueOrDefault(r.Key) == r.Value)
            && s.Roles.All(r => roles.GetValueOrDefault(r.Key) == r.Value));
    }

    public static List<string> Errors(SelectionComposition definition, UnitLoadout loadout)
    {
        var state = State(definition, loadout);
        if (state == null) return ["Model/bundle selections do not satisfy the imported composition constraints."];
        var context = new SelectionContext(definition, state, loadout);
        return (definition.Errors ?? []).Where(e => RuleEvaluation.Test(e.Condition, context.Count)).Select(e => e.Message).ToList();
    }

    public static UnitLoadout Select(UnitDefinition unit, UnitLoadout current, int models, string? roleId = null, int count = 0)
    {
        var definition = Loadouts.Definition(unit);
        var composition = definition.Composition!;
        var before = current.Models.GroupBy(m => m.RoleId).ToDictionary(g => g.Key, g => g.Count());
        var defaults = Traverse(composition.Root).Where(n => n.DefaultId.Length > 0).Select(n => n.DefaultId).ToHashSet();
        var replaceable = Loadouts.Sections(unit).Select(s => s.DefaultRoleId).ToHashSet();
        if (roleId != null)
        {
            replaceable.Add(roleId);
            // Removing one member of a fixed multi-model bundle removes the
            // complete bundle. Unrelated selected specialists stay selected.
            foreach (var bundle in Traverse(composition.Root).Where(n => n.Kind == "upgrade" && n.Children.Count > 0
                && n.Children.All(c => c.Kind == "model" && c.Limits.Any(l => l.Type == "min" && l.Modifiers.Count == 0)
                    && c.Limits.Any(l => l.Type == "max" && l.Modifiers.Count == 0
                        && c.Limits.Any(m => m.Type == "min" && m.Value == l.Value)))
                && n.Children.Any(c => c.SourceId == roleId)))
                replaceable.UnionWith(bundle.Children.Select(c => c.SourceId));
        }
        var state = States(composition).Where(s => s.Models == models && (roleId == null || s.Roles.GetValueOrDefault(roleId) == count))
            .Where(s => roleId == null || before.All(r => replaceable.Contains(r.Key) || s.Roles.GetValueOrDefault(r.Key) >= r.Value))
            .OrderBy(s => s.Roles.Sum(r => Math.Abs(r.Value - before.GetValueOrDefault(r.Key))))
            .ThenByDescending(s => s.Roles.Where(r => defaults.Contains(r.Key)).Sum(r => r.Value))
            .FirstOrDefault() ?? throw new InvalidDataException("No legal composition satisfies the proposed model/bundle selection.");
        List<ModelLoadout> selected = [];
        foreach (var role in definition.Roles)
        {
            var existing = current.Models.Where(m => m.RoleId == role.Id).ToList();
            for (var i = 0; i < state.Roles.GetValueOrDefault(role.Id); i++)
                selected.Add(i < existing.Count ? existing[i] : new(role.Id, Loadouts.Defaults(role.Groups, models)));
        }
        return new(selected, current.Equipment.Count == 0 ? Loadouts.Defaults(definition.UnitGroups, models) : current.Equipment.ToList());
    }
}

// A proposed composition is supplied by the caller. Nothing here mutates the
// committed army. Group nodes are structural; bundles are actual selections.
public sealed class SelectionContext
{
    private readonly SelectionComposition definition;
    private readonly CompositionState state;
    private readonly List<SelectionNode> nodes;
    private readonly Dictionary<string, SelectionNode> parents = [];
    private readonly UnitLoadout? loadout;
    private readonly int copy;
    private readonly bool allied;
    private readonly string catalogueId;
    public SelectionContext(SelectionComposition definition, CompositionState state, UnitLoadout? loadout = null,
        int copy = 1, bool allied = false, string catalogueId = "")
    {
        this.definition = definition; this.state = state; this.loadout = loadout; this.copy = copy; this.allied = allied; this.catalogueId = catalogueId;
        nodes = SelectionCompositions.Traverse(definition.Root).ToList();
        foreach (var node in nodes) foreach (var child in node.Children) parents[child.Key] = node;
    }
    public int Count(SelectionQuery query)
    {
        if (query.Field == "copy-before") return copy - 1;
        if (query.Field == "copies") return copy;
        if (query.Scope == "primary-catalogue") return !allied && query.ChildId == catalogueId ? 1 : 0;
        if (query.Field != "selections") throw new InvalidDataException("Unsupported selection query field: " + query.Field);
        var origin = nodes.FirstOrDefault(n => n.Key == query.Origin) ?? definition.Root;
        SelectionNode scope;
        if (query.Scope is "unit" or "unit-self" or "root-entry") scope = definition.Root;
        else if (query.Scope == "self") scope = origin;
        else if (query.Scope == "parent")
        {
            scope = parents.GetValueOrDefault(origin.Key) ?? definition.Root;
            while (scope.Kind == "" && parents.TryGetValue(scope.Key, out var parent)) scope = parent;
        }
        else scope = nodes.FirstOrDefault(n => n.IdentityIds.Contains(query.Scope))
            ?? throw new InvalidDataException("Selection scope not available in this context: " + query.Scope);
        IEnumerable<SelectionNode> Direct(SelectionNode n) => n.Children.SelectMany(c => c.Kind == "" ? Direct(c) : new[] { c });
        var candidates = query.IncludeChildSelections ? SelectionCompositions.Traverse(scope).Skip(1) : Direct(scope);
        bool Matches(SelectionNode n) => query.ChildId == "any" || n.Kind == query.ChildId || n.IdentityIds.Contains(query.ChildId);
        if (query.ChildId == "model" && scope.Kind == "model") candidates = candidates.Prepend(scope);
        var matching = candidates.Where(Matches).ToList();
        if (matching.Count == 0)
        {
            var group = SelectionCompositions.Traverse(scope).FirstOrDefault(n => n.Kind == "" && n.IdentityIds.Contains(query.ChildId));
            if (group != null)
            {
                if (!query.IncludeChildSelections) throw new InvalidDataException("A structural group query requires explicit descendant counting.");
                return state.Counts.GetValueOrDefault(group.Key);
            }
            // Equipment selections are stored independently of normalized weapon names.
            if (loadout != null)
            {
                var roles = SelectionCompositions.Traverse(scope).Where(n => n.Kind == "model").Select(n => n.SourceId).ToHashSet();
                var equipment = loadout.Models.Where(m => roles.Contains(m.RoleId)).SelectMany(m => m.Equipment);
                if (scope == definition.Root) equipment = equipment.Concat(loadout.Equipment);
                return equipment.Where(e => e.OptionId == query.ChildId).Sum(e => e.Count);
            }
        }
        return matching.Sum(n => state.Counts.GetValueOrDefault(n.Key));
    }
}
