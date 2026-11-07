using GrimList.Application;
using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private sealed record EquipmentChoice(string GroupId, int Model, string OptionId, string Name, int Count, bool Less, bool More,
        int NextCount, bool CanSelect, string Limit);
    private sealed record EquipmentSection(string Name, bool Exclusive, string Instruction, List<EquipmentChoice> Choices);
    private sealed record UnitView(UnitDefinition Unit, ArmyEntry? Entry, List<PointOption> Sizes, bool CanBeWarlord,
        List<ReferenceCard> Enhancements, List<EquipmentSection> Equipment,
        List<LoadoutCard> LoadoutCards, List<EffectiveWeapon> Weapons, List<AbilityProfile> Abilities, string EquipmentSummary,
        string FeelNoPain, List<AttachedEffect> Effects, List<RuleDefinition> KeywordRules, EffectiveUnitState? EffectiveState = null, string WarlordReason = "",
        EnhancementPresentation? EnhancementSummary = null);
    private UnitView? PrepareUnit(ArmySession s)
    {
        var entry = previewKey.Length == 0 ? s.SelectedEntry : null;
        var effectiveState = entry == null ? null : EffectiveDatasheets.Project(s.Army, entry, s.Dataset);
        var unit = entry == null ? s.Dataset?.FindUnit(previewKey) : effectiveState?.Unit;
        if (unit == null) return null;
        if (entry == null) unit = EffectiveDatasheets.Preview(s.Army, s.Dataset, unit);
        var copy = entry == null ? ArmyCalculator.NextCopy(s.Army, s.Dataset, unit) : s.Result.Entries.Single(p => p.Entry.Id == entry.Id).Copy;
        List<EquipmentSection> equipment = []; List<LoadoutCard> loadoutCards = [];
        if (!playMode && entry != null && unitTab == "equipment")
        {
            var loadout = entry.Loadout ?? Loadouts.Create(unit, entry.Models);
            equipment = Loadouts.Definition(unit, loadout).UnitGroups.Select(g => PrepareEquipmentGroup(s, unit, entry, loadout, g, -1)).ToList();
            loadoutCards = PrepareLoadoutCards(s, unit, entry, loadout);
            // Equipment does not display weapons, keyword definitions, abilities,
            // enhancements or attachment candidates. Prepare those on the datasheet.
            return new(unit, entry, s.OptionsFor(unit.Key, copy), false, [], equipment, loadoutCards, [], [], "",
                DatasheetStats.FeelNoPainLabel(DatasheetStats.FeelNoPain(s.Army, s.Dataset, unit, entry)), [], [], effectiveState);
        }
        var all = unit.Profile?.Weapons ?? [];
        var weapons = playMode ? Loadouts.Weapons(unit, entry?.Loadout).Select(w => w.Profile with { Name = $"{w.Count} × {w.Profile.Name}" }).ToList() : all;
        var effects = entry == null ? [] : AttachedEffects.ForUnit(s.Army, s.Dataset, entry);
        var context = entry?.AttachedToId is { Length: > 0 } parent ? parent : entry?.Id;
        var effective = AttachedEffects.Weapons(weapons, effects.Where(e => e.Adjustment?.RequiresAfflicted == true), context != null && afflictedTargets.Contains(context));
        var abilityWeapons = all.Concat(effective.Select(w => w.Profile)).ToList();
        var abilities = (playMode ? Loadouts.Abilities(unit, entry?.Loadout) : unit.Profile?.Abilities ?? [])
            .Where(a => !WeaponRuleReference.IsWeaponDefinition(a, abilityWeapons)).ToList();
        var keywordRules = effective.Count == 0 ? [] : WeaponRuleReference.ForWeapon(effective[0].Profile with
        { Keywords = string.Join(", ", effective.SelectMany(w => WeaponRuleReference.Keywords(w.Profile.Keywords)).Distinct(StringComparer.OrdinalIgnoreCase)) }, unit, s.Dataset);
        var summary = entry?.Loadout == null ? "" : string.Join(", ", Loadouts.Equipment(unit, entry.Loadout)
            .GroupBy(e => e.Option.Name).Select(g => $"{g.Sum(e => e.Count)} × {g.Key}"));
        return new(unit, entry, playMode ? [] : s.OptionsFor(unit.Key, copy), entry != null
            && (!playMode || s.Army.WarlordEntryId == entry.Id) && WarlordRules.Candidates(s.Army, s.Dataset).Any(e => e.Id == entry.Id),
            entry == null || playMode ? [] : Enhancements.Options(s.Army, s.Dataset, unit), equipment, loadoutCards, effective,
            abilities, summary,
            DatasheetStats.FeelNoPainLabel(DatasheetStats.FeelNoPain(s.Army, s.Dataset, unit, entry)), effects, keywordRules, effectiveState,
            entry == null || playMode ? "" : WarlordRules.RestrictionReason(s.Army, entry, s.Dataset),
            entry == null || s.Dataset == null ? null : EnhancementPresentation.Prepare(s.Army, s.Dataset, entry.Id, false));
    }
    private void RenderUnit()
    {
        var body = new VerticalStackLayout { Spacing = 10, Padding = new Thickness(8, 4, 8, 16) };
        var view = unitView;
        if (view == null) { body.Add(Ui.Text("Select a unit from the army or library.")); SetPage(ArmyHeader(true), body); return; }
        var unit = view.Unit; var entry = view.Entry;
        var profile = new VerticalStackLayout { Spacing = 12 };
        profile.Add(Ui.Text(unit.Name.ToUpperInvariant(), 23, true));
        statlineBody = new() { Spacing = 12 }; profile.Add(statlineBody);
        UpdateUnitStatlines(view);
        {
            var fnp = Ui.Button("", async () => await ShowReference("Feel No Pain",
                DatasheetStats.FeelNoPain(Session.Army, Session.Dataset, unitView?.Unit ?? unit, previewKey.Length == 0 ? Session.SelectedEntry : null)), "feel-no-pain", Ui.Dark(Accent));
            feelNoPain.Bind(fnp, Button.TextProperty, text => text + " \u24d8"); feelNoPain.Bind(fnp, IsVisibleProperty, text => text.Length > 0);
            fnp.TextColor = KeywordColour("Feel No Pain"); profile.Add(fnp);
        }
        body.Add(Ui.Card(profile, Accent, 12));
        if (entry == null)
        {
            body.Add(Ui.Text("LIBRARY PREVIEW", 12, true, Ui.Muted));
            body.Add(Action("+ Add to army", () => AddUnit(new(new(unit, null), unit.Category, view.Sizes)), "add-preview"));
        }
        else
        {
            var priced = Session.Result.Entries.Single(p => p.Entry.Id == entry.Id);
            var priceLabel = Ui.Text("", 13, colour: Ui.Muted); entryPrice.Bind(priceLabel, Label.TextProperty, value => value); body.Add(priceLabel);
            if (!playMode) body.Add(UnitSizeStepper(entry.Id, entry.Name, unitSize));
            var tabs = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 5 };
            foreach (var (key, text, column) in new[] { ("datasheet", "Datasheet", 0), ("equipment", "Equipment", 1) })
                tabs.Add(Action(text, async () =>
                {
                    unitTab = key;
                    await Session.RunAsync(s => s.SelectEntry(entry.Id, s.Context.ModelIndex, key));
                }, "unit-tab-" + key, unitTab == key ? Accent : Ui.Surface), column);
            if (!playMode) body.Add(tabs);
        }
        if (!playMode && entry != null && unitTab == "equipment")
        { equipmentBody = new() { Spacing = 10 }; body.Add(equipmentBody); RenderEquipment(equipmentBody, view); }
        else { datasheetBody = new() { Spacing = 10 }; body.Add(datasheetBody); RenderDatasheet(datasheetBody, view); }
        SetPage(ArmyHeader(true), Ui.Scroll(body));
    }
    // Every distinct model statline uses the same cards, typography and highlighting
    // as the normal unit row, immediately below it in the datasheet header.
    private View RenderStatline(DatasheetStatline line, bool showName, int index, bool modelDefences)
    {
        var block = new VerticalStackLayout { Spacing = 12 };
        if (showName && line.Name.Length > 0) block.Add(Ui.Text(line.Name, 16, true));
        var stats = new Grid { ColumnSpacing = 5 };
        var column = 0;
        foreach (var stat in EffectiveDatasheetDisplay.Characteristics(line.Source, line.Effective))
        {
            stats.ColumnDefinitions.Add(new(GridLength.Star));
            var heading = Ui.Text(stat.Key.ToUpperInvariant(), 10, true); heading.HorizontalTextAlignment = TextAlignment.Center;
            var value = Ui.Text(stat.Value, 21, true); value.HorizontalTextAlignment = TextAlignment.Center;
            HighlightEffectiveValue(value, stat.Modified);
            value.AutomationId = (index == 0 ? "effective-stat-" : "effective-model-" + index + "-stat-") + stat.Key;
            stats.Add(new VerticalStackLayout { Spacing = 5, Children = { heading, Ui.Card(value, Ui.Input, 8) } }, column++);
        }
        if (column > 0) block.Add(stats);
        if (DatasheetStats.Invulnerable(line.Effective) is { Length: > 0 } invul)
        {
            var save = Ui.Text(invul + "  INVULNERABLE SAVE", 14, true);
            HighlightEffectiveValue(save, EffectiveDatasheetDisplay.Changed(DatasheetStats.Invulnerable(line.Source), invul));
            block.Add(Ui.Card(save, Ui.Dark(Accent), 8));
        }
        if (modelDefences && DatasheetStats.FeelNoPainLabel(DatasheetStats.FeelNoPain(new(Characteristics: line.Effective))) is { Length: > 0 } fnp)
            block.Add(Ui.Card(Ui.Text(fnp, 14, true), Ui.Dark(Accent), 8));
        return block;
    }
    private void RenderDatasheet(VerticalStackLayout body, UnitView view)
    {
        var entry = view.Entry;
        var baseProfile = view.EffectiveState?.BaseUnit.Profile ?? Session.Dataset?.FindUnit(view.Unit.Key)?.Profile;
        UpdateRegion(body, "roster-choices", Session.Mutation.Version, target =>
        {
            if (entry == null) return;
            foreach (var group in EffectiveDatasheets.Choices(Session.Army, entry, Session.Dataset))
            {
                target.Add(Ui.Section(group.Name.ToUpperInvariant(), Accent));
                var options = new[] { new DatasheetChoiceOption("", "Choose…", [], []) }.Concat(group.Options).ToList();
                var picker = Ui.Picker(group.Name, "datasheet-choice-" + group.Id);
                picker.ItemsSource = options.Select(o => o.Name).ToList();
                picker.SelectedIndex = options.FindIndex(o => o.Id == (entry.Choices?.FirstOrDefault(c => c.GroupId == group.Id)?.OptionId ?? ""));
                picker.IsEnabled = !playMode;
                var resetting = false;
                picker.SelectedIndexChanged += async (_, _) =>
                {
                    if (resetting || picker.SelectedIndex < 0) return;
                    await Execute(async () =>
                    {
                    var option = options[picker.SelectedIndex].Id;
                    var value = option.Length == 0 ? null : option;
                    var invalid = Session.InvalidEnhancements(Session.ProposeChoice(entry.Id, group.Id, value));
                    if (invalid.Count > 0 && !await DisplayAlertAsync("Change " + group.Name + "?", "This makes these enhancements unavailable:\n"
                        + string.Join("\n", invalid.Select(e => e.Name + " — " + e.Enhancement!.Name)) + "\n\nRemove them and apply the choice?", "Remove and change", "Cancel"))
                    { resetting = true; picker.SelectedIndex = options.FindIndex(o => o.Id == (entry.Choices?.FirstOrDefault(c => c.GroupId == group.Id)?.OptionId ?? "")); resetting = false; return; }
                    await Session.RunAsync(s => s.SelectChoice(entry.Id, group.Id, value, invalid.Count > 0));
                    });
                };
                target.Add(picker);
            }
        });
        UpdateDatasheetRegion(body, "afflicted", new { entry?.AttachedToId, view.Effects, Active = afflictedTargets.Order().ToArray() }, target =>
        {
            if (entry != null && view.Effects.Any(e => e.Adjustment?.RequiresAfflicted == true))
            {
                var context = entry.AttachedToId.Length > 0 ? entry.AttachedToId : entry.Id;
                target.Add(Toggle("Target is Afflicted", afflictedTargets.Contains(context), selected =>
                { if (selected) afflictedTargets.Add(context); else afflictedTargets.Remove(context); return Task.CompletedTask; }));
            }

        });
        UpdateDatasheetRegion(body, "warlord", new { view.CanBeWarlord, view.WarlordReason, Session.Army.WarlordEntryId }, target =>
        {
            if (entry != null && view.WarlordReason.Length > 0 && !playMode)
            {
                target.Add(Ui.Section("WARLORD", Accent));
                target.Add(Ui.Text(view.WarlordReason, 14, colour: Ui.Muted));
            }
            if (entry != null && view.CanBeWarlord && (!playMode || Session.Army.WarlordEntryId == entry.Id))
            {
                target.Add(Ui.Section("WARLORD", Accent));
                var warlord = Action(Session.Army.WarlordEntryId == entry.Id ? "✓ Warlord" : "Select as warlord", () => Session.RunAsync(s => s.SelectWarlord(entry.Id)), "select-warlord", Ui.Input);
                warlord.IsEnabled = !playMode; target.Add(warlord);
            }

        });
        UpdateRegion(body, "enhancements", Session.Mutation.Version, target =>
        {
            RenderEnhancementChoices(target, view);

        });
        UpdateRegion(body, "composition", view.EquipmentSummary, target =>
        {
            if (view.EquipmentSummary.Length > 0)
            { target.Add(Ui.Section("UNIT COMPOSITION & WARGEAR", Accent)); target.Add(Ui.Card(Ui.Text(view.EquipmentSummary, 14), Ui.Dark(Accent))); }

        });
        UpdateRegion(body, "weapons", (view.Weapons, Abilities: view.Unit.Profile?.Abilities, Base: baseProfile?.Weapons), target =>
        {
            foreach (var type in new[] { "Ranged", "Melee" })
            {
                var weapons = view.Weapons.Where(w => string.Equals(w.Profile.Range?.Trim(), "Melee", StringComparison.OrdinalIgnoreCase) == (type == "Melee")).ToList();
                if (weapons.Count == 0) continue;
                target.Add(Ui.Section(type.ToUpperInvariant() + " WEAPONS", Accent));
                foreach (var effective in weapons)
                {
                    var weapon = effective.Profile;
                    var block = new VerticalStackLayout { Spacing = 6 }; block.Add(Ui.Text(weapon.Name, 16, true));
                    var stats = new Grid { ColumnSpacing = 3 };
                    var names = new[] { "RANGE", "A", type == "Melee" ? "WS" : "BS", "S", "AP", "D" };
                    var values = new[] { weapon.Range, weapon.Attacks, weapon.Skill, weapon.Strength, weapon.Ap, weapon.Damage };
                    // Give dice expressions more room than the compact skill/S/AP values.
                    var widths = new[] { 1.25, 1.25, .85, .85, .8, 1.4 };
                    for (var n = 0; n < names.Length; n++)
                    {
                        stats.ColumnDefinitions.Add(new(new GridLength(widths[n], GridUnitType.Star)));
                        var value = Ui.Text(values[n], 15);
                        HighlightEffectiveValue(value, EffectiveDatasheetDisplay.WeaponChanged(baseProfile, weapon, names[n]));
                        value.AutomationId = "effective-weapon-" + EffectiveDatasheets.Key(weapon.Name) + "-" + names[n];
                        value.LineBreakMode = LineBreakMode.CharacterWrap;
                        stats.Add(new VerticalStackLayout { Children = { Ui.Text(names[n], 10, true, Ui.Muted), value } }, n);
                    }
                    block.Add(stats);
                    if (weapon.Keywords.Length > 0) block.Add(WeaponKeywordLinks(weapon, view.Unit));
                    var card = Ui.Card(block, Ui.Surface);
                    Ui.Tap(card, async () =>
                    {
                        var rules = WeaponRuleReference.ForWeapon(weapon, view.Unit, Session.Dataset);
                        await ShowReference(weapon.Name, rules);
                    }); target.Add(card);
                }
            }

        }, static (a, b) => PresentationEquality.Sequence(a.Weapons, b.Weapons, PresentationEquality.EffectiveWeapon)
            && PresentationEquality.Sequence(a.Abilities, b.Abilities) && PresentationEquality.Sequence(a.Base, b.Base, PresentationEquality.Weapon));
        UpdateRegion(body, "attachments", Session.Mutation.Version, target =>
        {
            if (entry != null) RenderAttachments(target, view);

        });
        UpdateRegion(body, "abilities", view.Abilities, target =>
        {
            target.Add(Ui.Section("ABILITIES", Accent));
            foreach (var ability in view.Abilities)
                target.Add(Ui.Fold(ability.Name, () => Ui.Text(ability.Text, 15), Ui.Surface, ability.Section));

        }, static (a, b) => PresentationEquality.Sequence(a, b));
        UpdateDatasheetRegion(body, "effects", view.Effects, target =>
        {
            if (view.Effects.Count > 0)
            {
                target.Add(Ui.Section("ATTACHED UNIT EFFECTS", Accent));
                foreach (var effect in view.Effects) target.Add(Ui.Fold(effect.Ability.Name, () => Ui.Text(effect.Ability.Text, 15), Ui.Surface, effect.Source));
            }

        });
        UpdateRegion(body, "keywords", view.KeywordRules, target =>
        {
            if (view.KeywordRules.Count > 0)
            {
                target.Add(Ui.Section("KEYWORDS · UNIVERSAL WEAPON RULES", Accent));
                foreach (var rule in view.KeywordRules)
                    {
                    var link = Ui.Button(rule.Name + " \u24d8", async () => await ShowReference(rule.Name, [rule]), "rule-" + rule.Name, Ui.Surface);
                    link.TextColor = KeywordColour(rule.Name); target.Add(link);
                }
            }

        }, static (a, b) => PresentationEquality.Sequence(a, b));
        UpdateDatasheetRegion(body, "static", new { view.Unit.Key, view.Unit.Profile?.Keywords, view.Unit.Profile?.Warnings }, target =>
        {
            if (view.Unit.Profile?.Keywords is { Count: > 0 } keywords)
            { target.Add(Ui.Section("UNIT KEYWORDS", Accent)); target.Add(Ui.Card(Ui.Text(string.Join(" · ", keywords), 12, colour: Ui.Muted))); }
            foreach (var warning in view.Unit.Profile?.Warnings ?? []) target.Add(Ui.Text(warning, 12, colour: Ui.Muted));
        });

    }
    private static void HighlightEffectiveValue(Label value, bool modified)
    {
        value.TextColor = modified ? Color.FromArgb(EffectiveDatasheetDisplay.ModifiedColour) : Colors.White;
        value.TextDecorations = modified ? TextDecorations.Underline : TextDecorations.None;
        if (modified) value.FontAttributes = FontAttributes.Bold;
        SemanticProperties.SetHint(value, modified ? "Modified by active rules" : "");
    }
    private void RenderEquipment(VerticalStackLayout body, UnitView view)
    {
        var entry = view.Entry!;
        sizeButtons.Clear();
        body.Add(Ui.Section("UNIT SIZE", Accent));
        foreach (var size in view.Sizes)
        {
            var resize = Action((size.Models == entry.Models ? "✓ " : "") + size.Label,
                () => Session.RunAsync(s => s.ResizeUnit(entry.Id, size.Models)), "resize-" + size.Models, size.Models == entry.Models ? Accent : Ui.Surface);
            resize.IsEnabled = size.Models != entry.Models; body.Add(resize);
            sizeButtons.Add((resize, size));
        }
        body.Add(Ui.Section("UNIT COMPOSITION AND WARGEAR OPTIONS", Accent));
        if (entry.Loadout?.Manual == true || view.Unit.Profile?.Loadout?.Incomplete == true)
            body.Add(Ui.Text(Loadouts.ReviewWarning, 12, colour: Ui.Muted));
        foreach (var group in view.Equipment)
        {
            var content = new VerticalStackLayout { Spacing = 10 }; RenderEquipmentGroup(content, view.Entry!, group); body.Add(content);
            equipmentGroups[EquipmentGroupKey(group)] = (EquipmentGroupShape(group), content);
        }
        foreach (var card in view.LoadoutCards)
        {
            var rendered = RenderLoadoutCard(entry, card); body.Add(rendered);
            equipmentCards[card.Id] = (CardShape(card), rendered);
        }
        body.Add(Action("Reset equipment", async () =>
        { if (await DisplayAlertAsync("Reset equipment?", "Restore imported default choices for this unit.", "Reset", "Cancel")) await Session.RunAsync(s => s.ResetLoadout(entry.Id)); }, "reset-equipment", Ui.Surface));
    }
    private readonly List<(Button Button, PointOption Size)> sizeButtons = [];
    private void UpdateEquipmentSizes(UnitView view)
    {
        foreach (var (button, size) in sizeButtons)
        {
            button.Text = (size.Models == view.Entry!.Models ? "✓ " : "") + size.Label;
            button.IsEnabled = size.Models != view.Entry.Models;
            button.BackgroundColor = size.Models == view.Entry.Models ? Accent : Ui.Surface;
        }
    }
    private View RenderLoadoutCard(ArmyEntry entry, LoadoutCard card)
    {
            EquipmentCardBuildCount++;
            var state = State(cardStates, card.Id, card);
            var panel = new VerticalStackLayout { Spacing = 12 };
            for (var blockIndex = 0; blockIndex < card.Blocks.Count; blockIndex++)
            {
                var index = blockIndex; var block = card.Blocks[index];
                var content = new VerticalStackLayout { Spacing = 8 };
                var title = Ui.Text(block.Title, 16, true); state.Bind(title, Label.TextProperty, c => c.Blocks[index].Title);
                state.Bind(title, IsVisibleProperty, c => c.Blocks[index].Title.Length > 0); content.Add(title);
                var detail = Ui.Text(block.Detail, 13, colour: Ui.Muted); state.Bind(detail, Label.TextProperty, c => c.Blocks[index].Detail);
                state.Bind(detail, IsVisibleProperty, c => c.Blocks[index].Detail.Length > 0); content.Add(detail);
                foreach (var counter in block.Counters) RenderLoadoutCounter(content, entry, counter);
                foreach (var choice in block.Choices) RenderEquipmentGroup(content, entry, choice);
                panel.Add(block.Pool ? Ui.Card(content, Ui.Dark(Accent)) : content);
            }
            var key = entry.Id + "/" + card.Id;
            return Ui.Fold(card.Title, () => panel, Accent, card.Summary, !collapsedWargear.Contains(key),
                expanded => { if (expanded) collapsedWargear.Remove(key); else collapsedWargear.Add(key); },
                (title, subtitle) => { state.Bind(title, Label.TextProperty, c => c.Title); state.Bind(subtitle, Label.TextProperty, c => c.Summary); });
    }
    private void RenderAttachments(VerticalStackLayout body, UnitView view)
    {
        var entry = view.Entry!;
        body.Add(Ui.Section("LEADERS & SUPPORT", Accent));
        if (entry.AttachedToId.Length > 0)
        {
            body.Add(Action("View bodyguard: " + LeaderRules.EntryLabel(Session.Army, entry.AttachedToId), () => OpenEntry(entry.AttachedToId), "view-attached-" + entry.AttachedToId, Ui.Dark(Accent)));
            if (!playMode) body.Add(Action("Detach from bodyguard", () => Session.RunAsync(s => s.Attach(entry.Id, "")), "detach-unit", Ui.Surface));
        }
        foreach (var attached in Session.Army.Entries.Where(e => e.AttachedToId == entry.Id))
        {
            body.Add(Action("View " + LeaderRules.RoleLabel(MusterEffects.EffectiveUnit(Session.Army, attached, Session.Dataset)) + ": " + attached.Name,
                () => OpenEntry(attached.Id), "view-attached-" + attached.Id, Ui.Dark(Accent)));
            if (!playMode) body.Add(Action("Detach " + attached.Name, () => Session.RunAsync(s => s.Attach(attached.Id, "")), "detach-" + attached.Id, Ui.Surface));
        }
        if (playMode) return;
        AddAttachmentChooser(body, view);
    }
}
