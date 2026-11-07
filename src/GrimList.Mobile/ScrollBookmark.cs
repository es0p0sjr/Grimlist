namespace GrimList.Mobile;

// Presentation-only anchors. Keep the visible row when sorting/insertion changes
// its index; if it was deleted, prefer the following surviving row, then previous.
internal sealed record ScrollBookmark(string[] Rows, int Index, int Offset)
{
    public int Resolve(IReadOnlyList<string> rows)
    {
        if (rows.Count == 0) return -1;
        if (Index < 0 || Index >= Rows.Length) return 0;
        var positions = rows.Select((key, index) => (key, index)).ToDictionary(p => p.key, p => p.index);
        for (var i = Index; i < Rows.Length; i++)
            if (positions.TryGetValue(Rows[i], out var next)) return next;
        for (var i = Index - 1; i >= 0; i--)
            if (positions.TryGetValue(Rows[i], out var previous)) return previous;
        return Math.Min(Index, rows.Count - 1);
    }
}
