using GrimList.Core;
using GrimList.Application;

internal static class DetachmentChecks
{
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root)
    {
        var data = new Dataset(new("Detachment fixture", "1", "", "", Edition: "11"), [new("Death Guard", "Test unit", [new(5, 100)])],
            Detachments: [new("Death Guard", "Small", "Reconnaissance", "1 DP"), new("Death Guard", "Medium", "Disruption", "2DP"),
                new("Death Guard", "Large", "Assault", "3 DP"), new("Death Guard", "Missing"), new("Orks", "Other", "Assault", "1 DP")]);
        var army = ArmyList.New("Death Guard") with { Detachment = "Small", Entries = [new("unit", data.Units[0].Key, "Test unit", 5)] };
        test("Incursion allows a single 3-DP detachment or combinations totaling at most 2 DP", () =>
        {
            var fixture = data with { Detachments = [.. data.Detachments!, new("Death Guard", "Peer", Cost: "1 DP"),
                new("Death Guard", "Free", Cost: "0 DP"), new("Death Guard", "Too large", Cost: "4 DP")] };
            var empty = DetachmentRules.WithSelection(army with { Limit = 1000 }, []);
            foreach (var names in new[] { new[] { "Large" }, ["Medium"], ["Small", "Peer"] })
            {
                var draft = empty;
                foreach (var name in names)
                {
                    Check(DetachmentRules.CanAdd(draft, name, fixture), "Valid addition rejected: " + name);
                    draft = DetachmentRules.WithSelection(draft, [.. DetachmentRules.Selected(draft), name]);
                }
                Check(DetachmentRules.Issues(draft, fixture).Count == 0, "Valid Incursion selection rejected");
                Check(ArmyCalculator.Calculate(draft, fixture).Total == 100, "Detachment allowance changed unit points");
            }
            foreach (var names in new[] { new[] { "Small", "Medium" }, ["Medium", "Small"], ["Large", "Small"],
                ["Small", "Large"], ["Large", "Free"], ["Free", "Large"], ["Too large"], ["Missing"], ["Other"] })
            {
                var prior = DetachmentRules.WithSelection(empty, names.SkipLast(1));
                Check(!DetachmentRules.CanAdd(prior, names.Last(), fixture), "Invalid addition allowed: " + string.Join(", ", names));
                Check(DetachmentRules.Issues(DetachmentRules.WithSelection(empty, names), fixture).Count > 0, "Invalid imported combination accepted");
            }
            var single = DetachmentRules.WithSelection(empty, ["Large"]);
            Check(!DetachmentRules.CanAdd(single, "Large", fixture), "Duplicate detachment allowed");
            Check(DetachmentRules.Issues(single with { Limit = 1500 }, fixture).Count > 0, "Unknown game size guessed");
            Check(!DetachmentRules.CanAdd(DetachmentRules.WithSelection(empty with { Limit = 2000 }, []), "Too large", fixture), "Strike Force allowance increased");
            Check(DetachmentRules.CanAdd(DetachmentRules.WithSelection(empty with { Limit = 3000 }, []), "Too large", fixture), "Onslaught budget changed");
            var export = ArmyCalculator.Export(single, fixture);
            Check(export.Contains("3 DP selected") && export.Contains("one detachment up to 3 DP") && !export.Contains("3/2 DP"), "Export misrepresents single-detachment allowance");
        });
        test("Incursion single-detachment allowance survives legacy storage, session edits, undo and redo", () =>
        {
            var path = Path.Combine(root, "incursion-detachment");
            var legacy = army with { Limit = 1000, Detachment = "Large", Detachments = null };
            using (var store = WindowsServices.OpenStore(path))
            {
                store.Import(data); store.Save(legacy);
                var session = new ArmySession(store); session.Open(legacy);
                Check(DetachmentRules.Issues(session.Army, session.Dataset).Count == 0, "Legacy single selection rejected");
                var before = DataJson.Write(session.Army);
                try { session.AddDetachment("Small"); throw new Exception("Session allowed a second detachment"); }
                catch (InvalidDataException) { }
                Check(DataJson.Write(session.Army) == before, "Rejected addition mutated the army");
                session.RemoveDetachment("Large"); session.AddDetachment("Medium");
                session.Undo(); session.Undo();
                Check(DetachmentRules.Selected(session.Army).SequenceEqual(["Large"]) && DetachmentRules.Issues(session.Army, data).Count == 0, "Undo failed to restore allowance");
                session.Redo(); session.Redo();
                Check(DetachmentRules.Selected(session.Army).SequenceEqual(["Medium"]), "Redo failed to restore alternative selection");
                session.RemoveDetachment("Medium"); session.AddDetachment("Large");
            }
            using var reopened = WindowsServices.OpenStore(path);
            var loaded = reopened.LoadArmies().Single(a => a.Id == legacy.Id);
            Check(DetachmentRules.Selected(loaded).SequenceEqual(["Large"]) && DetachmentRules.Issues(loaded, reopened.LoadDataset()).Count == 0, "Reopen lost the valid 3-DP selection");
        });
        test("Detachment budgets validate combinations, unknown costs and game sizes without changing unit points", () =>
        {
            Check(DetachmentRules.Budget(1000) == 2 && DetachmentRules.Budget(2000) == 3 && DetachmentRules.Budget(3000) == 4, "Incorrect DP budget");
            Check(DetachmentRules.Budget(1500) == null && DetachmentRules.Cost(new("", "", Cost: "2 CP")) == null, "Unknown sizes or costs were guessed");
            var combined = DetachmentRules.WithSelection(army, ["Small", "Medium"]);
            Check(DetachmentRules.Spent(combined, data) == 3 && DetachmentRules.Issues(combined, data).Count == 0, "Valid combination rejected");
            Check(ArmyCalculator.Calculate(combined, data).Total == 100, "DP must not be added to unit points");
            Check(DetachmentRules.Issues(combined with { Limit = 1000 }, data).Any(i => i.Contains("1 DP over")), "Lower game size failed to flag over-budget choices");
            Check(DetachmentRules.Issues(DetachmentRules.WithSelection(army with { Limit = 3000 }, ["Small", "Large"]), data).Count == 0, "4 DP combination rejected");
            Check(DetachmentRules.Issues(DetachmentRules.WithSelection(army, ["Missing"]), data).Any(i => i.Contains("unavailable")), "Missing DP treated as zero");
            Check(DetachmentRules.Issues(DetachmentRules.WithSelection(army, ["Other"]), data).Any(i => i.Contains("unavailable")), "Other faction allowed");
            Check(DetachmentRules.Issues(army with { Detachments = ["Small", "SMALL"] }, data).Any(i => i.Contains("once")), "Duplicate selection allowed");
            Check(DetachmentRules.Issues(DetachmentRules.WithSelection(army, []), data).Any(i => i.Contains("at least")), "Empty selection accepted");
        });
        test("Multiple detachments survive save, reopen, duplication and exports while legacy armies retain their selection", () =>
        {
            var path = Path.Combine(root, "detachments");
            var combined = DetachmentRules.WithSelection(army, ["Small", "Medium"]) with { Disposition = "Disruption" };
            using (var store = WindowsServices.OpenStore(path)) { store.Import(data); store.Save(combined); store.Save(army with { Id = "legacy", Entries = [] }); }
            using (var store = WindowsServices.OpenStore(path))
            {
                var loaded = store.LoadArmies().Single(a => a.Id == army.Id);
                Check(DetachmentRules.Selected(loaded).SequenceEqual(["Small", "Medium"]), "Selections lost on reload");
                Check(loaded.Disposition == "Disruption", "Disposition lost on reload");
                Check(DetachmentRules.Selected(store.LoadArmies().Single(a => a.Id == "legacy")).SequenceEqual(["Small"]), "Legacy selection lost");
                var duplicate = loaded.Duplicate(); store.Save(duplicate);
                Check(duplicate.Disposition == "Disruption" && ArmyCalculator.Export(duplicate, data).Contains("Disposition: Disruption"), "Disposition lost on duplication or export");
                Check(DetachmentRules.Spent(duplicate, data) == 3 && duplicate.Id != loaded.Id, "Duplication lost detachments");
                Check(ArmyCalculator.Export(loaded, data).Contains("Small, Medium") && ArmyCalculator.Export(loaded, data).Contains("3/3 DP"), "Export lost DP choices");
                Check(DetachmentRules.Selected(DataJson.Read<ArmyList>(DataJson.Write(loaded))).SequenceEqual(["Small", "Medium"]), "JSON lost detachments");
                var changed = DetachmentRules.WithSelection(duplicate, ["Large"]);
                Check(DetachmentRules.Selected(loaded).Count == 2 && DetachmentRules.Selected(changed).SequenceEqual(["Large"]), "Editing altered an earlier selection");
            }
        });
        test("Disposition choices follow selected detachments, preserve valid choices and clear unavailable ones", () =>
        {
            var one = DetachmentRules.NormalizeDisposition(army, data);
            Check(one.Disposition == "Reconnaissance", "A sole available disposition should be automatic");
            var two = DetachmentRules.NormalizeDisposition(DetachmentRules.WithSelection(one, ["Small", "Medium"]), data);
            Check(two.Disposition == "Reconnaissance" && DetachmentRules.Dispositions(two, data).SequenceEqual(["Disruption", "Reconnaissance"]), "Adding a detachment changed a valid choice");
            two = two with { Disposition = "Disruption" };
            var removed = DetachmentRules.NormalizeDisposition(DetachmentRules.WithSelection(two, ["Small"]), data);
            Check(removed.Disposition == "Reconnaissance", "Removed detachment still granted its disposition");
            Check(DetachmentRules.NormalizeDisposition(DetachmentRules.WithSelection(two, ["Small", "Large"]), data).Disposition == "", "Ambiguous replacement should request a choice");
            Check(DetachmentRules.NormalizeDisposition(DetachmentRules.WithSelection(two, []), data).Disposition == "", "Empty army retained a disposition");
            var shared = data with { Detachments = [.. data.Detachments!, new("Death Guard", "Shared", "Reconnaissance", "1 DP")] };
            Check(DetachmentRules.Dispositions(DetachmentRules.WithSelection(army, ["Small", "Shared"]), shared).Count == 1, "Shared dispositions should appear once");
        });
    }
}
