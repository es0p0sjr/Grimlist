using System.Globalization;
using System.Runtime.InteropServices;

namespace GrimList.Platform.Windows;

public sealed class WindowsSqliteConnection : ISqliteConnection
{
    private IntPtr handle;
    private static readonly IntPtr Transient = new(-1);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int length, IntPtr destructor);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_blob(IntPtr stmt, int index, byte[] value, int length, IntPtr destructor);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_reset(IntPtr stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_clear_bindings(IntPtr stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr stmt, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);

    public WindowsSqliteConnection(string path)
    {
        var rc = sqlite3_open_v2(path, out handle, 2 | 4, IntPtr.Zero);
        if (rc != 0) { var message = Error(); Dispose(); throw new IOException(message); }
        Run("PRAGMA foreign_keys=ON");
        Run("PRAGMA busy_timeout=5000");
    }
    private string Error() => Marshal.PtrToStringUTF8(sqlite3_errmsg(handle)) ?? "SQLite error";
    public List<string?[]> Run(string sql, params object?[] args)
    {
        if (sqlite3_prepare_v2(handle, sql, -1, out var statement, IntPtr.Zero) != 0) throw new IOException(Error());
        try
        {
            Bind(statement, args);
            List<string?[]> rows = [];
            int step;
            while ((step = sqlite3_step(statement)) == 100)
                rows.Add(Enumerable.Range(0, sqlite3_column_count(statement)).Select(i => Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i))).ToArray());
            if (step != 101) throw new IOException(Error());
            return rows;
        }
        finally { sqlite3_finalize(statement); }
    }
    public byte[]? ReadBlob(string sql, params object?[] args)
    {
        if (sqlite3_prepare_v2(handle, sql, -1, out var statement, IntPtr.Zero) != 0) throw new IOException(Error());
        try
        {
            Bind(statement, args);
            var step = sqlite3_step(statement);
            if (step == 101) return null;
            if (step != 100) throw new IOException(Error());
            var bytes = new byte[sqlite3_column_bytes(statement, 0)];
            if (bytes.Length > 0) Marshal.Copy(sqlite3_column_blob(statement, 0), bytes, 0, bytes.Length);
            return bytes;
        }
        finally { sqlite3_finalize(statement); }
    }
    private void Bind(IntPtr statement, object?[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var rc = args[i] is byte[] blob ? sqlite3_bind_blob(statement, i + 1, blob, blob.Length, Transient)
                : args[i] == null ? sqlite3_bind_null(statement, i + 1)
                : sqlite3_bind_text(statement, i + 1, Convert.ToString(args[i], CultureInfo.InvariantCulture)!, -1, Transient);
            if (rc != 0) throw new IOException(Error());
        }
    }
    public void RunBatch(string sql, IEnumerable<object?[]> rows)
    {
        if (sqlite3_prepare_v2(handle, sql, -1, out var statement, IntPtr.Zero) != 0) throw new IOException(Error());
        try
        {
            foreach (var row in rows)
            {
                Bind(statement, row);
                if (sqlite3_step(statement) != 101) throw new IOException(Error());
                if (sqlite3_reset(statement) != 0 || sqlite3_clear_bindings(statement) != 0) throw new IOException(Error());
            }
        }
        finally { sqlite3_finalize(statement); }
    }
    public void Transaction(Action action)
    {
        Run("BEGIN IMMEDIATE");
        try { action(); Run("COMMIT"); }
        catch { Run("ROLLBACK"); throw; }
    }
    public void Dispose() { if (handle != IntPtr.Zero) { sqlite3_close(handle); handle = IntPtr.Zero; } }
}

