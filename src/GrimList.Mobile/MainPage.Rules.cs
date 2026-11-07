using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private void RenderRules()
    {
        var body = new VerticalStackLayout { Spacing = 10, Padding = new Thickness(10, 4, 10, 18) };
        if (!playMode)
        {
            body.Add(Action("Maximum points: " + Session.Army.Limit + " \u00b7 Edit", EditArmyLimit, "edit-army-limit"));
            var history = new HorizontalStackLayout { Spacing = 8 };
            var undo = Action("Undo", () => Session.RunAsync(s => s.Undo()), "rules-undo"); undo.IsEnabled = Session.CanUndo;
            var redo = Action("Redo", () => Session.RunAsync(s => s.Redo()), "rules-redo"); redo.IsEnabled = Session.CanRedo;
            history.Add(undo); history.Add(redo); body.Add(history);
        }
        foreach (var rule in MusterEffects.Rules(Session.Army, Session.Dataset))
        {
            body.Add(Ui.Section("MUSTER NOMINATIONS · " + rule.Detachment, Ui.Dark(Accent)));
            body.Add(Ui.Text(MusterEffects.Summary(Session.Army, rule), 14, true));
            body.Add(Ui.Text("Selected units gain " + string.Join(", ", rule.Rule.GrantedKeywords) + ".", 13, colour: Ui.Muted));
            var selected = MusterEffects.Selected(Session.Army, rule);
            var candidates = MusterEffects.Candidates(Session.Army, Session.Dataset, rule);
            if (candidates.Count == 0) body.Add(Ui.Text("Add eligible units to nominate them here.", 14));
            foreach (var candidate in candidates)
            {
                var chosen = selected.Contains(candidate);
                var button = Action((chosen ? "✓ " : "") + MusterEffects.Label(Session.Army, candidate),
                    () => Session.RunAsync(s => s.SelectMusterNomination(candidate, !chosen)), "nominate-" + candidate.RuleId + "-" + candidate.EntryId + "-" + candidate.ModelIndex,
                    chosen ? Accent : Ui.Surface);
                button.IsEnabled = !playMode && (chosen || selected.Count < rule.Rule.Maximum); body.Add(button);
            }
        }
        body.Add(Ui.Section("ARMY RULES", Ui.Dark(Accent)));
        foreach (var card in references.Where(c => c.Section.Equals("Army Rules", StringComparison.OrdinalIgnoreCase) && !ArmyReferences.IsCore(c))) body.Add(Reference(card));
        body.Add(Ui.Section("DETACHMENTS", Ui.Dark(Accent)));
        if (DetachmentRules.UsesPoints(Session.Army.Faction, Session.Dataset))
            body.Add(Ui.Text(DetachmentRules.SelectionSummary(Session.Army, Session.Dataset), 14, colour: Ui.Muted));
        foreach (var name in DetachmentRules.Selected(Session.Army))
        {
            var details = new VerticalStackLayout { Spacing = 8 };
            foreach (var card in references.Where(c => c.Detachment == name && !IsStratagem(c) && !IsEnhancement(c))) details.Add(Reference(card));
            if (!playMode) details.Add(Action("Remove detachment", () => Session.RunAsync(s => s.RemoveDetachment(name)), "remove-detachment-" + name, Ui.Surface));
            body.Add(Ui.Fold(name, () => details, Accent, DetachmentRules.Find(Session.Army, name, Session.Dataset)?.Cost ?? ""));
        }
        if (!playMode)
        {
            var options = new VerticalStackLayout { Spacing = 10 };
            foreach (var option in DetachmentRules.Options(Session.Army, Session.Dataset).Where(d => !DetachmentRules.Contains(Session.Army, d.Name, Session.Dataset)))
            {
                var usesPoints = DetachmentRules.UsesPoints(Session.Army.Faction, Session.Dataset);
                var allowed = !usesPoints || DetachmentRules.CanAdd(Session.Army, option.Name, Session.Dataset);
                options.Add(Ui.Text(option.Name + " \u00b7 " + option.Cost, 16, true));
                options.Add(Ui.Text(DetachmentRules.SelectionMetadata(option, Session.Dataset), 13, colour: Ui.Muted));
                var add = Action(usesPoints ? "Add detachment" : "Select detachment", () => Session.RunAsync(s =>
                { if (usesPoints) s.AddDetachment(option.Name); else s.Commit(DetachmentRules.WithSelection(s.Army, [option.Name])); }), "add-detachment-" + option.Name);
                add.IsEnabled = allowed; add.Opacity = allowed ? 1 : .35; options.Add(add);
                if (!allowed) options.Add(Ui.Text(string.Join("\n", DetachmentRules.Issues(DetachmentRules.WithSelection(Session.Army, [.. DetachmentRules.Selected(Session.Army), option.Name]), Session.Dataset)), 13, colour: Ui.Muted));
            }
            body.Add(Ui.Fold("Choose detachments", () => options, Accent));
        }
        var dispositions = DetachmentRules.Dispositions(Session.Army, Session.Dataset);
        if (dispositions.Count > 0)
        {
            body.Add(Ui.Section("DISPOSITION", Ui.Dark(Accent)));
            foreach (var disposition in dispositions)
            {
                var choice = Action((Session.Army.Disposition == disposition ? "✓ " : "") + disposition, () => Session.RunAsync(s => s.SelectDisposition(disposition)), "disposition-" + disposition, Session.Army.Disposition == disposition ? Accent : Ui.Surface);
                choice.IsEnabled = !playMode; body.Add(choice);
            }
        }
        var primaries = DispositionPrimaries.ForArmy(Session.Army, Session.Dataset);
        body.Add(Ui.Section("PRIMARY MISSIONS", Ui.Dark(Accent)));
        if (primaries.Disposition.Length > 0) body.Add(Ui.Text(primaries.Disposition, 17, true));
        body.Add(Ui.Text(primaries.Hint, 13, colour: Ui.Muted));
        foreach (var matchup in primaries.Matchups)
        {
            var row = new VerticalStackLayout { Spacing = 5, AutomationId = "primary-matchup-" + matchup.OpponentDisposition };
            row.Add(Ui.Text("Against " + matchup.OpponentDisposition, 15, true));
            row.Add(Ui.Text("Your primary: " + matchup.YourPrimary, 16, true, Color.FromArgb("#F4D58D")));
            row.Add(Action("View primary card", () => ShowPrimaryMission(matchup), "view-primary-" + matchup.OpponentDisposition, Ui.Surface));
            row.Add(Ui.Text("Opponent primary: " + matchup.OpponentPrimary, 14, colour: Ui.Muted));
            body.Add(Ui.Card(row));
        }
        if (primaries.Matchups.Count > 0)
        {
            body.Add(Ui.Text(DispositionPrimaries.SourceLabel, 12, colour: Ui.Muted));
            body.Add(Action("Official mission source", async () => { await Microsoft.Maui.ApplicationModel.Launcher.Default.OpenAsync(DispositionPrimaries.SourceUrl); }, "primary-missions-source", Ui.Surface));
        }
        var enhancements = references.Where(IsEnhancement).ToList();
        if (enhancements.Count > 0) { body.Add(Ui.Section("ENHANCEMENTS", Ui.Dark(Accent))); foreach (var card in enhancements) body.Add(Reference(card)); }
        body.Add(Ui.Section("STRATAGEMS", Ui.Dark(Accent)));
        if (Session.Dataset is { } referenceData)
            foreach (var coverage in DatasetSnapshot.For(referenceData).References.Coverage.Factions
                .Where(f => UnitIdentity.Normalize(f.Faction) == UnitIdentity.Normalize(Session.Army.Faction)).SelectMany(f => f.Detachments)
                .Where(d => DetachmentRules.Contains(Session.Army, d.Detachment, Session.Dataset)))
                body.Add(Ui.Text($"{coverage.Detachment}: {coverage.StratagemCount} current cards · coverage {coverage.Coverage}. " + string.Join(" ", coverage.Missing), 13, colour: Ui.Muted));
        foreach (var detachment in ArmyReferences.MissingStratagemDetachments(Session.Army, Session.Dataset))
            body.Add(Ui.Text(detachment + ": no verified current stratagems available. Use Update data or import a current rules reference for this edition.", 14, colour: Ui.Muted));
        foreach (var card in references.Where(c => IsStratagem(c) && !ArmyReferences.IsCore(c))) body.Add(Reference(card));
        var previous = ArmyReferences.PreviousForArmy(Session.Army, Session.Dataset);
        var expandedPrevious = ExpandedReferences();
        if (previous.Count > 0)
            body.Add(Ui.Fold($"Previous references ({previous.Count}) — not verified for current rules", () => {
                var stored = new VerticalStackLayout { Spacing = 8 };
                stored.Add(Ui.Text("These stored cards may belong to an earlier codex. Use Update data to check for current replacements.", 14));
                foreach (var card in previous) stored.Add(Reference(card));
                return stored;
            }, Ui.Muted, expanded: expandedPrevious.Contains("previous-stratagems"), toggled: open => {
                if (open) expandedPrevious.Add("previous-stratagems"); else expandedPrevious.Remove("previous-stratagems");
            }));
        body.Add(Ui.Section("CORE STRATAGEMS", Ui.Dark(Accent)));
        foreach (var card in references.Where(c => IsStratagem(c) && ArmyReferences.IsCore(c))) body.Add(Reference(card));
        var other = references.Where(c => c.Section is not ("Army Rules" or "Detachments" or "Dispositions") && !IsStratagem(c) && !IsEnhancement(c)).ToList();
        if (other.Count > 0) { body.Add(Ui.Section("OTHER REFERENCES", Ui.Dark(Accent))); foreach (var card in other) body.Add(Reference(card)); }
        SetPage(Heading(Session.Army.Faction), Ui.Scroll(body));
    }
    private static bool IsStratagem(ReferenceCard card) => card.Section.Contains("Stratagem", StringComparison.OrdinalIgnoreCase);
    private static bool IsEnhancement(ReferenceCard card) => card.Section.Contains("Enhancement", StringComparison.OrdinalIgnoreCase) || card.IsUpgrade;
    private View Reference(ReferenceCard card)
    {
        var expanded = ExpandedReferences();
        var key = $"{card.Section}/{card.Detachment}/{card.Name}";
        if (card.Details?.Status == "Stale") key += "/previous/" + card.Details.Id;
        var turn = StratagemTiming.FromText(card.DisplayText);
        var colour = !IsStratagem(card) ? Accent : Color.FromArgb(turn switch
        { StratagemTurn.Either => "#276442", StratagemTurn.Yours => "#245C91", StratagemTurn.Opponents => "#913E43", _ => "#485154" });
        var timing = turn switch { StratagemTurn.Either => "Either turn", StratagemTurn.Yours => "Your turn", StratagemTurn.Opponents => "Opponent's turn", _ => "See timing below" };
        return Ui.Fold(card.Name + (card.Cost.Length > 0 ? "  ·  " + card.Cost : ""), () => Ui.Text(card.DisplayText, 15), colour,
            IsStratagem(card) ? (card.Detachment.Length > 0 ? card.Detachment + " · " : "") + timing : card.Detachment,
            expanded: expanded.Contains(key), toggled: open => { if (open) expanded.Add(key); else expanded.Remove(key); });
    }
}
