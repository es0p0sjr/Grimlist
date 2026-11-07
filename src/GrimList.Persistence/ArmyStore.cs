using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Persistence;

public sealed class ArmyStore : IArmyRepository
{
    public const int SchemaVersion = 3;
    private readonly ISqliteConnection db;
    private readonly object sync = new();
    // Compatibility identity metadata only, never profiles/rules or a dataset copy.
    // Rebuilt on opening the repository and after an applied source transaction.
    private Dictionary<string, (string Key, string Name)> unitIdentities = new(StringComparer.Ordinal);
    public string DirectoryPath { get; }
    public ArmyStore(string directory, ISqliteConnectionFactory connections)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        db = connections.Open(Path.Combine(DirectoryPath, "grimlist.db"));
        try
        {
        db.Run("CREATE TABLE IF NOT EXISTS sources(id TEXT PRIMARY KEY,name TEXT NOT NULL,version TEXT NOT NULL,published TEXT NOT NULL,url TEXT NOT NULL,imported TEXT NOT NULL,game TEXT NOT NULL)");
        db.Run("CREATE TABLE IF NOT EXISTS units(source_id TEXT REFERENCES sources(id),unit_key TEXT,faction TEXT,name TEXT,max_copies INTEGER,PRIMARY KEY(source_id,unit_key))");
        db.Run("CREATE TABLE IF NOT EXISTS points(source_id TEXT,unit_key TEXT,models INTEGER,from_copy INTEGER,points INTEGER NOT NULL,PRIMARY KEY(source_id,unit_key,models,from_copy),FOREIGN KEY(source_id,unit_key) REFERENCES units(source_id,unit_key))");
        db.Run("CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT)");
        db.Run("CREATE TABLE IF NOT EXISTS armies(id TEXT PRIMARY KEY,name TEXT,faction TEXT,points_limit INTEGER,game TEXT,updated TEXT)");
        db.Run("CREATE TABLE IF NOT EXISTS entries(id TEXT PRIMARY KEY,army_id TEXT REFERENCES armies(id) ON DELETE CASCADE,unit_key TEXT,name TEXT,models INTEGER,position INTEGER)");
        db.Transaction(() =>
        {
            if (!db.Run("PRAGMA table_info(armies)").Any(c => c[1] == "muster_selections"))
                db.Run("ALTER TABLE armies ADD COLUMN muster_selections TEXT");
            if (!db.Run("PRAGMA table_info(armies)").Any(c => c[1] == "detachment"))
                db.Run("ALTER TABLE armies ADD COLUMN detachment TEXT NOT NULL DEFAULT ''");
            if (!db.Run("PRAGMA table_info(entries)").Any(c => c[1] == "notes"))
                db.Run("ALTER TABLE entries ADD COLUMN notes TEXT NOT NULL DEFAULT ''");
            if (!db.Run("PRAGMA table_info(armies)").Any(c => c[1] == "warlord_entry_id"))
                db.Run("ALTER TABLE armies ADD COLUMN warlord_entry_id TEXT NOT NULL DEFAULT ''");
            if (!db.Run("PRAGMA table_info(entries)").Any(c => c[1] == "loadout"))
                db.Run("ALTER TABLE entries ADD COLUMN loadout TEXT");
            if (!db.Run("PRAGMA table_info(entries)").Any(c => c[1] == "attached_to_id"))
                db.Run("ALTER TABLE entries ADD COLUMN attached_to_id TEXT NOT NULL DEFAULT ''");
            if (!db.Run("PRAGMA table_info(entries)").Any(c => c[1] == "enhancement"))
                db.Run("ALTER TABLE entries ADD COLUMN enhancement TEXT");
            if (!db.Run("PRAGMA table_info(entries)").Any(c => c[1] == "roster_choices"))
                db.Run("ALTER TABLE entries ADD COLUMN roster_choices TEXT");
        });
        if (!db.Run("PRAGMA table_info(armies)").Any(c => c[1] == "selected_detachments"))
            db.Run("ALTER TABLE armies ADD COLUMN selected_detachments TEXT");
        if (!db.Run("PRAGMA table_info(armies)").Any(c => c[1] == "disposition"))
            db.Run("ALTER TABLE armies ADD COLUMN disposition TEXT NOT NULL DEFAULT ''");
        db.Run("CREATE TABLE IF NOT EXISTS detachments(source_id TEXT REFERENCES sources(id),faction TEXT,name TEXT,PRIMARY KEY(source_id,faction,name))");
        db.Run("CREATE TABLE IF NOT EXISTS source_details(source_id TEXT PRIMARY KEY REFERENCES sources(id),content TEXT NOT NULL)");
        db.Run("CREATE TABLE IF NOT EXISTS dataset_snapshots(source_id TEXT PRIMARY KEY REFERENCES sources(id),content TEXT NOT NULL)");
        db.Run("CREATE TABLE IF NOT EXISTS dataset_chunks(source_id TEXT NOT NULL REFERENCES sources(id),position INTEGER NOT NULL,content TEXT NOT NULL,PRIMARY KEY(source_id,position))");
        CatalogueStorage.Create(db);
        db.Run("CREATE INDEX IF NOT EXISTS entries_army_position ON entries(army_id,position)");
        db.Run("CREATE INDEX IF NOT EXISTS entries_unit_key ON entries(unit_key)");
        // Track the authoritative blob, including edits made outside ApplyImport.
        // Roster/context writes deliberately do not invalidate library caches.
        db.Transaction(() =>
        {
            db.Run("CREATE TABLE IF NOT EXISTS library_state(id INTEGER PRIMARY KEY CHECK(id=1),revision TEXT NOT NULL)");
            db.Run("INSERT OR IGNORE INTO library_state VALUES(1,lower(hex(randomblob(16))))");
            db.Run("CREATE TABLE IF NOT EXISTS library_checks(name TEXT PRIMARY KEY,version TEXT NOT NULL,revision TEXT NOT NULL)");
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                foreach (var table in new[] { "catalogue_objects", "catalogue_chunks" })
                    db.Run($"CREATE TRIGGER IF NOT EXISTS library_{table}_{operation} AFTER {operation} ON {table} BEGIN UPDATE library_state SET revision=lower(hex(randomblob(16))) WHERE id=1; END");
                db.Run($"CREATE TRIGGER IF NOT EXISTS library_snapshot_{operation} AFTER {operation} ON dataset_snapshots BEGIN UPDATE library_state SET revision=lower(hex(randomblob(16))) WHERE id=1; END");
                db.Run($"CREATE TRIGGER IF NOT EXISTS library_chunk_{operation} AFTER {operation} ON dataset_chunks BEGIN UPDATE library_state SET revision=lower(hex(randomblob(16))) WHERE id=1; END");
                var condition = operation == "INSERT" ? "NEW.key='active_source'" : operation == "DELETE" ? "OLD.key='active_source'" : "NEW.key='active_source' OR OLD.key='active_source'";
                db.Run($"CREATE TRIGGER IF NOT EXISTS library_active_{operation} AFTER {operation} ON settings WHEN {condition} BEGIN UPDATE library_state SET revision=lower(hex(randomblob(16))) WHERE id=1; END");
            }
        });
        foreach (var row in db.Run("SELECT unit_key,faction,name FROM units WHERE source_id=(SELECT value FROM settings WHERE key='active_source')"))
        {
            unitIdentities.TryAdd(row[0]!, (row[0]!, row[2]!));
            unitIdentities.TryAdd($"{row[1]!.Trim().ToUpperInvariant()}|{row[2]!.Trim().ToUpperInvariant()}", (row[0]!, row[2]!));
        }
        }
        catch { db.Dispose(); throw; }
    }
    public string? InstalledDataRelease => db.Run("SELECT value FROM settings WHERE key='installed_data_release'").FirstOrDefault()?[0];
    public void VerifyBootstrap(int expectedUnits)
    {
        lock (sync)
        {
            if (LoadArmies().Count != 0
                || db.Run("SELECT count(*) FROM units WHERE source_id=(SELECT value FROM settings WHERE key='active_source')").Single()[0] != expectedUnits.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || db.Run("SELECT count(*) FROM dataset_snapshots").Single()[0] != "1"
                || db.Run("PRAGMA quick_check").Single()[0] != "ok")
                throw new InvalidDataException("The bundled library import did not complete.");
        }
    }
    public void RecordBootstrap(string releaseId, string sha256)
    {
        lock (sync)
        {
            if (LoadArmies().Count != 0 || InstalledDataRelease != null)
                throw new InvalidOperationException("Bootstrap metadata can only be recorded in a new library.");
            db.Transaction(() =>
            {
                db.Run("INSERT INTO settings VALUES('installed_data_release',?)", releaseId);
                db.Run("INSERT INTO settings VALUES('installed_data_sha256',?)", sha256);
                db.Run($"PRAGMA user_version={SchemaVersion}");
            });
            // Ensure the staged main file contains all committed pages before publication.
            db.Run("PRAGMA wal_checkpoint(TRUNCATE)");
            db.Run("PRAGMA journal_mode=DELETE");
        }
    }
    public Dataset? LoadDataset() => LoadLibrary().Dataset;
    // Caller holds a transaction across all chunks, including legacy byte slices.
    private Dataset? ReadDataset()
    {
        lock (sync)
        {
            var active = db.Run("SELECT value FROM settings WHERE key='active_source'").FirstOrDefault()?[0];
            if (active == null) return null;
            var snapshot = DatasetStorage.Read(db, active);
            if (snapshot != null) return ReferenceData.Normalize(snapshot);
            var source = db.Run("SELECT name,version,published,url,imported,game FROM sources WHERE id=?", active).Single();
            var points = db.Run("SELECT unit_key,models,points,from_copy FROM points WHERE source_id=?", active)
                .GroupBy(r => r[0]!).ToDictionary(g => g.Key, g => g.Select(r => new PointOption(int.Parse(r[1]!), int.Parse(r[2]!), int.Parse(r[3]!))).OrderBy(o => o.FromCopy).ThenBy(o => o.Models).ToList());
            var units = db.Run("SELECT unit_key,faction,name,max_copies FROM units WHERE source_id=? ORDER BY faction,name", active)
                .Select(r => new UnitDefinition(r[1]!, r[2]!, points[r[0]!], r[3] == null ? null : int.Parse(r[3]!))).ToList();
            var details = DatasetStorage.ReadLegacy<SourceDetails>(db, "source_details", active) ?? new SourceDetails([], []);
            var profiles = details.Units.ToDictionary(u => u.Key);
            units = units.Select(u => profiles.TryGetValue(u.Key, out var detail) ? u with { Category = detail.Category, Profile = detail.Profile } : u).ToList();
            var detachments = db.Run("SELECT faction,name FROM detachments WHERE source_id=? ORDER BY name", active)
                .Select(r => new DetachmentDefinition(r[0]!, r[1]!)).ToList();
            return ReferenceData.Normalize(new(new(source[0]!, source[1]!, source[2]!, source[3]!, source[4]!), units, source[5]!, detachments, details.Cards));
        }
    }
    private string? ReadLibraryRevision() => db.Run("SELECT revision FROM library_state WHERE id=1 AND EXISTS(SELECT 1 FROM dataset_snapshots WHERE source_id=(SELECT value FROM settings WHERE key='active_source'))").FirstOrDefault()?[0];
    public LoadedLibrary LoadLibrary()
    {
        lock (sync)
        {
            LoadedLibrary loaded = new(null);
            // A concurrent import cannot pair old data with a new revision.
            db.Transaction(() => loaded = new(ReadDataset(), ReadLibraryRevision()));
            return loaded;
        }
    }
    public string? LibraryRevision() { lock (sync) return ReadLibraryRevision(); }
    public bool HasLibraryCheck(string name, string version, string? revision)
    {
        if (revision == null) return false;
        lock (sync) return db.Run("SELECT 1 FROM library_checks WHERE name=? AND version=? AND revision=?", name, version, revision).Count > 0;
    }
    public void RecordLibraryCheck(string name, string version, string? revision)
    {
        if (revision == null) return;
        lock (sync) db.Run("INSERT OR REPLACE INTO library_checks(name,version,revision) SELECT ?,?,revision FROM library_state WHERE id=1 AND revision=?", name, version, revision);
    }
    public static List<PointChange> Compare(Dataset? oldData, Dataset next) => DatasetChanges.Compare(oldData, next);
    public ImportPreview PreviewImport(Dataset data, ImportLayer layer, bool completeFactions = false) => DatasetMerge.Prepare(LoadDataset(), data, layer, completeFactions);
    public void Import(Dataset data) => ApplyImport(PreviewImport(data, ImportLayer.Combined).Dataset);
    public void ApplyImport(Dataset data, string? expectedSnapshot = null)
        => ApplyImport(data, expectedSnapshot, null);
    public void ApplyImportAtRevision(Dataset data, string expectedRevision)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedRevision);
        ApplyImport(data, null, expectedRevision);
    }
    public void ApplyPreparedImport(Dataset data, string? expectedSnapshot, string? expectedRevision, Action<LoadedLibrary> prepare)
        => ApplyImport(data, expectedSnapshot, expectedRevision, prepare);
    private void ApplyImport(Dataset data, string? expectedSnapshot, string? expectedRevision, Action<LoadedLibrary>? prepare = null)
    {
        lock (sync)
        {
            data = ReferenceData.Normalize(LeaderRules.ImportRules(data));
            data.Validate();
            data = data with { Source = data.Source with { ImportedAt = DateTimeOffset.UtcNow.ToString("O") } };
            var id = Guid.NewGuid().ToString();
            db.Transaction(() =>
            {
                if (expectedRevision != null && expectedRevision != ReadLibraryRevision())
                    throw new InvalidDataException("The library changed since preview. Preview again before applying.");
                if (expectedSnapshot != null && expectedSnapshot != DataJson.Write(ReadDataset()))
                    throw new InvalidDataException("The library changed since preview. Preview again before applying.");
                db.Run("INSERT INTO sources VALUES(?,?,?,?,?,?,?)", id, data.Source.Name, data.Source.Version, data.Source.Date, data.Source.Url, DateTimeOffset.UtcNow.ToString("O"), data.Game);
                db.RunBatch("INSERT INTO units VALUES(?,?,?,?,?)", data.Units.Select(unit => new object?[] { id, unit.Key, unit.Faction, unit.Name, unit.MaxCopies }));
                db.RunBatch("INSERT INTO points VALUES(?,?,?,?,?)", data.Units.SelectMany(unit => unit.Options.Select(option => new object?[] { id, unit.Key, option.Models, option.FromCopy, option.Points })));
                db.RunBatch("INSERT INTO detachments VALUES(?,?,?)", (data.Detachments ?? []).Distinct().Select(detachment => new object?[] { id, detachment.Faction, detachment.Name }));
                // The authoritative snapshot already contains profiles/references.
                // source_details is read only for pre-snapshot legacy databases.
                CatalogueStorage.Write(db, id, data);
                // Migrate old name-based roster keys atomically with the update. Preserve entry IDs and loadout notes.
                db.RunBatch("UPDATE entries SET unit_key=?,name=? WHERE unit_key=? AND army_id IN (SELECT id FROM armies WHERE game=?)",
                    data.Units.SelectMany(unit => new[] { unit.NameKey }.Concat(unit.Aliases ?? []).Distinct()
                        .Select(alias => new object?[] { unit.Key, unit.Name, alias, data.Game })));
                db.RunBatch("UPDATE entries SET name=? WHERE unit_key=? AND army_id IN (SELECT id FROM armies WHERE game=?)",
                    data.Units.Select(unit => new object?[] { unit.Name, unit.Key, data.Game }));
                db.Run("INSERT OR REPLACE INTO settings VALUES('active_source',?)", id);
                foreach (var army in LoadArmies())
                    db.Run("UPDATE armies SET warlord_entry_id=? WHERE id=?", WarlordRules.Normalize(army, data).WarlordEntryId, army.Id);
                db.Run($"PRAGMA user_version={SchemaVersion}");
                prepare?.Invoke(new(data, ReadLibraryRevision()));
            });
            var identities = new Dictionary<string, (string Key, string Name)>(StringComparer.Ordinal);
            foreach (var unit in data.Units)
                foreach (var key in new[] { unit.Key, unit.NameKey }.Concat(unit.Aliases ?? [])) identities.TryAdd(key, (unit.Key, unit.Name));
            unitIdentities = identities;
        }
    }
    public void Save(ArmyList army, EditorContext? context = null)
    {
        lock (sync)
        {
            if (string.IsNullOrWhiteSpace(army.Name) || army.Limit <= 0) throw new InvalidDataException("Enter an army name and a positive points limit.");
            // Rules and canonical unit identities belong to ArmySession. Persist only
            // its prepared army/context; a local edit must never read the library blob.
            if (army.WarlordEntryId.Length > 0 && army.Entries.All(e => e.Id != army.WarlordEntryId))
                army = army with { WarlordEntryId = "" };
            army = LeaderRules.Normalize(army);
            db.Transaction(() =>
            {
                db.Run("INSERT INTO armies(id,name,faction,points_limit,game,updated,detachment) VALUES(?,?,?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET name=excluded.name,faction=excluded.faction,points_limit=excluded.points_limit,game=excluded.game,updated=excluded.updated,detachment=excluded.detachment", army.Id, army.Name, army.Faction, army.Limit, army.Game, DateTimeOffset.UtcNow.ToString("O"), army.Detachment);
                db.Run("UPDATE armies SET warlord_entry_id=? WHERE id=?", army.WarlordEntryId, army.Id);
                db.Run("UPDATE armies SET selected_detachments=? WHERE id=?", army.Detachments == null ? null : DataJson.Write(army.Detachments), army.Id);
                db.Run("UPDATE armies SET disposition=? WHERE id=?", army.Disposition, army.Id);
                db.Run("UPDATE armies SET muster_selections=? WHERE id=?", army.MusterSelections == null ? null : DataJson.Write(army.MusterSelections), army.Id);
                if (context != null) SaveContext(context);
                db.Run("DELETE FROM entries WHERE army_id=?", army.Id);
                for (var i = 0; i < army.Entries.Count; i++)
                {
                    var e = army.Entries[i];
                    var identity = unitIdentities.GetValueOrDefault(e.UnitKey, (e.UnitKey, e.Name));
                    db.Run("INSERT INTO entries(id,army_id,unit_key,name,models,position,notes,loadout,attached_to_id,enhancement,roster_choices) VALUES(?,?,?,?,?,?,?,?,?,?,?)", e.Id, army.Id, identity.Item1, identity.Item2, e.Models, i, e.Notes, e.Loadout == null ? null : DataJson.Write(e.Loadout), e.AttachedToId, e.Enhancement == null ? null : DataJson.Write(e.Enhancement), e.Choices == null ? null : DataJson.Write(e.Choices));
                }
            });
        }
    }
    public List<ArmyList> LoadArmies() { lock (sync) { return db.Run("SELECT id,name,faction,points_limit,game,detachment,warlord_entry_id,selected_detachments,disposition,muster_selections FROM armies ORDER BY updated DESC").Select(a =>
        new ArmyList(a[0]!, a[1]!, a[2]!, int.Parse(a[3]!), db.Run("SELECT id,unit_key,name,models,notes,loadout,attached_to_id,enhancement,roster_choices FROM entries WHERE army_id=? ORDER BY position", a[0])
            .Select(e => new ArmyEntry(e[0]!, e[1]!, e[2]!, int.Parse(e[3]!), e[4] ?? "", e[5] == null ? null : DataJson.Read<UnitLoadout>(e[5]!), e[6] ?? "", e[7] == null ? null : DataJson.Read<EnhancementSelection>(e[7]!), e[8] == null ? null : DataJson.Read<List<RosterChoice>>(e[8]!))).ToList(), a[4]!, a[5] ?? "", a[6] ?? "", a[7] == null ? null : DataJson.Read<List<string>>(a[7]!), a[8] ?? "", a[9] == null ? null : DataJson.Read<List<MusterSelection>>(a[9]!))).ToList(); } }
    public void Delete(string id) { lock (sync) { db.Run("DELETE FROM armies WHERE id=?", id); } }
    public EditorContext LoadContext() { lock (sync) { return db.Run("SELECT value FROM settings WHERE key='editor_context'").FirstOrDefault()?[0] is string json
        ? DataJson.Read<EditorContext>(json) : new(); } }
    public void SaveContext(EditorContext context) { lock (sync) { db.Run("INSERT OR REPLACE INTO settings VALUES('editor_context',?)", DataJson.Write(context)); } }
    public void Dispose() { lock (sync) { db.Dispose(); } }
    private record UnitDetail(string Key, string Category, UnitProfile? Profile);
    private record SourceDetails(List<UnitDetail> Units, List<ReferenceCard> Cards);
}
