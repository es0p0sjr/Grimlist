using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrimList.Core;
using GrimList.Application;

namespace GrimList.Desktop;

public partial class MainWindow
{
    private record AttachmentChoice(string Id, string Name);
    private ArmyList? attachmentChoiceArmy;
    private Dataset? attachmentChoiceData;
    private readonly Dictionary<string, Task<List<ArmyEntry>>> attachmentChoices = [];
    private void InvalidateAttachmentChoices()
    {
        if (ReferenceEquals(army, attachmentChoiceArmy) && ReferenceEquals(data, attachmentChoiceData)) return;
        attachmentChoices.Clear(); attachmentChoiceArmy = army; attachmentChoiceData = data;
    }
    private Task<List<ArmyEntry>> AttachmentChoices(ArmyList snapshot, Dataset? library, string entryId)
    {
        // Every edit, undo/redo, open and import replaces one of these immutable
        // inputs. Context-only navigation can safely reuse the same choices.
        if (!ReferenceEquals(snapshot, attachmentChoiceArmy) || !ReferenceEquals(library, attachmentChoiceData))
        {
            attachmentChoices.Clear(); attachmentChoiceArmy = snapshot; attachmentChoiceData = library;
        }
        if (!attachmentChoices.TryGetValue(entryId, out var task) || task.IsFaulted || task.IsCanceled)
            attachmentChoices[entryId] = task = Task.Run(() => ArmySession.AttachmentCandidates(snapshot, library, entryId));
        return task;
    }
    private List<RosterRow> AttachmentRows(ArmyResult result)
    {
        var ordered = result.Entries.Where(e => e.Entry.AttachedToId.Length == 0 || army.Entries.All(b => b.Id != e.Entry.AttachedToId))
            .OrderBy(e => e.Entry, RosterOrder.ByNameThenSize)
            .SelectMany(e => new[] { e }.Concat(result.Entries.Where(l => l.Entry.AttachedToId == e.Entry.Id)
                .OrderBy(l => l.Entry, RosterOrder.ByNameThenSize))).ToList();
        // Keep even invalid imported relationships selectable so they can be detached.
        ordered.AddRange(result.Entries.Where(e => ordered.All(o => o.Entry.Id != e.Entry.Id)));
        return ordered.Select(e =>
        {
            var parent = army.Entries.FirstOrDefault(b => b.Id == e.Entry.AttachedToId);
            var root = parent?.Id ?? e.Entry.Id;
            var members = result.Entries.Where(r => r.Entry.Id == root || r.Entry.AttachedToId == root).ToList();
            var total = members.All(m => m.Points.HasValue) ? members.Sum(m => m.Points!.Value) + " pts" : "Unknown points";
            var group = new AttachmentGroup(root, members.Count > 1, $"ATTACHED UNIT · {total}");
            return new RosterRow(e.Entry.Id, (parent == null ? "" : "↳ ") + LeaderRules.EntryLabel(army, e.Entry.Id)
                + (e.Entry.Id == army.WarlordEntryId ? " · Warlord" : "")
                + (MusterEffects.GrantedKeywords(army, e.Entry, data) is { Count: > 0 } granted ? " · Muster: " + string.Join(", ", granted) : ""),
                RosterExport.Category(army, data, parent ?? e.Entry),
                $"{(e.Points.HasValue ? e.Points + " points" : "Unknown points")} · {e.Entry.Models} models", group);
        }).ToList();
    }

    private void RefreshAttachments(UnitDefinition? unit, ArmyEntry? entry)
    {
        InvalidateAttachmentChoices();
        AttachmentEditor.Children.Clear(); AttachmentSummary.Children.Clear();
        AttachmentEditor.Visibility = AttachmentSummary.Visibility = Visibility.Collapsed;
        if (entry == null) return;
        var parent = army.Entries.FirstOrDefault(e => e.Id == entry.AttachedToId);
        var leaders = army.Entries.Where(e => e.AttachedToId == entry.Id).ToList();
        void Link(ArmyEntry linked, string prefix)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = prefix + LeaderRules.EntryLabel(army, linked.Id), TextWrapping = TextWrapping.Wrap },
                HorizontalContentAlignment = HorizontalAlignment.Left, Background = (Brush)FindResource("AccentDark"),
                Margin = new(0, 0, 0, 5), Padding = new(9, 7, 9, 7), ToolTip = "View unit and its equipped weapons"
            };
            button.Click += (_, _) =>
            {
                // SelectionChanged refreshes the newly selected entry once.
                if (Roster.SelectedValue as string != linked.Id) Roster.SelectedValue = linked.Id;
            };
            AttachmentSummary.Children.Add(button);
        }
        if (parent != null) Link(parent, LeaderRules.IsSupport(unit) ? "Supporting: " : "Leading: ");
        foreach (var leader in leaders) Link(leader, LeaderRules.RoleLabel(MusterEffects.EffectiveUnit(army, leader, data)) + ": ");
        AttachmentSummary.Visibility = AttachmentSummary.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (playMode) return;
        var isLeader = LeaderRules.IsLeader(unit);
        var snapshot = army;
        var library = data;
        AttachmentEditor.Visibility = Visibility.Visible;
        AttachmentEditor.Children.Add(Label(isLeader ? "BODYGUARD UNIT" : "ATTACHED LEADERS / SUPPORT", true));
        if (isLeader || entry.AttachedToId.Length > 0)
        {
            var choices = new List<AttachmentChoice> { new("", "Unattached") };
            if (entry.AttachedToId.Length > 0)
                choices.Add(new(entry.AttachedToId, LeaderRules.EntryLabel(army, entry.AttachedToId)));
            AddPicker(choices, entry.AttachedToId, "Attach leader to unit", id => session.Attach(entry.Id, id), true);
            var rules = LeaderRules.RuleText(unit);
            if (rules.Length > 0) AttachmentEditor.Children.Add(new Expander
            {
                Header = LeaderRules.RoleLabel(unit) + " rules", Margin = new(0, 6, 0, 0), Content = new TextBlock { Text = rules, TextWrapping = TextWrapping.Wrap, Margin = new(8), FontSize = 12 }
            });
        }
        else
        {
            if (LeaderRules.BodyguardRuleText(unit) is { Length: > 0 } bodyguardRules)
                AttachmentEditor.Children.Add(new Expander { Header = "Attachment limits", Margin = new(0, 0, 0, 6),
                    Content = new TextBlock { Text = bodyguardRules, TextWrapping = TextWrapping.Wrap, Margin = new(8), FontSize = 12 } });
            foreach (var leader in leaders)
            {
                var detach = new Button { Content = "Detach " + LeaderRules.EntryLabel(army, leader.Id), Margin = new(0, 0, 0, 5) };
                detach.Click += (_, _) => { Run(() => Edit(() => session.Attach(leader.Id, ""))); };
                AttachmentEditor.Children.Add(detach);
            }
            AddPicker([new("", "Attach a Leader or Support…")], "", "Attach leader", id => session.Attach(id, entry.Id), false);
        }
        void AddPicker(List<AttachmentChoice> choices, string selected, string name, Action<string> change, bool choosingBodyguard)
        {
            var picker = new DeferredAttachmentPicker { Style = (Style)FindResource(typeof(ComboBox)),
                ItemsSource = choices, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = selected };
            var hint = Label(""); hint.Visibility = Visibility.Collapsed;
            var updating = false;
            var loaded = false;
            bool Current() => ReferenceEquals(army, snapshot) && ReferenceEquals(data, library)
                && detailEntryId == entry.Id && !playMode && AttachmentEditor.Children.Contains(picker);
            picker.LoadChoices = async () =>
            {
                if (loaded || !Current()) return;
                hint.Text = "Loading attachment choices…"; hint.Visibility = Visibility.Visible;
                try
                {
                    var candidates = await AttachmentChoices(snapshot, library, entry.Id);
                    if (!Current()) return;
                    var available = new List<AttachmentChoice> { choices[0] };
                    available.AddRange(candidates.Select(e => new AttachmentChoice(e.Id,
                        LeaderRules.EntryLabel(snapshot, e.Id) + (choosingBodyguard ? $" · {e.Models} models"
                            : " · " + LeaderRules.RoleLabel(MusterEffects.EffectiveUnit(snapshot, e, library))))));
                    if (selected.Length > 0 && available.All(c => c.Id != selected))
                        available.Add(new(selected, LeaderRules.EntryLabel(snapshot, selected) + " · needs review"));
                    updating = true;
                    try { picker.ItemsSource = available; picker.SelectedValue = selected; }
                    finally { updating = false; }
                    loaded = true;
                    hint.Text = choosingBodyguard ? "No eligible bodyguard available in this roster." : "No eligible Leader or Support available in this roster.";
                    hint.Visibility = candidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    ApplicationErrors.Record(ex);
                    if (Current()) { hint.Text = "Could not load attachments. Open the list to retry."; hint.Visibility = Visibility.Visible; }
                }
            };
            System.Windows.Automation.AutomationProperties.SetName(picker, name);
            picker.SelectionChanged += (_, _) =>
            {
                if (updating || rendering || renderingDetails || !Current() || picker.SelectedValue is not string id || id == selected) return;
                Run(() =>
                {
                    try { Edit(() => change(id)); }
                    catch { picker.SelectedValue = selected; throw; }
                });
            };
            AttachmentEditor.Children.Add(picker); AttachmentEditor.Children.Add(hint);
        }
    }
}

internal sealed class DeferredAttachmentPicker : ComboBox
{
    internal Func<Task> LoadChoices { get; set; } = () => Task.CompletedTask;
    internal Task Loading { get; private set; } = Task.CompletedTask;
    internal DeferredAttachmentPicker() => DropDownOpened += (_, _) =>
    {
        if (Loading.IsCompleted) Loading = LoadChoices();
    };
}
