#if PERFORMANCE_CHECKS
using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private async Task RunUnitSizeChecks(string directory)
    {
        List<string> checks = [];
        void Check(bool pass, string name)
        { if (!pass) throw new InvalidOperationException(name); checks.Add(name); }
        var data = Session.DatasetForArmy(ArmyList.New("Death Guard"))!;
        var unit = data.Units.Single(u => u.Faction == "Death Guard" && u.Name == "Plague Marines");
        var leader = data.Units.Single(u => u.Faction == "Death Guard" && u.Name == "Lord of Contagion");
        const string id = "size-check-marines", fixedId = "size-check-leader";
        var options = unit.Options.Where(o => o.FromCopy <= 1).Select(o => o.Models).Distinct().Order().ToArray();
        Check(options.Length > 1, "Fixture has multiple legal sizes");
        var roster = ArmyList.New("Death Guard") with { Id = "unit-size-checks", Name = "Unit size checks", Limit = 2000,
            Detachments = ["Virulent Vectorium"], Entries = [
                new(id, unit.Key, unit.Name, options[0], Loadout: Loadouts.Create(unit, options[0])),
                new(fixedId, leader.Key, leader.Name, 1, Loadout: Loadouts.Create(leader, 1))] };
        busy = true;
        await Session.RunAsync(s => { s.Open(roster); s.SelectEntry(id); });
        async Task Show(Screen next, string tab = "datasheet")
        { screen = next; unitTab = tab; previewKey = ""; await PrepareAsync(); RefreshPresentation(); await DisplayFrames(); }
        Button Button(string action) => this.GetVisualTreeDescendants().OfType<Button>()
            .Single(b => b.AutomationId == action);
        async Task Click(string action)
        {
            var button = Button(action);
            Check(button.IsEnabled && button.Handler?.PlatformView is Android.Widget.Button,
                action + $" has an enabled native button (enabled={button.IsEnabled}, undo={Session.CanUndo}, headerUndo={headerState.Value.Undo}, page={page.IsEnabled})");
            busy = false;
            ((Android.Widget.Button)button.Handler!.PlatformView!).PerformClick();
            for (var i = 0; busy && i < 600; i++) await Task.Delay(50);
            Check(!busy, action + " completed"); busy = true; await DisplayFrames();
        }
        void Size(int expected, string name)
        {
            var entry = Session.Army.Entries.Single(e => e.Id == id);
            Check(entry.Models == expected && entry.Loadout!.Models.Count == expected, name + " updates models and equipment");
            Check(this.GetVisualTreeDescendants().OfType<Label>().Single(l => l.AutomationId == "unit-size-count-" + id).Text == expected.ToString(),
                name + " updates visible size");
            Check(Session.LoadArmies().Single(a => a.Id == roster.Id).Entries.Single(e => e.Id == id).Models == expected, name + " persists");
        }
        await Show(Screen.Editor);
        for (var i = 0; i < 100 && !this.GetVisualTreeDescendants().OfType<Button>().Any(b => b.AutomationId == "unit-size-more-" + id); i++) await Task.Delay(50);
        Check(!Button("unit-size-less-" + id).IsEnabled, "Roster minimum disables minus");
        Check(!this.GetVisualTreeDescendants().OfType<Button>().Any(b => b.AutomationId == "unit-size-more-" + fixedId), "Fixed-size roster unit has no stepper");
        var oldPoints = Session.Result.Total;
        await Click("unit-size-more-" + id); Size(options[1], "Roster plus");
        Check(screen == Screen.Editor, "Roster plus does not open datasheet");
        Check(Session.Result.Total > oldPoints, "Growing unit updates army points");
        await Click("undo"); Size(options[0], "Undo");
        await Click("redo"); Size(options[1], "Redo");
        await Show(Screen.Unit);
        await Click("unit-size-less-" + id); Size(options[0], "Datasheet minus");
        Check(!Button("unit-size-less-" + id).IsEnabled, "Datasheet minimum disables minus after refresh");
        await Show(Screen.Unit, "equipment");
        for (var i = 1; i < options.Length; i++) { await Click("unit-size-more-" + id); Size(options[i], "Equipment plus " + i); }
        Check(!Button("unit-size-more-" + id).IsEnabled, "Equipment maximum disables plus");
        var version = Session.Mutation.Version;
        await StepUnitSize(id, true);
        Check(Session.Mutation.Version == version, "Stepping beyond maximum is a no-op");
        await Click("resize-" + options[0]); Size(options[0], "Existing size selector");
        Check(!Button("unit-size-less-" + id).IsEnabled && Button("unit-size-more-" + id).IsEnabled, "Existing selector refreshes stepper bounds");
        playMode = true; await Show(Screen.Unit);
        Check(!this.GetVisualTreeDescendants().OfType<Button>().Any(b => b.AutomationId?.StartsWith("unit-size-") == true), "Play mode hides size controls");
        await StepUnitSize(id, true);
        Check(Session.Army.Entries.Single(e => e.Id == id).Models == options[0], "Play mode cannot resize");
        playMode = false; await Show(Screen.Editor);
        File.WriteAllText(Path.Combine(directory, "unit-size-checks.json"), DataJson.Write(new { Checks = checks, Sizes = options }));
    }
}
#endif
