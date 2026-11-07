using GrimList.Application;
using GrimList.Core;
using GrimList.Platform;

internal static class InheritedLeaderChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private const string Faction = "Fixture faction";
    private static UnitDefinition Guard(string name, string? target = null, string leader = "Character unit from your army with the Leader ability") =>
        new(Faction, name, [new(5, 50)], Profile: new(Keywords: [name, "Infantry"], Abilities: target == null ? [] :
            [new("Attached Unit", $"If a {leader} can be attached to a {target} unit, it can be attached to this unit instead.")]));
    private static UnitDefinition Leader(string name = "Captain", params string[] keywords) => new(Faction, name, [new(1, 50)],
        Profile: new(Keywords: ["Character", "Leader", .. keywords], Abilities: [new("Leader", "This model can be attached to the following unit: Base Squad.")]));
    private static Dataset Data(params UnitDefinition[] units) => new(new("Fixture", "1", "", "", Edition: "11"), units.ToList());
    private static ArmyList Army(Dataset data) => ArmyList.New(Faction) with {
        Entries = data.Units.Where(u => u.Name != "Base Squad").Select(u => new ArmyEntry(u.Name, u.Key, u.Name, u.Options[0].Models)).ToList() };
    public static void Run(Action<string, Action> test, string root)
    {
        test("Inherited leaders work from both pickers without the referenced squad in the roster or library", () =>
        {
            var data = Data(Leader(), Guard("Special Squad", "Base Squad"));
            var army = Army(data);
            Check(LeaderRules.Problem(army, data, "Captain", "Special Squad") == null, "Prose inheritance rejected");
            using var store = WindowsServices.OpenStore(Path.Combine(root, "inherited-leaders"));
            store.Import(data); var session = new ArmySession(store); session.Open(army);
            Check(session.AttachmentCandidates("Captain").Any(e => e.Id == "Special Squad"), "Leader picker missing bodyguard");
            Check(session.AttachmentCandidates("Special Squad").Any(e => e.Id == "Captain"), "Bodyguard picker missing leader");
            session.Attach("Captain", "Special Squad");
            Check(LeaderRules.Issues(session.Army, session.Dataset).Count == 0, "Commit invalid");
            session.Undo(); Check(session.Army.Entries.Single(e => e.Id == "Captain").AttachedToId == "", "Undo lost");
            session.Redo(); var reopened = new ArmySession(store); reopened.Open(session.Army.Id);
            Check(LeaderRules.Issues(reopened.Army, reopened.Dataset).Count == 0, "Reopen invalid");
        });
        test("Inherited leader requirements retain named bearers, Epic Hero exclusions and actual bodyguard slots", () =>
        {
            var data = Data(Leader(), Leader("Priest", "Ministorum Priest"), Leader("Hero", "Epic Hero"),
                Guard("Special Squad", "Base Squad", "Character unit from your army with the Leader ability (excluding Epic Heroes)"),
                Guard("Priest Squad", "Base Squad", "Ministorum Priest or Inquisitor model from your army with the Leader ability"));
            var army = Army(data);
            Check(LeaderRules.Problem(army, data, "Captain", "Special Squad") == null, "Character rejected");
            Check(LeaderRules.Problem(army, data, "Hero", "Special Squad") != null, "Epic Hero exclusion lost");
            Check(LeaderRules.Problem(army, data, "Priest", "Priest Squad") == null, "Named leader rejected");
            Check(LeaderRules.Problem(army, data, "Captain", "Priest Squad") != null, "Named requirement lost");
            Check(LeaderRules.CombinationProblem(army, data, "Special Squad", ["Captain", "Priest"]) != null, "Inherited permission granted an extra slot");
        });
        test("Inherited structured permissions preserve owner enhancement conditions and never use stale prose", () =>
        {
            var leader = Leader() with { Profile = Leader().Profile! with { Semantics = new([], [], Attachments: [new("conditional", new("and", Children:
                [new("unit", "base-id"), new("owner", Children: [new("enhancement", "Unlock")])]))]) } };
            var data = Data(leader, Guard("Special Squad", "Base Squad"), Guard("Base Squad") with { Aliases = ["bsdata:base-id"] }) with {
                Detachments = [new(Faction, "Host")], ReferenceCards = [new(Faction, "Enhancements", "Unlock", "CHARACTER model only.", "Host", "10 pts")] };
            var army = Army(data) with { Detachment = "Host" };
            Check(LeaderRules.Problem(army, data, "Captain", "Special Squad") != null, "Structured denial fell back to prose");
            army = Enhancements.Select(army, data, "Captain", new("Host", "Unlock"));
            Check(LeaderRules.Problem(army, data, "Captain", "Special Squad") == null, "Owner's enhancement did not unlock referenced unit");
            army = LeaderRules.Attach(army, data, "Captain", "Special Squad");
            army = Enhancements.Select(army, data, "Captain", null);
            Check(LeaderRules.Issues(army, data).Count > 0, "Removed enhancement left inherited attachment legal");
        });
        test("Inherited chains are directional, cycle safe and refresh persisted permissions", () =>
        {
            var data = LeaderRules.ImportRules(Data(Leader(), Guard("Middle Squad", "Base Squad"), Guard("Special Squad", "Middle Squad")));
            Check(LeaderRules.Problem(Army(data), data, "Captain", "Special Squad") == null, "Valid chain rejected");
            var revised = data with { Units = data.Units.Select(u => u.Name == "Middle Squad" ? u with { Profile = u.Profile! with {
                Abilities = Guard("Middle Squad", "Special Squad").Profile!.Abilities } } : u).ToList() };
            Check(LeaderRules.Problem(Army(revised), revised, "Captain", "Special Squad") != null, "Cycle or stale permission accepted");
            var reloaded = DataJson.Read<Dataset>(DataJson.Write(data));
            Check(LeaderRules.Problem(Army(reloaded), reloaded, "Captain", "Special Squad") == null, "Roundtrip lost rule");
            var removed = data with { Units = data.Units.Select(u => u.Name == "Special Squad" ? u with { Profile = u.Profile! with { Abilities = [] } } : u).ToList() };
            Check(LeaderRules.Problem(Army(removed), removed, "Captain", "Special Squad") != null, "Removed ability retained inheritance");
        });
    }
}
