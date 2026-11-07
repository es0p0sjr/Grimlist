using GrimList.Core;

internal static class AlliedChecks
{
    private static readonly SourceInfo Source = new("Synthetic ally fixtures", "1", "2026-10-02", "", Edition: "11");
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Illegal increase accepted"); }
    private static UnitDefinition Unit(string faction, string name, int points, params string[] keywords)
        => new(faction, name, [new(1, points)], Profile: new(Characteristics: new() { ["T"] = "4" }, Keywords: [.. keywords, "Faction: " + faction]));
    private static ArmyEntry Entry(UnitDefinition unit, int models = 1) => new(Guid.NewGuid().ToString(), unit.Key, unit.Name, models);
    private static Dataset Data(params UnitDefinition[] units) => new(Source, units.ToList());
    public static void Run(Action<string, Action> test, string root)
    {
        var lord = Unit("Death Guard", "Fixture Lord", 100, "Chaos", "Character");
        var rhino = Unit("Death Guard", "Fixture Rhino", 75, "Chaos", "Vehicle", "Transport", "Dedicated Transport") with { Category = "Vehicles" };
        var dog = Unit("Chaos Knights", "Fixture War Dog", 150, "Chaos", "Vehicle", "War Dog");
        var titan = Unit("Chaos Knights", "Fixture Titanic Knight", 400, "Chaos", "Vehicle", "Titanic", "Character");
        var nurgle = Unit("Plague Legions", "Fixture Plaguebearers", 100, "Chaos", "Daemon", "Nurgle", "Battleline") with { Faction = "Death Guard" };
        var great = Unit("Plague Legions", "Great Unclean One", 250, "Chaos", "Daemon", "Nurgle", "Character") with { Faction = "Death Guard" };
        var ork = Unit("Orks", "Fixture Warboss", 90, "Character");
        var data = Data(lord, rhino, dog, titan, nurgle, great, ork);
        var dg = ArmyList.New("Death Guard") with { Limit = 2000 };
        test("Transport and contextual ally categories work with existing imported data", () =>
        {
            Check(rhino.DisplayCategory == "Dedicated Transports", "Previously imported vehicle category hid transport keyword");
            Check(AlliedRules.Category(dg, dog) == "Allies" && dog.DisplayCategory == "Vehicles", "Allied categorization changed source data");
            Check(AlliedRules.Category(dg, nurgle) == "Allies" && AlliedRules.Category(dg, lord) == "Characters", "Embedded allied faction not recognized");
            Check(UnitCategories.Order("Battleline") < UnitCategories.Order("Dedicated Transports") && UnitCategories.Order("Dedicated Transports") < UnitCategories.Order("Vehicles")
                && UnitCategories.Order("Allies") > UnitCategories.Order("Others"), "Category order wrong");
            Check(UnitCategories.FromKeywords(["Vehicle", "Transport"]) == "Vehicles", "Ordinary transport incorrectly treated as Dedicated Transport");
        });
        test("Ally library unlocks by faction and detachment and never lists unrelated armies", () =>
        {
            var library = AlliedRules.Library(dg, data);
            Check(library.Contains(dog) && !library.Contains(ork) && !library.Contains(nurgle), "Ally eligibility ignored");
            var summoners = dg with { Detachments = ["Tallyband Summoners"] };
            Check(AlliedRules.Library(summoners, data).Contains(nurgle), "Summoners failed to unlock imported daemon");
            Check(UnitAvailability.Problem(dg, nurgle, data)!.Contains("Tallyband Summoners"), "Missing detachment not explained");
            Check(AlliedRules.Library(dg, data with { Source = Source with { Edition = "10" } }).All(u => u.Faction == dg.Faction), "11e allies leaked to 10e");
            Check(!AlliedRules.Library(dg, data with { Units = [dog with { ProfileSource = Source with { Edition = "10" } }] }).Any(), "Mixed edition ally accepted");
            var contextual = dog with { Faction = "Death Guard" };
            Check(AlliedRules.Library(dg, data with { Units = [dog, contextual] }).Single() == contextual, "Contextual and original catalogue duplicated ally");
            var variant = contextual with { Id = "fixture-variant", Options = [new(1, 160)] };
            Check(AlliedRules.Library(dg, data with { Units = [dog, contextual, variant] }).Count == 2, "Same-name source variants were hidden");
        });
        test("Dreadblades and Freeblades use an exclusive model allowance", () =>
        {
            var three = dg with { Entries = [Entry(dog), Entry(dog), Entry(dog)] };
            Check(!ArmyCalculator.Calculate(three, data).Issues.Any(i => i.StartsWith("Dreadblades:")), "Three War Dogs rejected");
            var four = three with { Entries = [.. three.Entries, Entry(dog)] };
            Reject(() => AlliedRules.ValidateIncrease(three, four, data));
            Reject(() => AlliedRules.ValidateIncrease(dg with { Entries = [Entry(titan)] }, dg with { Entries = [Entry(titan), Entry(dog)] }, data));
            var pack = dog with { Options = [new(3, 450), new(4, 600)] };
            Check(ArmyCalculator.Calculate(dg with { Entries = [Entry(pack, 4)] }, Data(pack)).Issues.Any(i => i.StartsWith("Dreadblades:")), "Models counted as units");
            var armiger = Unit("Imperial Knights", "Fixture Armiger", 150, "Imperium", "Armiger");
            Check(AlliedRules.Permission(ArmyList.New("Space Marines"), armiger, Data(armiger))?.Name == "Freeblades", "Imperial allies missing");
            Check(AlliedRules.Permission(dg, armiger, Data(armiger)) == null, "Imperial Knight allowed in Chaos army");
        });
        test("Assigned Agents have independent size-based budgets and separate transports", () =>
        {
            var character = Unit("Imperial Agents", "Fixture Inquisitor", 60, "Imperium", "Character");
            var retinue = Unit("Imperial Agents", "Fixture Retinue", 60, "Imperium", "Retinue");
            var requisitioned = Unit("Imperial Agents", "Fixture Requisitioned", 100, "Imperium", "Requisitioned");
            var transport = Unit("Imperial Agents", "Fixture Agents Rhino", 75, "Imperium", "Dedicated Transport", "Vehicle");
            var unknown = Unit("Imperial Agents", "Unclassified fixture", 100, "Imperium", "Vehicle");
            var agents = Data(character, retinue, requisitioned, transport, unknown);
            foreach (var size in new[] { 1000, 2000, 3000 })
            {
                var rank = size / 1000;
                var entries = Enumerable.Range(0, rank).SelectMany(_ => new[] { Entry(character), Entry(retinue) }).Concat(Enumerable.Range(0, rank == 3 ? 2 : 1).Select(_ => Entry(requisitioned))).Append(Entry(transport)).ToList();
                var army = ArmyList.New("Astra Militarum") with { Limit = size, Entries = entries };
                Check(!ArmyCalculator.Calculate(army, agents).Issues.Any(i => i.Contains("units ")), "Valid agent budgets rejected");
                Reject(() => AlliedRules.ValidateIncrease(army, army with { Entries = [.. entries, Entry(retinue)] }, agents));
                Check(AlliedRules.Category(army, transport) == "Allies", "Allied transport left Allies group");
                Check(!AlliedRules.Library(army, agents).Contains(unknown), "Agent without allowance exposed");
            }
        });
        test("Daemonic Pact validates points and Battleline balance separately for each god", () =>
        {
            var battleline = Unit("Chaos Daemons", "Fixture Nurgle Battleline", 100, "Chaos", "Legiones Daemonica", "Nurgle", "Battleline");
            var monster = Unit("Chaos Daemons", "Fixture Nurgle Monster", 300, "Chaos", "Legiones Daemonica", "Nurgle", "Monster");
            var otherGod = Unit("Chaos Daemons", "Fixture Khorne Monster", 100, "Chaos", "Legiones Daemonica", "Khorne", "Monster");
            var daemons = Data(battleline, monster, otherGod);
            var host = ArmyList.New("Chaos Space Marines") with { Limit = 2000, Entries = [Entry(battleline), Entry(monster)] };
            Check(!ArmyCalculator.Calculate(host, daemons).Issues.Any(i => i.StartsWith("Daemonic Pact:")), "Valid daemon pact rejected");
            Reject(() => AlliedRules.ValidateIncrease(host, host with { Entries = [.. host.Entries, Entry(otherGod)] }, daemons));
            var oversized = host with { Limit = 1000 };
            Check(ArmyCalculator.Calculate(oversized, daemons).Issues.Any(i => i.Contains("400/250")), "Daemon points cap ignored");
            var reduced = host with { Entries = [host.Entries[1]] };
            AlliedRules.ValidateIncrease(host, reduced, daemons);
            Check(ArmyCalculator.Calculate(reduced, daemons).Issues.Any(i => i.Contains("Battleline")), "Removing supporting unit did not flag invalid roster");
            Check(AlliedRules.Permission(dg, battleline, daemons) == null, "Generic Daemonic Pact allowed for Death Guard");
        });
        test("Daemon detachment limits count enhancements and allies cannot become warlords", () =>
        {
            var gift = new ReferenceCard("Death Guard", "Enhancements", "Fixture gift", "GREAT UNCLEAN ONE only. Fixture effect.", "Tallyband Summoners", "20 pts");
            var source = data with { ReferenceCards = [gift] };
            var army = dg with { Limit = 1000, Detachments = ["Tallyband Summoners"], Entries = [Entry(lord), Entry(great), Entry(great)] };
            Check(Enhancements.Eligible(army, great, gift, source), "Explicit daemon enhancement blocked");
            Check(!WarlordRules.Candidates(army, source).Any(e => e.UnitKey == great.Key), "Daemon ally can be warlord");
            var wrongGift = gift with { Text = "DEATH GUARD model only. Fixture effect." };
            Check(!Enhancements.Eligible(army, great, wrongGift, source), "Catalogue ownership substituted for faction keyword");
            var enhanced = army with { Entries = army.Entries.Select((e, i) => i == 1 ? e with { Enhancement = new(gift.Detachment, gift.Name) } : e).ToList() };
            Check(ArmyCalculator.Calculate(enhanced, source).Issues.Any(i => i.Contains("520/500")), "Enhancement cost omitted from allied allowance");
            Reject(() => AlliedRules.ValidateIncrease(army, enhanced, source));
            var knightGift = gift with { Text = "CHARACTER model only." };
            Check(!Enhancements.Eligible(army, titan, knightGift, source), "Knight ally received enhancement");
        });
        test("Detachment-specific ally routes enforce faction and keyword restrictions", () =>
        {
            var cases = new[]
            {
                ("World Eaters", "Khorne Daemonkin", Unit("Blood Legions", "Fixture Bloodletter", 100, "Chaos")),
                ("Thousand Sons", "Changehost of Deceit", Unit("Scintillating Legions", "Fixture Horror", 100, "Chaos")),
                ("Emperor's Children", "Carnival of Excess", Unit("Legions of Excess", "Fixture Daemonette", 100, "Chaos")),
                ("Genestealer Cults", "Brood Brother Auxilia", Unit("Astra Militarum", "Fixture Guard", 100, "Infantry", "Imperium")),
                ("Genestealer Cults", "Final Day", Unit("Tyranids", "Fixture Lictor", 100, "Vanguard Invader")),
                ("Chaos Knights", "Iconoclast Fiefdom", Unit("Chaos Space Marines", "Fixture Cultists", 100, "Chaos", "Damned")),
                ("Imperial Knights", "Questor Forgepact", Unit("Adeptus Mechanicus", "Skitarii Rangers", 100, "Imperium")),
                ("Chaos Daemons", "Shadow Legion", Unit("Chaos Space Marines", "Legionaries", 100, "Chaos", "Heretic Astartes"))
            };
            foreach (var (host, detachment, unit) in cases)
            {
                var army = ArmyList.New(host) with { Detachments = [detachment] };
                Check(AlliedRules.Permission(army, unit, Data(unit))?.Name == detachment, "Missing permission: " + detachment);
                Check(AlliedRules.Permission(army with { Detachments = [] }, unit, Data(unit)) == null, "Missing detachment bypass: " + detachment);
            }
            var commissar = Unit("Astra Militarum", "Fixture Commissar", 50, "Commissar");
            Check(AlliedRules.Permission(ArmyList.New("Genestealer Cults") with { Detachment = "Brood Brother Auxilia" }, commissar, Data(commissar)) == null, "Excluded Brood Brother allowed");
            var corsair = Unit("Aeldari", "Fixture Corsair", 100, "Anhrathe");
            var clown = Unit("Aeldari", "Fixture Harlequin", 100, "Harlequins");
            var drukhari = ArmyList.New("Drukhari");
            Check(AlliedRules.Permission(drukhari, corsair, Data(corsair))?.PointsLimit == 500, "Corsair allowance missing");
            Check(AlliedRules.Permission(drukhari with { Detachment = "Reaper's Wager" }, corsair, Data(corsair)) == null, "Reaper's Wager incorrectly stacked corsair rule");
            Check(AlliedRules.Permission(drukhari with { Detachment = "Reaper's Wager" }, clown, Data(clown))?.PointsLimit == 1000, "Wager allowance missing");
        });
        test("Allied identities, category, price and export survive persistence and reimport", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "ally-persistence"));
            store.Import(data);
            var savedData = store.LoadDataset()!;
            var savedDog = savedData.Units.Single(u => u.Name == dog.Name);
            var army = dg with { Entries = [Entry(savedDog)] };
            store.Save(army); var snapshot = DataJson.Write(store.LoadArmies());
            store.ApplyImport(store.PreviewImport(data, ImportLayer.Combined).Dataset);
            Check(DataJson.Write(store.LoadArmies()) == snapshot, "Ally import rewrote saved roster");
            Check(ArmyCalculator.Calculate(army, store.LoadDataset()).Total == 150, "Ally price missing");
            Check(ArmyCalculator.Export(army, store.LoadDataset()).Contains("Ally: Chaos Knights"), "Export omits ally origin");
            Check(AlliedRules.Category(army, store.LoadDataset()!.FindUnit(savedDog.Key)!) == "Allies", "Saved ally category lost");
        });
        test("Permitted allied leaders use explicit bodyguard rules and warlord restrictions", () =>
        {
            var agents = Unit("Imperial Agents", "Fixture Inquisitor", 60, "Imperium", "Character");
            agents = agents with { Profile = agents.Profile! with { Abilities = [new("Leader", "This model can be attached to the following units: Fixture Troopers.")] } };
            var assassin = Unit("Imperial Agents", "Fixture Assassin", 80, "Imperium", "Character", "Officio Assassinorum");
            var troops = Unit("Astra Militarum", "Fixture Troopers", 60, "Imperium", "Battleline");
            var source = Data(agents, assassin, troops);
            var leaderEntry = Entry(agents); var bodyguard = Entry(troops); var assassinEntry = Entry(assassin);
            var army = ArmyList.New("Astra Militarum") with { Entries = [leaderEntry, bodyguard, assassinEntry] };
            Check(LeaderRules.Problem(army, source, leaderEntry.Id, bodyguard.Id) == null, "Permitted allied leader rejected");
            Check(WarlordRules.Candidates(army, source).Any(e => e.Id == leaderEntry.Id) && !WarlordRules.Candidates(army, source).Any(e => e.Id == assassinEntry.Id), "Assigned Agent warlord restriction incorrect");
            Check(LeaderRules.Problem(army, source, assassinEntry.Id, bodyguard.Id) != null, "Ally gained an invented leader ability");
        });
        File.WriteAllText(Path.Combine(root, "allies-fixture.json"), DataJson.Write(data with { Detachments = [new("Death Guard", "Tallyband Summoners", Cost: "2 DP")] }));
    }
}
