using GrimList.Core;

internal static class StatlineDisplayChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> test)
    {
        Dictionary<string, string> Stats(string wounds) => new() { ["M"] = "6\"", ["T"] = "4", ["Sv"] = "6+", ["W"] = wounds, ["LD"] = "7+", ["OC"] = "1" };
        test("Mixed-model units use the normal stat row followed by each different profile", () =>
        {
            var profile = new UnitProfile(Models: [new("Torment", Stats("3")), new("Mutant", Stats("1"))]);
            var lines = EffectiveDatasheetDisplay.Statlines(profile, profile);
            Check(lines.Count == 2 && lines[0].Name == "Torment" && lines[1].Name == "Mutant", "Mixed profile names/order lost");
            Check(lines[0].Effective["W"] == "3" && lines[1].Effective["W"] == "1", "Model wounds flattened");
            Check(lines.All(l => EffectiveDatasheetDisplay.Characteristics(l.Source, l.Effective).All(s => !s.Modified)), "Unchanged model highlighted");
            var effective = profile with { Models = [profile.Models![0], new("Mutant", new(Stats("1")) { ["T"] = "5" })] };
            lines = EffectiveDatasheetDisplay.Statlines(profile, effective);
            Check(EffectiveDatasheetDisplay.Characteristics(lines[1].Source, lines[1].Effective).Single(s => s.Key == "T").Modified, "Model change not highlighted");
            Check(profile.Models[1].Characteristics["T"] == "4", "Display mutated source");
        });
        test("Identical statlines collapse including duplicate primary rows; distinct saves still get a row", () =>
        {
            var profile = new UnitProfile(Characteristics: Stats("1"), Models: [new("Trooper", Stats("1")), new("Champion", Stats("1"))]);
            Check(EffectiveDatasheetDisplay.Statlines(profile, profile).Count == 1, "Identical profiles duplicated");
            var modelsOnly = profile with { Characteristics = null };
            Check(EffectiveDatasheetDisplay.Statlines(modelsOnly, modelsOnly).Count == 1, "Identical model-only profiles duplicated");
            var differentSave = profile with { Models = [profile.Models![0], new("Champion", new(Stats("1")) { ["InSv"] = "4+" })] };
            Check(EffectiveDatasheetDisplay.Statlines(differentSave, differentSave).Count == 2, "Invulnerable difference lost");
            var sharedSave = differentSave with { Characteristics = new() { ["InSv"] = "4+" } };
            Check(EffectiveDatasheetDisplay.Statlines(sharedSave, sharedSave).Count == 2, "Shared save introduced a blank primary statline");
            Check(EffectiveDatasheetDisplay.Statlines(null, null).Count == 0, "Missing profile invented stats");
            Check(EffectiveDatasheetDisplay.Statlines(null, new(Models: [new("Only model", Stats("1"))])).Count == 1, "Single model fallback missing");
        });
    }
}
