using GrimList.Core;

internal static class LeaderChecks
{
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid attachment accepted"); }
    public static void Run(Action<string, Action> test, string root)
    {
        var squad = new UnitDefinition("Death Guard", "Attachment squad", [new(5, 80)], Category: "Battleline");
        var leader = new UnitDefinition("Death Guard", "Attachment leader", [new(1, 50)], Category: "Characters",
            Profile: new(Keywords: ["Character", "Leader"], Abilities: [new("Leader", "This model can be attached to the following units:\n• Attachment squad. Other squad is mentioned only in an exception."),
                new("Leader", "Each bodyguard unit can only have one leader unit and one support unit attached to it.")]));
        var support = leader with { Name = "Attachment support", Profile = leader.Profile! with { Keywords = ["Character", "Support"] } };
        var other = squad with { Name = "Other squad" };
        var data = new Dataset(new("Attachment fixtures", "1", "", ""), [squad, leader, support, other]);
        var a = new ArmyEntry("leader", leader.Key, leader.Name, 1);
        var b = new ArmyEntry("squad", squad.Key, squad.Name, 5);
        var army = ArmyList.New("Death Guard") with { Entries = [a, b, b with { Id = "squad2" }, a with { Id = "leader2" }, new("support", support.Key, support.Name, 1), new("other", other.Key, other.Name, 5)] };
        test("Attachments use exact eligible roster entries and enforce shared slots", () =>
        {
            var attached = LeaderRules.Attach(army, data, a.Id, b.Id);
            Equal(b.Id, attached.Entries[0].AttachedToId);
            Equal("Attachment squad (unit 2)", LeaderRules.EntryLabel(army, "squad2"));
            Reject(() => LeaderRules.Attach(attached, data, "leader2", b.Id));
            Reject(() => LeaderRules.Attach(army, data, a.Id, a.Id));
            Reject(() => LeaderRules.Attach(army, data, a.Id, "other"));
            Reject(() => LeaderRules.Attach(army, data, a.Id, "missing"));
            Reject(() => LeaderRules.Attach(army with { Game = "Other" }, data, a.Id, b.Id));
            Reject(() => LeaderRules.Attach(army, data with { Units = [leader, squad with { Faction = "Other" }] }, a.Id, b.Id));
            var both = LeaderRules.Attach(attached, data, "support", b.Id);
            Equal(0, LeaderRules.Issues(both, data).Count);
            var supportFirst = LeaderRules.Attach(army, data, "support", b.Id);
            Equal(0, LeaderRules.Issues(LeaderRules.Attach(supportFirst, data, a.Id, b.Id), data).Count);
            Equal("squad2", LeaderRules.Attach(both, data, a.Id, "squad2").Entries[0].AttachedToId);
            Equal("", LeaderRules.Attach(attached, data, a.Id, "").Entries[0].AttachedToId);
            Equal(ArmyCalculator.Calculate(army, data).Total, ArmyCalculator.Calculate(both, data).Total);
            var chain = attached with { Entries = attached.Entries.Select(e => e.Id == b.Id ? e with { AttachedToId = a.Id } : e).ToList() };
            Equal(true, LeaderRules.Issues(chain, data).Count > 0);
        });
        test("Attachments persist, remap on army duplication, detach on deletion, and flag changed data", () =>
        {
            var attached = LeaderRules.Attach(army, data, a.Id, b.Id);
            Equal(b.Id, DataJson.Read<ArmyList>(DataJson.Write(attached)).Entries[0].AttachedToId);
            var copy = attached.Duplicate();
            Equal(copy.Entries[1].Id, copy.Entries[0].AttachedToId);
            Equal(false, copy.Entries[0].AttachedToId == b.Id);
            var exported = ArmyCalculator.Export(attached, data);
            if (!exported.Contains("Attached to: Attachment squad (unit 1)")) throw new Exception("Attachment missing from export: " + exported);
            var path = Path.Combine(root, "leaders");
            using (var store = WindowsServices.OpenStore(path)) { store.Import(data); store.Save(attached); }
            using (var store = WindowsServices.OpenStore(path))
            {
                var saved = store.LoadArmies().Single(); Equal(b.Id, saved.Entries[0].AttachedToId);
                var changed = data with { Units = [squad, leader with { Profile = new() }, support, other] };
                store.Import(changed);
                Equal(b.Id, store.LoadArmies().Single().Entries[0].AttachedToId);
                var issues = ArmyCalculator.Calculate(saved, store.LoadDataset()).Issues;
                if (!issues.Any(i => i.Contains("Leader"))) throw new Exception("Missing eligibility warning: " + string.Join("; ", issues));
                store.Save(saved with { Entries = saved.Entries.Where(e => e.Id != b.Id).ToList() });
                Equal("", store.LoadArmies().Single().Entries[0].AttachedToId);
            }
            Equal("", DataJson.Read<ArmyEntry>("""{"Id":"old","UnitKey":"old","Name":"Old","Models":1}""").AttachedToId);
        });
        File.WriteAllText(Path.Combine(root, "leader-fixture.json"), DataJson.Write(data));
    }
}
