using System.ComponentModel;
using System.Text.RegularExpressions;
using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Application;

public record LibraryFilter(string Search = "", bool FitsRemaining = false, bool HideLegends = false, bool HideCrucible = false);
public record LibraryChoice(UnitDefinition Unit, string? Problem)
{
    public bool CanAdd => Problem == null && Unit.Options.Count > 0;
}
public record LoadoutCountAction(string Section, string Role, string Group, string Option, int Delta);

/// <summary>
/// Application-owned editing session shared by every frontend. Committed edits and recovery
/// context are saved before publishing state. Notifications occur on the caller's thread;
/// frontends dispatch notifications to their UI thread when using RunAsync.
/// </summary>
public sealed partial class ArmySession : INotifyPropertyChanged
{
    private readonly IArmyRepository repository;
    private readonly object sync = new();
    private readonly Stack<ArmyList> undo = new(), redo = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public Dataset? Dataset { get; private set; }
    public DatasetSnapshot? Snapshot { get; private set; }
    public ArmyMutation Mutation { get; private set; } = new(0, ArmyChange.None, []);
    private readonly Func<ArmyList, Dataset?, ArmyResult> calculate;
    private readonly bool useFactionLoading;
    private readonly Func<IArmyRepository, LoadedLibrary>? libraryLoader;
    private readonly string libraryVariant;
    private FactionLibrary? factionLibrary;
    public FactionLibrary? FactionLoading => factionLibrary;
    public ArmyList Army { get; private set; } = ArmyList.New("");
    public ArmyResult Result { get; private set; } = new([], 0, []);
    public EditorContext Context { get; private set; } = new();
    public bool IsOpen => Context.ArmyId.Length > 0;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public IReadOnlyList<string> NewIssues { get; private set; } = [];
    public ArmyEntry? SelectedEntry => Army.Entries.FirstOrDefault(e => e.Id == Context.EntryId);
    public IReadOnlyList<ArmyEntry> AttachedEntries => Army.Entries.Where(e => e.AttachedToId == Context.EntryId).ToList();
    public IReadOnlyList<string> Factions => factionLibrary?.Factions ?? Snapshot?.Factions ?? [];
    public int LibraryUnitCount => factionLibrary?.UnitCount ?? Dataset?.Units.Count ?? 0;

    public ArmySession(IArmyRepository repository, Func<ArmyList, Dataset?, ArmyResult>? calculate = null, LoadedLibrary? loadedLibrary = null,
        bool useFactionLoading = false, Func<IArmyRepository, LoadedLibrary>? libraryLoader = null, string libraryVariant = "")
    {
        this.repository = repository;
        this.calculate = calculate ?? ArmyCalculator.Calculate;
        this.useFactionLoading = useFactionLoading; this.libraryLoader = libraryLoader; this.libraryVariant = libraryVariant;
        LoadLibrary(loadedLibrary);
    }
    private void LoadLibrary(LoadedLibrary? loadedLibrary = null)
    {
        factionLibrary = useFactionLoading ? FactionLibrary.Open(repository, () => LoadCompleteLibrary(loadedLibrary), libraryVariant, factionLibrary) : null;
        var dataset = factionLibrary?.Header ?? LoadCompleteLibrary(loadedLibrary).Dataset;
        SetDataset(dataset); ReleasePreviousLibrary();
    }
    private LoadedLibrary LoadCompleteLibrary(LoadedLibrary? supplied = null)
    {
        var loaded = LocalPerformance.Measure("library.read-and-reference-check", () => supplied ?? libraryLoader?.Invoke(repository) ?? repository.LoadLibrary());
        loaded = LocalPerformance.Measure("library.reference-recovery", () => ReferenceLibraryRecovery.RestoreLibrary(repository, loaded).Library);
        if (loaded.Dataset != null) loaded = LocalPerformance.Measure("library.eligibility-recovery", () => EnhancementLibraryRepair.RestoreLibrary(repository, loaded));
        var dataset = loaded.Dataset;
        if (dataset != null) dataset = LocalPerformance.Measure("library.effective-recovery", () => EffectiveDatasheetRecovery.ApplyPersisted(loaded, repository.DirectoryPath));
        if (dataset != null) dataset = LocalPerformance.Measure("library.detachment-recovery", () => DetachmentMetadataRecovery.ApplyBundled(dataset));
        if (dataset != null) dataset = LocalPerformance.Measure("library.price-recovery", () => EquipmentPriceRecovery.ApplyBundled(dataset));
        return loaded with { Dataset = dataset };
    }
    private void SetDataset(Dataset? dataset)
    { var snapshot = dataset == null ? null : DatasetSnapshot.For(dataset); Dataset = dataset; Snapshot = snapshot; }
    private sealed class DatasetEdit(ArmySession session) : IDisposable
    {
        private readonly ArmyList previousArmy = session.Army;
        private readonly Dataset? previousDataset = session.Dataset;
        // An unsuccessful edit must not leave the old army paired with a new
        // faction view. Army is published only after validation and saving succeed.
        public void Dispose() { if (ReferenceEquals(session.Army, previousArmy)) session.SetDataset(previousDataset); }
    }
    public Dataset? DatasetForArmy(ArmyList army) { lock (sync) return factionLibrary?.ForArmy(army) ?? Dataset; }
    public Dataset? LoadFullDataset() { lock (sync) return factionLibrary == null ? Dataset : LoadCompleteLibrary().Dataset; }
    private ArmyResult Calculate(ArmyList army) => calculate(army, Dataset);
    private void Publish(ArmyChange changes, IReadOnlyList<string>? entries = null)
    { Mutation = new(Mutation.Version + 1, changes, entries ?? []); Notify(); }

    // Serializes mobile/background commands with any synchronous frontend operations.
    public Task RunAsync(Action<ArmySession> action, CancellationToken cancellation = default) => Task.Run(() =>
    {
        lock (sync) { cancellation.ThrowIfCancellationRequested(); action(this); }
    }, cancellation);

    public List<ArmyList> LoadArmies() { lock (sync) return repository.LoadArmies(); }
    public int ArmiesNeedingReview() { lock (sync) return repository.LoadArmies().Count(a => ArmyCalculator.Calculate(a, DatasetForArmy(a)).Issues.Count > 0); }

    public void Open(string armyId) => Open(LoadArmies().Single(a => a.Id == armyId));
    public void Open(ArmyList army)
    {
        lock (sync)
        {
            using var datasetEdit = new DatasetEdit(this);
            SetDataset(DatasetForArmy(army));
            var next = Normalize(WithDefaultLoadouts(Copy(army)));
            var context = new EditorContext(next.Id, next.Entries.FirstOrDefault()?.Id);
            var result = Calculate(next);
            repository.Save(next, context);
            Army = next; Context = context; Result = result;
            undo.Clear(); redo.Clear(); NewIssues = []; Publish(ArmyChange.Structure | ArmyChange.Rules | ArmyChange.Context);
        }
    }

    public void Create(string faction, string name, int limit, IEnumerable<string>? detachments = null, string disposition = "")
    {
        ValidateFields(name, limit);
        var draft = DetachmentRules.WithSelection(ArmyList.New(faction) with { Name = name.Trim(), Limit = limit, Disposition = disposition,
            Game = Dataset?.Game ?? "Warhammer 40,000" }, detachments ?? []);
        if (DetachmentRules.CombinationIssues(draft, DatasetForArmy(draft)) is { Count: > 0 } conflicts) throw new InvalidDataException(string.Join("\n", conflicts));
        Open(draft);
    }

    public bool Restore()
    {
        lock (sync)
        {
            var context = repository.LoadContext();
            var saved = repository.LoadArmies().FirstOrDefault(a => a.Id == context.ArmyId);
            if (saved == null) { Context = new(); repository.SaveContext(Context); Notify(); return false; }
            Open(saved);
            SetContext(context);
            return true;
        }
    }

    public void SelectEntry(string? id, int? modelIndex = null, string? section = null)
    {
        lock (sync) { if (IsOpen) SetContext(Context with { EntryId = id, ModelIndex = modelIndex, LoadoutSection = section }); }
    }
    public void Navigate(string screen)
    {
        lock (sync) SetContext(Context with { Screen = screen });
    }
    private void SetContext(EditorContext context)
    {
        context = ValidContext(Army, context);
        if (context == Context) return;
        repository.SaveContext(context); Context = context; Publish(ArmyChange.Context);
    }
    private static EditorContext ValidContext(ArmyList army, EditorContext context)
    {
        var entry = army.Entries.FirstOrDefault(e => e.Id == context.EntryId);
        return context with { ArmyId = army.Id, EntryId = entry?.Id,
            ModelIndex = entry != null && context.ModelIndex >= 0 && context.ModelIndex < entry.Models ? context.ModelIndex : null,
            LoadoutSection = entry == null ? null : context.LoadoutSection };
    }

    public void ReloadLibrary()
    {
        lock (sync)
        {
            LoadLibrary();
            if (IsOpen && repository.LoadArmies().FirstOrDefault(a => a.Id == Army.Id) is { } saved)
            {
                var context = Context; Open(saved); SetContext(context);
            }
            else { undo.Clear(); redo.Clear(); Result = Calculate(Army); }
            Publish(ArmyChange.Source | ArmyChange.Rules);
        }
    }

    public void Save()
    {
        lock (sync) { if (IsOpen) repository.Save(Army, Context); }
    }
    public void SetMetadata(string name, int limit)
    {
        lock (sync)
        {
            ValidateFields(name, limit);
            if (Army.Name == name.Trim() && Army.Limit == limit) { Save(); NewIssues = []; return; }
            Commit(Army with { Name = name.Trim(), Limit = limit });
        }
    }
    private static void ValidateFields(string name, int limit)
    {
        if (string.IsNullOrWhiteSpace(name) || limit is <= 0 or > 1000000)
            throw new InvalidDataException("Enter an army name and a points limit from 1 to 1,000,000.");
    }

    public void Commit(ArmyList proposed, string? selectEntry = null)
    {
        lock (sync)
        {
            if (proposed.Id != Army.Id) throw new InvalidOperationException("Open the army before editing it.");
            using var datasetEdit = new DatasetEdit(this);
            SetDataset(DatasetForArmy(proposed with { Entries = [.. Army.Entries, .. proposed.Entries] }));
            var next = Copy(proposed);
            ValidateFields(next.Name, next.Limit);
            if (!DetachmentRules.Selected(next).Order().SequenceEqual(DetachmentRules.Selected(Army).Order())
                && DetachmentRules.CombinationIssues(next, Dataset) is { Count: > 0 } conflicts)
                throw new InvalidDataException(string.Join("\n", conflicts));
            MusterRules.ValidateIncrease(Army, next, Dataset);
            MusterEffects.ValidateIncrease(Army, next, Dataset);
            Enhancements.ValidateIncrease(Army, next, Dataset);
            next = Normalize(next);
            var choicesChanged = next.Entries.Any(e => !(e.Choices ?? []).SequenceEqual(Army.Entries.FirstOrDefault(b => b.Id == e.Id)?.Choices ?? []));
            var invalidSelections = InvalidEnhancements(next).Where(e => choicesChanged || Army.Entries.FirstOrDefault(b => b.Id == e.Id)?.Enhancement != e.Enhancement).ToList();
            if (invalidSelections.Count > 0) throw new InvalidDataException("Unavailable enhancement in the proposed roster: " + string.Join(", ", invalidSelections.Select(e => e.Enhancement!.Name)) + ". Remove or change it first.");
            var result = Calculate(next);
            // A paid equipment change can increase an ally allowance even without
            // changing allied structure. Reuse both results; never skip that guard.
            AlliedRules.ValidateIncrease(Army, next, Dataset, Result, result);
            var context = ValidContext(next, Context with { EntryId = selectEntry ?? Context.EntryId });
            if (Context.EntryId != null && context.EntryId == null) context = context with { EntryId = next.Entries.FirstOrDefault()?.Id };
            repository.Save(next, context); // Failure leaves state and history unchanged.
            undo.Push(Copy(Army)); redo.Clear();
            NewIssues = result.Issues.Except(Result.Issues).ToList();
            var changes = ArmyChanges.Between(Army, proposed, out var entries);
            Army = next; Context = context; Result = result; Publish(changes, entries);
        }
    }

    public void AddUnit(string key, int models)
    {
        lock (sync)
        {
            var unit = Unit(key);
            if (UnitAvailability.Problem(Army, unit, Dataset) is { } problem) throw new InvalidDataException(problem);
            if (!OptionsFor(key, ArmyCalculator.NextCopy(Army, Dataset, unit)).Any(o => o.Models == models))
                throw new InvalidDataException("This unit size has no available price.");
            var entry = new ArmyEntry(Guid.NewGuid().ToString(), unit.Key, unit.Name, models,
                Loadout: unit.Profile?.Loadout == null ? null : Loadouts.Create(unit, models));
            Commit(Army with { Entries = [.. Army.Entries, entry] }, entry.Id);
        }
    }
    public void RemoveUnit(string id) { lock (sync) Commit(Army with { Entries = Army.Entries.Where(e => e.Id != id).ToList() }); }
    public void DuplicateUnit(string id)
    {
        lock (sync)
        {
            var entry = Entry(id) with { Id = Guid.NewGuid().ToString(), AttachedToId = "" };
            // Nominations belong to a roster identity. A new copy must be nominated separately.
            if (entry.Enhancement is { } choice && Enhancements.Find(Army, Dataset, choice) is { } card
                && !Enhancements.Eligible(Army, MusterEffects.EffectiveUnit(Army, entry, Dataset), card, Dataset))
                entry = entry with { Enhancement = null };
            if (Dataset?.FindUnit(entry.UnitKey) is { } unit && UnitAvailability.Problem(Army, unit, Dataset) is { } problem)
                throw new InvalidDataException(problem);
            Commit(Army with { Entries = [.. Army.Entries, entry] }, entry.Id);
        }
    }
    public ArmyList DuplicateArmy(ArmyList army)
    {
        lock (sync) { var copy = army.Duplicate(); repository.Save(copy); return copy; }
    }
    public void DeleteArmy(string id)
    {
        lock (sync)
        {
            repository.Delete(id);
            if (Army.Id == id) { Context = new(); repository.SaveContext(Context); undo.Clear(); redo.Clear(); Notify(); }
        }
    }
    public void ResizeUnit(string id, int models)
    {
        lock (sync)
        {
            var entry = Entry(id); var unit = Unit(entry.UnitKey);
            var copy = Result.Entries.Single(e => e.Entry.Id == id).Copy;
            if (!OptionsFor(unit.Key, copy).Any(o => o.Models == models)) throw new InvalidDataException("This unit size has no available price.");
            ReplaceEntry(entry with { Models = models, Loadout = entry.Loadout == null ? null : Loadouts.Resize(unit, entry.Loadout, models) });
        }
    }
    public void ChooseEquipment(string id, int model, string group, string option, int count)
    {
        lock (sync) { var entry = Entry(id); ReplaceEntry(entry with { Loadout = Loadouts.Choose(Unit(entry.UnitKey), entry, model, group, option, count) }); }
    }
    public bool CanChooseEquipment(string id, int model, string group, string option, int count)
    {
        lock (sync)
        {
            try { var entry = Entry(id); Loadouts.Choose(Unit(entry.UnitKey), entry, model, group, option, count); return true; }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentOutOfRangeException) { return false; }
        }
    }
    public void AdjustEquipment(string id, LoadoutCountAction action)
    {
        lock (sync) { var entry = Entry(id); ReplaceEntry(entry with { Loadout = AdjustedLoadout(Unit(entry.UnitKey), entry, action) }); }
    }
    public static UnitLoadout AdjustedLoadout(UnitDefinition unit, ArmyEntry entry, LoadoutCountAction action)
    {
        var current = entry.Loadout ?? Loadouts.Create(unit, entry.Models);
        return action.Group.Length == 0
            ? Loadouts.SetRoleCount(unit, entry, action.Section, action.Role, Loadouts.RoleCount(current, action.Role) + action.Delta)
            : Loadouts.SetOptionCount(unit, entry, action.Role, action.Group, action.Option, Loadouts.OptionCount(current, action.Role, action.Group, action.Option) + action.Delta);
    }
    public static bool CanAdjust(UnitDefinition unit, ArmyEntry? entry, LoadoutCountAction action)
    {
        if (entry == null) return false;
        try { AdjustedLoadout(unit, entry, action); return true; }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentOutOfRangeException) { return false; }
    }
    public void ResetLoadout(string id)
    {
        lock (sync) { var entry = Entry(id); ReplaceEntry(entry with { Loadout = Loadouts.Create(Unit(entry.UnitKey), entry.Models) }); }
    }
    public void Attach(string leaderId, string bodyguardId) { lock (sync) Commit(LeaderRules.Attach(Army, Dataset, leaderId, bodyguardId)); }
    // Both clients use the Core validator for candidate eligibility. Keep new
    // attachment rules there so pickers and committed/saved rosters agree.
    // Candidates intentionally skip enhancement slot conflicts; Attach still checks
    // them before Commit. Preserve that distinction and the existing selections.
    public List<ArmyEntry> AttachmentCandidates(string entryId)
    {
        lock (sync) return AttachmentCandidates(Army, Dataset, entryId);
    }
    // Read-only snapshot overload lets a frontend prepare choices off its UI
    // thread without holding the editing session lock. Validation is identical.
    public static List<ArmyEntry> AttachmentCandidates(ArmyList army, Dataset? data, string entryId)
    {
        var entry = army.Entries.Single(e => e.Id == entryId);
        var leader = LeaderRules.IsLeader(MusterEffects.EffectiveUnit(army, entry, data));
        return army.Entries.Where(e => e.Id != entryId && (leader || e.AttachedToId.Length == 0))
            .Where(e => (leader ? LeaderRules.Problem(army, data, entryId, e.Id, false)
                : LeaderRules.Problem(army, data, e.Id, entryId, false)) == null).ToList();
    }
    public void SelectEnhancement(string id, EnhancementSelection? choice) { lock (sync) Commit(Enhancements.Select(Army, Dataset, id, choice)); }
    public ArmyList ProposeChoice(string id, string groupId, string? optionId)
    {
        lock (sync)
        {
            var entry = Entry(id);
            var group = EffectiveDatasheets.Choices(Army, entry, Dataset).SingleOrDefault(g => g.Id == groupId)
                ?? throw new InvalidDataException("This roster choice is no longer available.");
            if (optionId != null && !group.Options.Any(o => o.Id == optionId)) throw new InvalidDataException("This option is no longer available.");
            var choices = (entry.Choices ?? []).Where(c => c.GroupId != groupId).ToList();
            if (optionId != null) choices.Add(new(groupId, optionId));
            return Army with { Entries = Army.Entries.Select(e => e.Id == id ? e with { Choices = choices } : e).ToList() };
        }
    }
    public List<ArmyEntry> InvalidEnhancements(ArmyList proposed) => proposed.Entries.Where(e => e.Enhancement != null
        && (Enhancements.Find(proposed, Dataset, e.Enhancement) is not { } card
            || !Enhancements.Eligible(proposed, EffectiveDatasheets.Project(proposed, e, Dataset, false)?.Unit, card, Dataset))).ToList();
    public void SelectChoice(string id, string groupId, string? optionId, bool removeInvalidEnhancements = false)
    {
        lock (sync)
        {
            var proposed = ProposeChoice(id, groupId, optionId);
            var invalid = InvalidEnhancements(proposed);
            if (invalid.Count > 0 && !removeInvalidEnhancements)
                throw new InvalidDataException("This change makes these enhancements unavailable: " + string.Join(", ", invalid.Select(e => e.Enhancement!.Name)) + ". Confirm their removal first.");
            var ids = invalid.Select(e => e.Id).ToHashSet();
            Commit(proposed with { Entries = proposed.Entries.Select(e => ids.Contains(e.Id) ? e with { Enhancement = null } : e).ToList() });
        }
    }
    public void ToggleEnhancement(string id, EnhancementSelection choice)
    {
        lock (sync) SelectEnhancement(id, Entry(id).Enhancement is { } current && Enhancements.Key(current) == Enhancements.Key(choice) ? null : choice);
    }
    public void SelectWarlord(string id) { lock (sync) Commit(WarlordRules.Select(Army, Dataset, id)); }
    public void SelectMusterNomination(MusterSelection selection, bool selected)
    { lock (sync) Commit(MusterEffects.Select(Army, Dataset, selection, selected)); }
    public void AddDetachment(string name)
    {
        lock (sync)
        {
            if (!DetachmentRules.CanAdd(Army, name, Dataset)) throw new InvalidDataException("This detachment cannot be added.");
            Commit(DetachmentRules.WithSelection(Army, [.. DetachmentRules.Selected(Army), name]));
        }
    }
    public void RemoveDetachment(string name) { lock (sync) Commit(DetachmentRules.WithSelection(Army, DetachmentRules.Selected(Army).Where(n => n != name))); }
    public void SelectDisposition(string choice)
    {
        lock (sync)
        {
            if (!DetachmentRules.Dispositions(Army, Dataset).Contains(choice)) throw new InvalidDataException("Choose an available disposition.");
            Commit(Army with { Disposition = choice });
        }
    }
    public void Undo() { lock (sync) RestoreHistory(undo, redo); }
    public void Redo() { lock (sync) RestoreHistory(redo, undo); }
    private void RestoreHistory(Stack<ArmyList> from, Stack<ArmyList> to)
    {
        if (!from.TryPeek(out var previous)) return;
        using var datasetEdit = new DatasetEdit(this);
        SetDataset(DatasetForArmy(previous));
        var next = Normalize(Copy(previous)); var result = Calculate(next);
        var context = ValidContext(next, Context);
        repository.Save(next, context);
        var changes = ArmyChanges.Between(Army, next, out var entries);
        to.Push(Copy(Army)); from.Pop(); Army = next; Context = context; Result = result; NewIssues = []; Publish(changes, entries);
    }
    public List<PointOption> OptionsFor(string key, int copy)
    {
        lock (sync) return Dataset?.FindUnit(key)?.PricesFor(Army, Dataset).Where(o => o.FromCopy <= copy)
            .GroupBy(o => o.Models).Select(g => g.MaxBy(o => o.FromCopy)!).OrderBy(o => o.Models).ToList() ?? [];
    }
    private List<LibraryChoice> BuildLibrary(LibraryFilter filter, IReadOnlyList<UnitDefinition> eligible)
    {
        lock (sync)
        {
            var remaining = Army.Limit - Result.Total;
            var units = eligible.AsEnumerable();
            if (filter.HideLegends) units = units.Where(u => !HasLabel(u, "Legends"));
            if (filter.HideCrucible) units = units.Where(u => !HasLabel(u, "Crucible"));
            if (filter.FitsRemaining) units = units.Where(u => MusterRules.AddProblem(Army, Dataset, u) == null && OptionsFor(u.Key, ArmyCalculator.NextCopy(Army, Dataset, u))
                .Any(o => o.Points + WargearPricing.Cost(u, new("preview", u.Key, u.Name, o.Models)) <= remaining));
            return units.OrderBy(u => UnitCategories.Order(AlliedRules.Category(Army, u, Dataset))).ThenBy(u => u.Name)
                .Select(u => new LibraryChoice(u with { Options = u.PricesFor(Army, Dataset), PointsWarning = u.PriceWarningFor(Army) }, MusterRules.AddProblem(Army, Dataset, u))).ToList();
        }
    }
    private static bool HasLabel(UnitDefinition unit, string label) => new[] { unit.Name, unit.Category }.Concat(unit.Profile?.Keywords ?? [])
        .Any(value => Regex.IsMatch(value, $@"\b{label}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    private ArmyList WithDefaultLoadouts(ArmyList army) => army with { Entries = army.Entries.Select(e =>
        e.Loadout == null && Dataset?.FindUnit(e.UnitKey) is { Profile.Loadout: not null } unit ? e with { Loadout = Loadouts.Create(unit, e.Models) } : e).ToList() };
    private ArmyList Normalize(ArmyList army)
    {
        // Rename only through a proven stable source identity; obsolete selections
        // are retained verbatim for review.
        if (Dataset?.DetachmentSources is { Count: > 0 })
        {
            army = DetachmentRules.WithSelection(army, DetachmentRules.Selected(army)
                .Select(n => DetachmentLifecycle.ResolveName(Dataset, army.Faction, n)));
            army = army with { Entries = army.Entries.Select(e => e.Enhancement is { } selected
                ? e with { Enhancement = selected with { Detachment = DetachmentLifecycle.ResolveName(Dataset, army.Faction, selected.Detachment) } } : e).ToList() };
        }
        army = army with { Entries = army.Entries.Select(e => Dataset?.FindUnit(e.UnitKey) is { } unit ? EffectiveDatasheets.NormalizeChoices(e, unit) : e).ToList() };
        army = army with { Entries = army.Entries.Select(e => Dataset?.FindUnit(e.UnitKey) is { } u && (u.Key != e.UnitKey || u.Name != e.Name)
            ? e with { UnitKey = u.Key, Name = u.Name } : e).ToList() };
        army = MusterEffects.Normalize(army, Dataset);
        return DetachmentRules.NormalizeDisposition(LeaderRules.Normalize(WarlordRules.Normalize(army, Dataset)), Dataset);
    }
    private void ReplaceEntry(ArmyEntry entry) => Commit(Army with { Entries = Army.Entries.Select(e => e.Id == entry.Id ? entry : e).ToList() });
    private ArmyEntry Entry(string id) => Army.Entries.Single(e => e.Id == id);
    private UnitDefinition Unit(string key) => Dataset?.FindUnit(key) ?? throw new InvalidDataException("Unit is missing from the active library.");
    private static ArmyList Copy(ArmyList army) => DataJson.Read<ArmyList>(DataJson.Write(army));
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
