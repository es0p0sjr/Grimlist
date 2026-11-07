using GrimList.Mobile;

internal static class ScrollRetentionChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("Scroll anchor follows the visible unit through insertion and size sorting", () =>
        {
            var bookmark = new ScrollBookmark(["header", "category", "small", "large", "other"], 2, -83);
            if (bookmark.Resolve(["header", "category", "new", "large", "small", "other"]) != 4 || bookmark.Offset != -83)
                throw new Exception("Visible unit or partial row offset lost during reorder");
        });
        test("Scroll anchor survives deleting the visible row or its whole category", () =>
        {
            var bookmark = new ScrollBookmark(["header", "category", "a", "b", "c"], 3, -20);
            if (bookmark.Resolve(["header", "category", "a", "c"]) != 3 || bookmark.Resolve(["header", "category", "a"]) != 2
                || bookmark.Resolve(["header", "empty"]) != 0)
                throw new Exception("Deleted anchor did not fall back to a surviving neighbor");
        });
        test("Scroll anchor handles empty lists, header and entirely replaced search results", () =>
        {
            var bookmark = new ScrollBookmark(["a", "b", "c"], 2, 0);
            if (bookmark.Resolve([]) != -1 || bookmark.Resolve(["new"]) != 0
                || new ScrollBookmark([], 0, 0).Resolve(["header", "a"]) != 0
                || new ScrollBookmark(["header", "a"], 0, -12).Resolve(["header", "b", "a"]) != 0)
                throw new Exception("Invalid fallback for an empty or replaced list");
        });
    }
}
