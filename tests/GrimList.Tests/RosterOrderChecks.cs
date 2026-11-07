using GrimList.Core;

internal static class RosterOrderChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("Roster order keeps categories, names and attachments, then puts larger same-name units first", () =>
        {
            var troop = new UnitDefinition("Fixture", "Troops", [new(5, 50), new(10, 100)], Category: "Battleline");
            var leader = new UnitDefinition("Fixture", "Leader", [new(1, 10)], Category: "Characters");
            var other = new UnitDefinition("Fixture", "Other", [new(20, 200)], Category: "Battleline");
            var data = new Dataset(new("Fixture", "1", "", ""), [troop, leader, other]);
            var army = ArmyList.New("Fixture") with { Entries = [
                new("small", troop.Key, "Troops", 5), new("large-a", troop.Key, "Troops", 10),
                new("large-b", troop.Key, "troops", 10), new("attached", leader.Key, "Leader", 1, AttachedToId: "small"),
                new("other", other.Key, "Zebra", 20), new("leader", leader.Key, "Leader", 1)] };
            var before = DataJson.Write(army);
            var ordered = RosterExport.OrderedEntries(army, data).Select(e => e.Id);
            if (!ordered.SequenceEqual(["leader", "large-a", "large-b", "small", "attached", "other"]))
                throw new Exception("Wrong category/name/size/attachment order: " + string.Join(", ", ordered));
            var resized = army with { Entries = army.Entries.Select(e => e.Id == "small" ? e with { Models = 20 } : e).ToList() };
            if (!RosterExport.OrderedEntries(resized, data).Select(e => e.Id).SequenceEqual(["leader", "small", "attached", "large-a", "large-b", "other"]))
                throw new Exception("Resizing failed to reorder unit with its leader");
            if (DataJson.Write(army) != before) throw new Exception("Display sorting mutated the saved roster");
        });
    }
}
