#if PERFORMANCE_CHECKS
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private async Task RunWeaponMetadataChecks(string directory)
    {
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidDataException(name); checks.Add(name); }
        var data = Session.DatasetForArmy(ArmyList.New("Thousand Sons"))!;
        var unit = data.Units.Single(u => u.Name == "Daemon Prince of Tzeentch with wings");
        var army = ArmyList.New("Thousand Sons") with { Id = "weapon-metadata-native", Name = "Disposable metadata check", Detachment = "Grand Coven",
            Entries = [new("bearer", unit.Key, unit.Name, 1, Loadout: Loadouts.Create(unit, 1))] };
        await Session.RunAsync(s => { s.Open(army); s.SelectEntry("bearer"); });
        screen = Screen.Unit; previewKey = ""; unitTab = "datasheet"; playMode = false;
        await PrepareAsync(); RefreshPresentation(); await DisplayFrames();
        var builds = FullPageBuildCount;
        await Session.RunAsync(s => s.SelectEnhancement("bearer", new("Grand Coven", "Eldritch Vortex of E'taph")));
        await PrepareAsync(); RefreshPresentation(); await DisplayFrames();
        Check(FullPageBuildCount == builds, "Enhancement retains datasheet page");
        void Profiles(bool enhanced, string mode)
        {
            Check(unitView!.Weapons.Count > 0, mode + " has displayed weapons");
            foreach (var effective in unitView.Weapons)
            {
                var weapon = effective.Profile;
                var baseline = unit.Profile!.Weapons!.Single(w => w.SourceId == weapon.SourceId);
                var delta = enhanced && WeaponRules.Tokens(baseline.Keywords).Contains("Psychic") ? 1 : 0;
                Check(weapon.Strength == (int.Parse(baseline.Strength) + delta).ToString()
                    && weapon.Damage == (int.Parse(baseline.Damage) + delta).ToString(), mode + ": " + baseline.Name);
            }
        }
        Profiles(true, "Build");
        playMode = true; await PrepareAsync(); RefreshPresentation(); await DisplayFrames(); Profiles(true, "Play");
        playMode = false; await Session.RunAsync(s => s.Undo()); await PrepareAsync(); RefreshPresentation(); await DisplayFrames(); Profiles(false, "Undo");
        await Session.RunAsync(s => s.Redo()); await PrepareAsync(); RefreshPresentation(); await DisplayFrames(); Profiles(true, "Redo");
        await Session.RunAsync(s => { s.ReloadLibrary(); s.Open(army.Id); s.SelectEntry("bearer"); });
        await PrepareAsync(); RefreshPresentation(); await DisplayFrames(); Profiles(true, "Reopen");
        File.WriteAllText(Path.Combine(directory, "weapon-metadata-checks.json"), DataJson.Write(new { Checks = checks, Passed = checks.Count }));
    }
}
#endif
