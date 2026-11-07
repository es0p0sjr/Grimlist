using GrimList.Application;
using GrimList.Core;
using System.Text.Json.Nodes;

static class WarlordRestrictionChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action, string reason) { try { action(); } catch (InvalidDataException e) { Check(e.Message.Contains(reason), e.Message); return; } throw new Exception("Forbidden Warlord accepted"); }
    public static void Run(Action<string, Action> test, string root)
    {
        var source = new SourceInfo("Generic Warlord rules", "1", "", "", Edition: "11");
        var unit = new UnitDefinition("Death Guard", "Generic character", [new(1, 50)], Profile: new(Keywords: ["Character", "Infantry"]));
        var entry = new ArmyEntry("bearer", unit.Key, unit.Name, 1);
        var army = ArmyList.New(unit.Faction) with { Entries = [entry] };
        var data = new Dataset(source, [unit]);
        test("Warlord prohibitions recognize generic wording and override mandatory Warlord", () =>
        {
            foreach (var text in new[] { "This model cannot be your WARLORD.", "This model cannot be selected as your Warlord.",
                "This model cannot be selected to be your Warlord.", "This unit may not be chosen as Warlord.",
                "**This model** cannot be your Warlord and cannot be given Enhancements.", "Other rule text.\nThis model must not be selected as your Warlord." })
            {
                var changed = unit with { Profile = unit.Profile! with { MustBeWarlord = true, Abilities = [new("Generic prohibition", text), new("Supreme Commander", "Mandatory")] } };
                var rules = data with { Units = [changed] };
                var before = DataJson.Write(rules);
                var state = WarlordRules.Evaluate(army, entry, rules);
                Check(state.CannotBeWarlord && !state.Eligible && !state.MustBeWarlord, text);
                Check(!WarlordRules.Required(changed) && !WarlordRules.Eligible(changed), "Legacy unit checks ignored prohibition");
                Check(WarlordRules.Candidates(army, rules).Count == 0, "Forbidden unit in picker");
                Check(WarlordRules.Normalize(army with { WarlordEntryId = entry.Id }, rules).WarlordEntryId == "", "Mandatory override won");
                Check(WarlordRules.Issues(army with { WarlordEntryId = entry.Id }, rules).Any(r => r.Contains("Generic prohibition")), "Missing validation source");
                Reject(() => WarlordRules.Select(army, rules, entry.Id), "Generic prohibition");
                Check(DataJson.Write(rules) == before, "Imported datasheet changed");
            }
        });
        test("Real C'tan and other prohibited characters are excluded by their imported ability text", () =>
        {
            var real = DataJson.Read<Dataset>(File.ReadAllText("tests/fixtures/libraries/GrimList-Library.json"));
            var targets = new[] { ("Necrons", "C'tan Shard of the Deceiver"), ("Necrons", "C'tan Shard of the Nightbringer"),
                ("Necrons", "C'tan Shard of the Void Dragon"), ("Necrons", "Transcendent C'tan"), ("Adeptus Mechanicus", "Sydonian Skatros"),
                ("Astra Militarum", "Nork Deddog"), ("Astra Militarum", "Ogryn Bodyguard"), ("Space Marines", "Lieutenant with Combi-weapon"),
                ("Chaos Daemons", "Exalted Flamer"), ("Tyranids", "Deathleaper"), ("Imperial Agents", "Callidus Assassin"), ("Space Wolves", "Murderfang") };
            foreach (var (faction, name) in targets)
            {
                var u = real.Units.Single(u => u.Faction == faction && u.Name == name);
                var e = new ArmyEntry("forbidden", u.Key, u.Name, 1);
                var roster = ArmyList.New(faction) with { Entries = [e], WarlordEntryId = e.Id };
                var state = WarlordRules.Evaluate(roster, e, real);
                Check(state.CannotBeWarlord && !state.Eligible, name);
                Check(WarlordRules.Candidates(roster, real).Count == 0, name + " leaked into picker");
                Check(WarlordRules.Issues(roster, real).Any(r => r.Contains("Cannot be your Warlord")), name + " missing reason");
            }
            var ctan = real.Units.Single(u => u.Faction == "Necrons" && u.Name == "Transcendent C'tan");
            var lord = real.Units.First(u => u.Faction == "Necrons" && u.Name == "Overlord");
            var mixed = ArmyList.New("Necrons") with { Entries = [new("ctan", ctan.Key, ctan.Name, 1), new("lord", lord.Key, lord.Name, 1)], WarlordEntryId = "lord" };
            Check(WarlordRules.Candidates(mixed, real).Select(e => e.Id).SequenceEqual(["lord"]), "Legal Overlord excluded");
            Check(WarlordRules.Issues(mixed, real).Count == 0, "Forbidden non-Warlord invalidated a legal army");
            File.WriteAllText(Path.Combine(root, "warlord-restriction-fixture.json"), DataJson.Write(real with { Units = [ctan, lord] }));
        });
        test("Army and selected detachment restrictions use effective targets and active reference scope", () =>
        {
            var cards = new List<ReferenceCard> { new(unit.Faction, "Army Rules", "Army restriction", "You cannot select a SCOUT model as your Warlord."),
                new(unit.Faction, "Detachment Rules", "Detachment restriction", "INFANTRY models from your army cannot be your Warlord.", "Restricted"),
                new("Orks", "Army Rules", "Other faction", "CHARACTER models cannot be your Warlord."),
                new(unit.Faction, "Stratagems", "Not a muster rule", "CHARACTER models cannot be your Warlord.") };
            var rules = data with { ReferenceCards = cards, Detachments = [new(unit.Faction, "Restricted"), new(unit.Faction, "Open")] };
            Check(WarlordRules.Evaluate(army, entry, rules).Eligible, "Unselected/other faction/stratagem leaked");
            Check(WarlordRules.Evaluate(army with { Detachment = "Restricted" }, entry, rules).Reasons.Any(r => r.Contains("Detachment restriction")), "Selected detachment ignored");
            var scout = unit with { Profile = unit.Profile! with { Semantics = new([], [new("scout", "Muster effect", "keyword", "", "add", "Scout")]) } };
            Check(WarlordRules.Evaluate(army, entry, rules with { Units = [scout] }).CannotBeWarlord, "Effective keyword ignored");
            Check(!unit.Profile!.Keywords!.Contains("Scout"), "Base keyword changed");
            var obsolete = cards[0] with { Details = new(Status: "Obsolete") };
            Check(WarlordRules.Evaluate(army, entry, rules with { Units = [scout], ReferenceCards = [obsolete] }).Eligible, "Obsolete rule applied");
        });
        test("Structured detachment and allied prohibitions override required Warlord and are scoped", () =>
        {
            var rules = data with { Detachments = [new(unit.Faction, "Restricted", WarlordRestrictions: [new("Command rule", "Infantry cannot command.", new("keyword", "Infantry"))])] };
            Check(WarlordRules.Evaluate(army, entry, rules).Eligible, "Inactive structured detachment applied");
            Check(WarlordRules.Evaluate(army with { Detachment = "Restricted" }, entry, rules).CannotBeWarlord, "Structured detachment ignored");
            var daemon = unit with { Faction = "Chaos Daemons", Profile = new(Keywords: ["Character", "Legiones Daemonica"], MustBeWarlord: true) };
            var ally = entry with { UnitKey = daemon.Key };
            var host = ArmyList.New("Chaos Space Marines") with { Entries = [ally] };
            var state = WarlordRules.Evaluate(host, ally, data with { Units = [daemon] });
            Check(state.CannotBeWarlord && !state.MustBeWarlord && state.Reasons.Any(r => r.Contains("Daemonic Pact")), "Mandatory ally accepted");
            var agent = unit with { Faction = "Imperial Agents", Profile = new(Keywords: ["Character", "Imperium", "Agent"]) };
            ally = entry with { UnitKey = agent.Key }; host = ArmyList.New("Space Marines") with { Entries = [ally] };
            var alliedData = data with { Units = [agent], ReferenceCards = [new("Imperial Agents", "Army Rules", "Assigned Agents",
                "AGENT models cannot be your Warlord.")] };
            Check(AlliedRules.CanBeWarlord(host, agent, alliedData), "Fixture must have an allied Warlord permission");
            Check(WarlordRules.Evaluate(host, ally, alliedData).CannotBeWarlord, "Origin-faction allied reference restriction ignored");
        });
        test("Structured reference restrictions survive persistence and participate in content identity", () =>
        {
            var card = new ReferenceCard(unit.Faction, "Army Rules", "Command", "Command restrictions.",
                WarlordRestrictions: [new("Command", "Infantry cannot command.", new("keyword", "Infantry"))]);
            Check(ReferenceModel.ContentHash(card) != ReferenceModel.ContentHash(card with { WarlordRestrictions = null }), "Restriction-only update invisible");
            var rules = DataJson.Read<Dataset>(DataJson.Write(data with { ReferenceCards = [card] }));
            Check(WarlordRules.Evaluate(army, entry, rules).CannotBeWarlord, "Structured reference lost");
            rules = data with { ReferenceCards = [card with { WarlordRestrictions = null, Details = new(Restrictions: "INFANTRY models cannot be your Warlord.") }] };
            Check(WarlordRules.Evaluate(army, entry, rules).CannotBeWarlord, "Canonical restriction text ignored");
        });
        test("Only active abilities and roster conditions prohibit Warlord; unresolved conditions require review", () =>
        {
            var restricted = unit with { Profile = unit.Profile! with { Abilities = [new("Conditional", "If this model has the SCOUT keyword, this model cannot be your Warlord.")],
                Semantics = new([new("role", "Role", 0, [new("scout", "Scout", ["Scout"], []), new("commander", "Commander", [], [])])], []) } };
            var rules = data with { Units = [restricted] };
            Check(WarlordRules.Evaluate(army, entry, rules).Eligible, "Inactive condition applied");
            var chosen = entry with { Choices = [new("role", "scout")] };
            Check(WarlordRules.Evaluate(army with { Entries = [chosen] }, chosen, rules).CannotBeWarlord, "Choice condition ignored");
            var grant = unit with { Profile = unit.Profile! with { Semantics = new([new("role", "Role", 0,
                [new("ban", "Restricted role", [], [new("Granted restriction", "This model cannot be selected as your Warlord.")])])], []) } };
            Check(WarlordRules.Evaluate(army, entry, data with { Units = [grant] }).Eligible, "Unselected ability applied");
            chosen = entry with { Choices = [new("role", "ban")] };
            Check(WarlordRules.Evaluate(army with { Entries = [chosen] }, chosen, data with { Units = [grant] }).CannotBeWarlord, "Granted ability ignored");
            var contextual = unit with { Profile = unit.Profile! with { Abilities = [new("Conditional", "This model cannot be your Warlord unless a condition is met.")] } };
            var state = WarlordRules.Evaluate(army, entry, data with { Units = [contextual] });
            Check(!state.Eligible && !state.CannotBeWarlord && state.Reasons.Any(r => r.Contains("needs review")), "Unknown condition asserted permanent");
            Check(WarlordRules.ParseRestrictions("Unrelated", "An enemy model cannot be selected as your Warlord. This model cannot target your Warlord.").Count == 0, "Unrelated text parsed");
        });
        test("Warlord selection, restriction edits, save/reopen and undo/redo preserve roster state", () =>
        {
            var restricted = unit with { Profile = unit.Profile! with { Semantics = new([new("role", "Role", 0,
                [new("ban", "Restricted", [], [new("Choice rule", "This model cannot be your Warlord.")])])], []) } };
            var rules = data with { Units = [restricted], Detachments = [new(unit.Faction, "Restricted", Cost: "1 DP", WarlordRestrictions: [new("Detachment", "Cannot be Warlord.")])] };
            var directory = Path.Combine(root, "warlord-restrictions");
            using (var store = WindowsServices.OpenStore(directory))
            {
                store.Import(rules); var session = new ArmySession(store); session.Open(army);
                session.SelectWarlord(entry.Id); Check(session.Army.WarlordEntryId == entry.Id, "Selection failed");
                session.Undo(); Check(session.Army.WarlordEntryId == "", "Selection undo failed");
                session.Redo(); Check(session.Army.WarlordEntryId == entry.Id, "Selection redo failed");
                session.SelectChoice(entry.Id, "role", "ban"); Check(session.Army.WarlordEntryId == "", "Choice kept forbidden Warlord");
                session.Undo(); Check(session.Army.WarlordEntryId == entry.Id && session.Army.Entries[0].Choices == null, "Undo did not restore full state");
                session.Redo(); Check(session.Army.WarlordEntryId == "" && WarlordRules.Candidates(session.Army, session.Dataset).Count == 0, "Redo left stale candidates");
                session.Undo(); session.AddDetachment("Restricted"); Check(session.Army.WarlordEntryId == "", "Detachment kept forbidden Warlord");
                session.Undo(); Check(session.Army.WarlordEntryId == entry.Id, "Detachment undo lost Warlord");
            }
            using (var store = WindowsServices.OpenStore(directory))
            {
                var session = new ArmySession(store); session.Open(store.LoadArmies().Single());
                Check(session.Army.WarlordEntryId == entry.Id, "Reopen lost legal Warlord");
                session.SelectChoice(entry.Id, "role", "ban");
            }
            using (var store = WindowsServices.OpenStore(directory))
            {
                var session = new ArmySession(store); session.Open(store.LoadArmies().Single());
                Check(session.Army.WarlordEntryId == "" && session.Army.Entries[0].Choices!.Single().OptionId == "ban", "Reopen lost restriction state");
                session.SelectChoice(entry.Id, "role", null); session.SelectWarlord(entry.Id);
                Check(session.Army.WarlordEntryId == entry.Id, "Removing restriction left stale eligibility");
            }
        });
        test("BSData zero Warlord maximum imports as a prohibition, including semantic conditional overrides", () =>
        {
            const string json = """{"catalogue":{"id":"dg","name":"Chaos - Death Guard","selectionEntries":[{"id":"unit","name":"Generic fixture","type":"model","costs":[{"name":"pts","typeId":"points","value":50}],"categoryLinks":[{"name":"Character"}],"profiles":[{"name":"Unit","typeName":"Unit","characteristics":[{"name":"T","$text":"5"}]}],"entryLinks":[{"name":"Warlord","targetId":"warlord","constraints":[{"id":"forbidden","field":"selections","scope":"parent","type":"max","value":0}]}]}],"sharedSelectionEntries":[{"id":"warlord","name":"Warlord","type":"upgrade"}],"categoryEntries":[{"id":"scout","name":"Scout"}]}}""";
            var doc = BsDataDocument.Parse("fixture.json", json);
            var imported = BsDataImporter.Import([doc], source).Dataset;
            var u = imported.Units.Single(); var e = entry with { UnitKey = u.Key }; var roster = army with { Entries = [e] };
            Check(u.Profile!.WarlordRestrictions?.Count == 1 && WarlordRules.Evaluate(roster, e, imported).CannotBeWarlord, "Max zero ignored");
            var link = doc.Root["selectionEntries"]![0]!["entryLinks"]![0]!;
            link["constraints"]![0]!["value"] = 1;
            link["modifiers"] = JsonNode.Parse("""[{"field":"forbidden","type":"set","value":0,"conditions":[{"type":"instanceOf","field":"selections","scope":"root-entry","childId":"scout"}]}]""");
            imported = BsDataImporter.Import([doc], source).Dataset;
            Check(WarlordRules.Evaluate(roster, e, imported).Eligible, "Inactive max override applied");
            u = imported.Units.Single(); imported = imported with { Units = [u with { Profile = u.Profile! with { Keywords = ["Character", "Scout"] } }] };
            Check(WarlordRules.Evaluate(roster, e, imported).CannotBeWarlord, "Conditional max zero ignored");
            Check(DataJson.Read<Dataset>(DataJson.Write(imported)).Units[0].Profile!.WarlordRestrictions?.Count == 1, "Restriction JSON roundtrip failed");
        });
    }
}
