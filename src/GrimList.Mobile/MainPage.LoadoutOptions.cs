using GrimList.Application;
using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private sealed record LoadoutCounter(string Id, string Title, string Rule, string Description, int Count,
        LoadoutCountAction Less, LoadoutCountAction More, bool CanLess, bool CanMore);
    private sealed record LoadoutBlock(string Title, string Detail, List<LoadoutCounter> Counters,
        List<EquipmentSection> Choices, bool Pool = false);
    private sealed record LoadoutCard(string Id, string Title, string Summary, List<LoadoutBlock> Blocks);

    private static EquipmentSection PrepareEquipmentGroup(ArmySession session, UnitDefinition unit, ArmyEntry entry,
        UnitLoadout loadout, EquipmentGroup group, int model)
    {
        var selected = model < 0 ? loadout.Equipment : loadout.Models[model].Equipment;
        var minimum = group.Minimum.At(entry.Models, loadout.Models);
        var maximum = group.Maximum.At(entry.Models, loadout.Models);
        var exclusive = maximum == 1 && group.Options.Count > 1;
        return new(group.Name, exclusive, exclusive ? minimum > 0 ? "Select one" : "Select up to one; tap the selected choice to remove it"
            : "Set quantities. Standard equipment updates automatically.", group.Options.Select(option =>
        {
            var count = selected.Where(e => e.GroupId == group.Id && e.OptionId == option.Id).Sum(e => e.Count);
            var next = count > 0 && minimum == 0 ? 0 : 1;
            var limit = option.UnitMaximum is { } bound ? $"Maximum {bound.At(entry.Models, loadout.Models)} in this unit" : "";
            return new EquipmentChoice(group.Id, model, option.Id, WargearPricing.Label(unit, option), count,
                session.CanChooseEquipment(entry.Id, model, group.Id, option.Id, count - 1),
                session.CanChooseEquipment(entry.Id, model, group.Id, option.Id, count + 1), next,
                exclusive && session.CanChooseEquipment(entry.Id, model, group.Id, option.Id, next), limit);
        }).ToList());
    }

    // Mirrors Desktop SectionChoices presentation. All mutations and limit decisions remain shared Core/Application calls.
    private static List<LoadoutCard> PrepareLoadoutCards(ArmySession session, UnitDefinition unit, ArmyEntry entry, UnitLoadout loadout)
    {
        var definition = Loadouts.Definition(unit, loadout); var size = entry.Models;
        List<LoadoutCard> cards = [];
        foreach (var section in loadout.Manual ? [] : Loadouts.Sections(unit))
        {
            var roles = definition.Roles.Where(r => section.RoleIds.Contains(r.Id)).ToList();
            var models = loadout.Models.Where(m => section.RoleIds.Contains(m.RoleId)).ToList();
            List<LoadoutBlock> blocks = [];
            LoadoutCounter Counter(string title, int count, string rule, string role, string group = "", string option = "", string description = "")
            {
                var less = new LoadoutCountAction(section.Id, role, group, option, -1); var more = less with { Delta = 1 };
                return new(role + (group.Length > 0 ? "/" + group + "/" + option : ""), title, rule, description, count,
                    less, more, ArmySession.CanAdjust(unit, entry, less), ArmySession.CanAdjust(unit, entry, more));
            }
            if (roles.Count > 1)
            {
                var standard = roles.Single(r => r.Id == section.DefaultRoleId);
                List<string> Names(LoadoutRole role)
                {
                    var defaults = Loadouts.Defaults(role.Groups, size);
                    return role.Groups.SelectMany(g => g.Options.Where(o => defaults.Any(e => e.GroupId == g.Id && e.OptionId == o.Id))).Select(o => o.Name).ToList();
                }
                var standardNames = Names(standard);
                blocks.Add(new($"{Loadouts.RoleCount(loadout, standard.Id)} × {string.Join(" + ", standardNames)}", "Replace standard equipment with:", [], []));
                var pools = (definition.ModelGroups ?? []).Where(g => g.RoleIds.Count < section.RoleIds.Count
                    && g.RoleIds.All(section.RoleIds.Contains) && !g.RoleIds.Contains(standard.Id)).ToList();
                var groups = roles.Where(r => r.Id != standard.Id).GroupBy(role => pools
                    .Where(g => g.RoleIds.Contains(role.Id)).OrderBy(g => g.RoleIds.Count).FirstOrDefault()).OrderBy(g => g.Key == null ? 0 : 1);
                foreach (var group in groups)
                {
                    var pool = group.Key; var detail = "";
                    if (pool != null)
                    {
                        var count = models.Count(m => pool.RoleIds.Contains(m.RoleId));
                        var maximum = Math.Min(models.Count, pool.Maximum.At(size, loadout.Models));
                        var minimum = pool.Minimum.At(size, loadout.Models);
                        detail = $"{count} / {maximum} selected · Shared limit" + (minimum > 0 ? $" · Minimum {minimum}" : "");
                    }
                    List<LoadoutCounter> counters = [];
                    foreach (var role in group)
                    {
                        var names = Names(role); var additions = names.Except(standardNames).ToList();
                        var title = additions.Count > 0 ? string.Join(" + ", additions) : role.Name;
                        if (unit.ModelCosts?.FirstOrDefault(c => c.OptionId == role.Id)?.Points is > 0 and var points)
                            title += $" (+{points} pts per model)";
                        var maximum = Math.Min(models.Count, role.Maximum.At(size, loadout.Models));
                        var rule = $"Maximum {maximum} in this unit";
                        foreach (var other in pools.Where(g => g != pool && g.RoleIds.Contains(role.Id)))
                            rule += $" · {other.Name}: {other.Maximum.At(size, loadout.Models)} combined";
                        counters.Add(Counter(title, Loadouts.RoleCount(loadout, role.Id), rule, role.Id, description: string.Join(" + ", names)));
                    }
                    blocks.Add(new(pool?.Name ?? "", detail, counters, [], pool != null));
                }
            }
            foreach (var role in roles)
            {
                var indices = loadout.Models.Select((m, i) => (m, i)).Where(x => x.m.RoleId == role.Id).Select(x => x.i).ToList();
                if (indices.Count == 0) continue;
                foreach (var group in role.Groups.Where(g => roles.Count == 1 || g.Options.Count > 1 || g.Minimum.At(size, loadout.Models) != g.Maximum.At(size, loadout.Models)))
                {
                    if (role.Minimum.At(size, loadout.Models) == 1 && role.Maximum.At(size, loadout.Models) == 1)
                    {
                        blocks.Add(new("", "", [], [PrepareEquipmentGroup(session, unit, entry, loadout, group, indices[0])]));
                        continue;
                    }
                    var standardId = Loadouts.DefaultOption(group, size);
                    List<string> fixedChoices = []; List<LoadoutCounter> counters = [];
                    foreach (var option in group.Options)
                    {
                        var count = Loadouts.OptionCount(loadout, role.Id, group.Id, option.Id);
                        if (group.Maximum.At(size, loadout.Models) == 1 && option.Id == standardId || option.Minimum.At(size, loadout.Models) == option.Maximum.At(size, loadout.Models))
                        { fixedChoices.Add($"{count} × {WargearPricing.Label(unit, option)}"); continue; }
                        var maximum = Math.Min(indices.Count * option.Maximum.At(size, loadout.Models), option.UnitMaximum?.At(size, loadout.Models) ?? int.MaxValue);
                        counters.Add(Counter(WargearPricing.Label(unit, option), count, $"Maximum {maximum} in this unit", role.Id, group.Id, option.Id));
                    }
                    blocks.Add(new(group.Name, string.Join("\n", fixedChoices), counters, []));
                }
            }
            var selected = Loadouts.Equipment(unit, loadout with { Models = models, Equipment = [] }).GroupBy(e => e.Option.Name)
                .Select(g => $"{g.Sum(e => e.Count)} × {g.Key}");
            var name = System.Text.RegularExpressions.Regex.Replace(section.Name, @"^\d+\s*[-–]\s*\d+\s+", "");
            cards.Add(new(section.Id, roles.Count == 1 && roles[0].Maximum.At(size, loadout.Models) == 1 ? name : $"{models.Count} × {name}",
                string.Join(" · ", selected), blocks));
        }
        return cards;
    }

    private void RenderLoadoutCounter(VerticalStackLayout panel, ArmyEntry entry, LoadoutCounter counter)
    {
        var state = State(counterStates, counter.Id, counter);
        var rule = Ui.Text("", 13, colour: Ui.Muted); state.Bind(rule, Label.TextProperty, c => c.Rule); panel.Add(rule);
        var row = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 6 };
        var less = Action("−", () => Session.RunAsync(s => s.AdjustEquipment(entry.Id, state.Value.Less)), "loadout-minus-" + counter.Id, Accent, true);
        var more = Action("+", () => Session.RunAsync(s => s.AdjustEquipment(entry.Id, state.Value.More)), "loadout-plus-" + counter.Id, Accent, true);
        state.Bind(less, IsEnabledProperty, c => c.CanLess); state.Bind(more, IsEnabledProperty, c => c.CanMore);
        state.Bind(less, OpacityProperty, c => c.CanLess ? 1.0 : .35); state.Bind(more, OpacityProperty, c => c.CanMore ? 1.0 : .35);
        state.Bind(less, SemanticProperties.DescriptionProperty, c => "Remove " + c.Title); state.Bind(more, SemanticProperties.DescriptionProperty, c => "Add " + c.Title);
        state.Bind(more, SemanticProperties.HintProperty, c => c.CanMore ? c.Description : "Limit reached or no models with standard equipment remain.");
        var label = Ui.Text("", 15, true); state.Bind(label, Label.TextProperty, c => $"{c.Count} × {c.Title}");
        row.Add(less); row.Add(label, 1); row.Add(more, 2);
        panel.Add(Ui.Card(row, Ui.Input));
    }

    private void RenderEquipmentGroup(VerticalStackLayout choices, ArmyEntry entry, EquipmentSection group)
    {
        choices.Add(Ui.Text(group.Name, 16, true)); choices.Add(Ui.Text(group.Instruction, 13, colour: Ui.Muted));
        foreach (var option in group.Choices)
        {
            var state = State(choiceStates, ChoiceKey(option), option);
            if (option.Limit.Length > 0)
            { var limit = Ui.Text("", 13, colour: Ui.Muted); state.Bind(limit, Label.TextProperty, c => c.Limit); choices.Add(limit); }
            if (group.Exclusive)
            {
                var button = Action("", () => state.Value.Count == state.Value.NextCount ? Task.CompletedTask
                    : Session.RunAsync(s => s.ChooseEquipment(entry.Id, state.Value.Model, state.Value.GroupId, state.Value.OptionId, state.Value.NextCount)),
                    $"equipment-choice-{option.Model}-{option.GroupId}-{option.OptionId}", Ui.Input);
                state.Bind(button, Button.TextProperty, c => (c.Count > 0 ? "✓ " : "") + c.Name);
                state.Bind(button, BackgroundColorProperty, c => c.Count > 0 ? Ui.Dark(Accent) : Ui.Input);
                state.Bind(button, Button.BorderColorProperty, c => c.Count > 0 ? Colors.White : Ui.Line);
                state.Bind(button, Button.BorderWidthProperty, c => c.Count > 0 ? 2.0 : 1.0);
                state.Bind(button, IsEnabledProperty, c => c.CanSelect); state.Bind(button, OpacityProperty, c => c.CanSelect ? 1.0 : .35);
                state.Bind(button, SemanticProperties.DescriptionProperty, c => c.Name + (c.Count > 0 ? ", selected" : ", not selected") + (c.CanSelect ? "" : ", unavailable"));
                choices.Add(button); continue;
            }
            var row = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 6 };
            var less = Action("−", () => Session.RunAsync(s => s.ChooseEquipment(entry.Id, state.Value.Model, state.Value.GroupId, state.Value.OptionId, state.Value.Count - 1)), "equipment-minus-" + option.OptionId, Accent, true);
            var more = Action("+", () => Session.RunAsync(s => s.ChooseEquipment(entry.Id, state.Value.Model, state.Value.GroupId, state.Value.OptionId, state.Value.Count + 1)), "equipment-plus-" + option.OptionId, Accent, true);
            state.Bind(less, IsEnabledProperty, c => c.Less); state.Bind(more, IsEnabledProperty, c => c.More);
            state.Bind(less, OpacityProperty, c => c.Less ? 1.0 : .35); state.Bind(more, OpacityProperty, c => c.More ? 1.0 : .35);
            state.Bind(less, IsVisibleProperty, c => c.Less || c.More || c.Count == 0); state.Bind(more, IsVisibleProperty, c => c.Less || c.More || c.Count == 0);
            var label = Ui.Text("", 15, true); state.Bind(label, Label.TextProperty, c => $"{c.Count} × {c.Name}");
            row.Add(less); row.Add(label, 1); row.Add(more, 2); choices.Add(Ui.Card(row, Ui.Input));
        }
    }
}
