using System.Globalization;
using Android.Database.Sqlite;
using GrimList.Platform;

namespace GrimList.Mobile;

public sealed class AndroidSqliteConnectionFactory : ISqliteConnectionFactory
{
    public ISqliteConnection Open(string path) => new AndroidSqliteConnection(path);
}

/// <summary>Android driver only. Schema, migrations, transactions and roster mapping live in ArmyStore.</summary>
public sealed class AndroidSqliteConnection : ISqliteConnection
{
    private readonly SQLiteDatabase database;
    public AndroidSqliteConnection(string path)
    {
        database = SQLiteDatabase.OpenOrCreateDatabase(path, null) ?? throw new IOException("Could not open GrimList database.");
        database.SetForeignKeyConstraintsEnabled(true);
    }
    public List<string?[]> Run(string sql, params object?[] arguments)
    {
        var text = sql.TrimStart();
        // ArmyStore streams new snapshots and slices legacy rows. Never reintroduce
        // scalar whole-catalogue strings here: they exhaust Android's Java heap.
        if (text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || text.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
        {
            using var cursor = database.RawQuery(sql, arguments.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)!).ToArray())!;
            try
            {
                List<string?[]> rows = [];
                while (cursor.MoveToNext()) rows.Add(Enumerable.Range(0, cursor.ColumnCount).Select(i => cursor.IsNull(i) ? null : cursor.GetString(i)).ToArray());
                return rows;
            }
            finally { cursor.Close(); }
        }
        using var statement = database.CompileStatement(sql)!; Bind(statement, arguments); statement.Execute(); return [];
    }
    private static void Bind(SQLiteStatement statement, object?[] arguments)
    {
        for (var i = 0; i < arguments.Length; i++)
            if (arguments[i] == null) statement.BindNull(i + 1);
            else if (arguments[i] is byte[] blob) statement.BindBlob(i + 1, blob);
            else statement.BindString(i + 1, Convert.ToString(arguments[i], CultureInfo.InvariantCulture)!);
    }
    public byte[]? ReadBlob(string sql, params object?[] arguments)
    {
        using var cursor = database.RawQuery(sql, arguments.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)!).ToArray())!;
        try { return cursor.MoveToFirst() ? cursor.GetBlob(0) : null; }
        finally { cursor.Close(); }
    }
    public void RunBatch(string sql, IEnumerable<object?[]> rows)
    {
        using var statement = database.CompileStatement(sql)!;
        foreach (var row in rows) { statement.ClearBindings(); Bind(statement, row); statement.Execute(); }
    }
    public void Transaction(Action action)
    {
        // IMMEDIATE serializes writers while permitting another connection to
        // read the committed revision during import/cache preparation.
        database.BeginTransactionNonExclusive();
        try { action(); database.SetTransactionSuccessful(); }
        finally { database.EndTransaction(); }
    }
    public void Dispose() { database.Close(); database.Dispose(); }
}
