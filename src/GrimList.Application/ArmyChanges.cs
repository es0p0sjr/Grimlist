using GrimList.Core;

namespace GrimList.Application;

[Flags]
public enum ArmyChange { None = 0, Equipment = 1, Composition = 2, Attachments = 4, Structure = 8,
    Metadata = 16, Rules = 32, Enhancement = 64, Warlord = 128, Source = 256, Context = 512 }
public sealed record ArmyMutation(long Version, ArmyChange Changes, IReadOnlyList<string> EntryIds);

public static class ArmyChanges
{
    public static ArmyChange Between(ArmyList before, ArmyList after, out IReadOnlyList<string> changed)
    {
        var flags = ArmyChange.None; HashSet<string> ids = [];
        if (!(before.MusterSelections ?? []).SequenceEqual(after.MusterSelections ?? [])) flags |= ArmyChange.Rules;
        if (before.Id != after.Id || before.Faction != after.Faction || before.Game != after.Game || before.Limit != after.Limit
            || before.Disposition != after.Disposition || !DetachmentRules.Selected(before).SequenceEqual(DetachmentRules.Selected(after))) flags |= ArmyChange.Rules;
        if (before.Name != after.Name) flags |= ArmyChange.Metadata;
        if (before.WarlordEntryId != after.WarlordEntryId) flags |= ArmyChange.Warlord;
        var old = before.Entries.ToDictionary(e => e.Id);
        foreach (var entry in after.Entries)
        {
            if (!old.Remove(entry.Id, out var previous)) { flags |= ArmyChange.Structure; ids.Add(entry.Id); continue; }
            var local = ArmyChange.None;
            if (previous.UnitKey != entry.UnitKey) local |= ArmyChange.Structure;
            if (previous.Models != entry.Models) local |= ArmyChange.Composition;
            if (previous.Loadout != entry.Loadout) local |= ArmyChange.Equipment;
            if (previous.AttachedToId != entry.AttachedToId) local |= ArmyChange.Attachments;
            if (previous.Enhancement != entry.Enhancement) local |= ArmyChange.Enhancement;
            if (!(previous.Choices ?? []).SequenceEqual(entry.Choices ?? [])) local |= ArmyChange.Rules;
            if (previous.Name != entry.Name || previous.Notes != entry.Notes) local |= ArmyChange.Metadata;
            if (local != ArmyChange.None) ids.Add(entry.Id);
            flags |= local;
        }
        if (old.Count > 0) { flags |= ArmyChange.Structure; ids.UnionWith(old.Keys); }
        changed = ids.ToArray(); return flags;
    }
}
