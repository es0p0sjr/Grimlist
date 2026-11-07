namespace GrimList.Core;

public record PointChange(string Unit, string Faction, int Models, int Copy, int? Before, int? After, string Context = "")
{
    public string Description => $"{Faction} / {Unit}{(Context.Length == 0 ? "" : " (" + Context + ")")} · {Models} models · copy {Copy}+ : {Before?.ToString() ?? "—"} → {After?.ToString() ?? "—"} pts";
}
public static class DatasetChanges
{
    public static List<PointChange> Compare(Dataset? oldData, Dataset next)
    {
        static IEnumerable<(UnitDefinition u, PointOption o, string Context)> Tiers(Dataset? data) => (data?.Units ?? []).SelectMany(u =>
            u.Options.Select(o => (u, o, "")).Concat((u.AlliedOptions ?? []).Select(o => (u, o, "Allies")))
                .Concat((u.DetachmentCosts ?? []).GroupBy(c => c.Detachment).SelectMany(g => u.Options.Select(o => (u, o with { Points = o.Points + g.Sum(c => c.Points) }, g.Key)))));
        var before = Tiers(oldData).ToDictionary(p => (next.FindUnit(p.u.Key)?.Key ?? p.u.Key, p.o.Models, p.o.FromCopy, p.Context));
        var after = Tiers(next).ToDictionary(p => (p.u.Key, p.o.Models, p.o.FromCopy, p.Context));
        List<PointChange> changes = [];
        foreach (var key in before.Keys.Union(after.Keys))
        {
            var hasOld = before.TryGetValue(key, out var a);
            var hasNew = after.TryGetValue(key, out var b);
            if (hasOld && hasNew && a.o.Points == b.o.Points) continue;
            var unit = hasNew ? b.u : a.u;
            changes.Add(new(unit.Name, unit.Faction, key.Models, key.FromCopy, hasOld ? a.o.Points : null, hasNew ? b.o.Points : null, key.Context));
        }
        return changes;
    }
}
