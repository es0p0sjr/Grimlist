using AndroidX.RecyclerView.Widget;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private readonly Dictionary<string, ScrollBookmark> scrollBookmarks = [];
    private readonly List<ScrollRegistration> scrollRegistrations = [];
    private readonly Dictionary<string, HashSet<string>> expandedReferences = [];

    private void CaptureScroll()
    {
        foreach (var registration in scrollRegistrations) registration.Capture();
    }
    private void RestoreScroll()
    {
        foreach (var registration in scrollRegistrations) registration.Restore();
    }
    private void ReleaseScroll()
    {
        CaptureScroll();
        foreach (var registration in scrollRegistrations) registration.Dispose();
        scrollRegistrations.Clear();
    }
    private void RetainScroll(View view, string panel, Func<string[]>? rows = null)
    {
        var key = ViewKey + "/" + panel;
        // This is a visit history, never army data; bound it for long sessions.
        if (!scrollBookmarks.ContainsKey(key))
        {
            if (scrollBookmarks.Count >= 64) scrollBookmarks.Remove(scrollBookmarks.Keys.First());
            scrollBookmarks[key] = new([], 0, 0);
        }
        scrollRegistrations.Add(new(view, rows, () => scrollBookmarks.GetValueOrDefault(key), value => scrollBookmarks[key] = value));
    }
    private HashSet<string> ExpandedReferences()
    {
        if (!expandedReferences.TryGetValue(ViewKey, out var expanded))
        {
            if (expandedReferences.Count >= 16) expandedReferences.Remove(expandedReferences.Keys.First());
            expandedReferences[ViewKey] = expanded = [];
        }
        return expanded;
    }

    // Android's list layout manager restores both the row and the partially
    // visible pixels. MAUI ScrollTo(Start) alone loses that partial-row offset.
    private sealed class ScrollRegistration : IDisposable
    {
        public View Control => view;
        private readonly View view;
        private readonly Func<string[]>? rows;
        private readonly Func<ScrollBookmark?> read;
        private readonly Action<ScrollBookmark> save;
        private bool disposed, pending;
        private int generation;

        public ScrollRegistration(View view, Func<string[]>? rows, Func<ScrollBookmark?> read, Action<ScrollBookmark> save)
        {
            this.view = view; this.rows = rows; this.read = read; this.save = save;
            pending = true;
            view.Loaded += Loaded;
            view.Unloaded += Unloaded;
            view.SizeChanged += Resized;
            Restore();
        }
        private void Loaded(object? sender, EventArgs e) { if (pending) Restore(); }
        private void Unloaded(object? sender, EventArgs e)
        {
            Capture(); ++generation; pending = true;
        }
        private void Resized(object? sender, EventArgs e) { if (pending) Restore(); }
        public void Capture()
        {
            if (disposed || pending || view.Handler?.PlatformView is not Android.Views.View native || !native.IsLaidOut) return;
            if (rows != null && native is RecyclerView recycler && recycler.GetLayoutManager() is LinearLayoutManager layout)
            {
                var index = layout.FindFirstVisibleItemPosition();
                if (index >= 0 && layout.FindViewByPosition(index) is { } child)
                    save(new(rows(), index, layout.GetDecoratedTop(child) - recycler.PaddingTop));
            }
            else if (rows == null) save(new([], 0, native.ScrollY));
        }
        public void Restore()
        {
            if (disposed || read() is not { } bookmark) return;
            pending = true;
            var version = ++generation;
            if (view.Handler?.PlatformView is not Android.Views.View native) return; // Loaded will retry.
            var attempts = 0;
            void Apply()
            {
                if (disposed || version != generation || view.Handler?.PlatformView != native) return;
                if (!native.IsAttachedToWindow || !native.IsLaidOut || native.IsLayoutRequested || native.Height <= 0)
                {
                    if (++attempts < 30) native.PostOnAnimation(new Java.Lang.Runnable(Apply));
                    else pending = false;
                    return;
                }
                if (rows != null && native is RecyclerView recycler && recycler.GetLayoutManager() is LinearLayoutManager layout)
                {
                    // Never pull a list away from a gesture which began while a
                    // deferred layout was pending.
                    if (recycler.ScrollState != RecyclerView.ScrollStateDragging)
                    {
                        var index = bookmark.Resolve(rows());
                        if (index >= 0) layout.ScrollToPositionWithOffset(index, bookmark.Offset);
                    }
                }
                else if (rows == null) native.ScrollTo(native.ScrollX, bookmark.Offset);
                native.PostOnAnimation(new Java.Lang.Runnable(() => { if (!disposed && version == generation) pending = false; }));
            }
            native.PostOnAnimation(new Java.Lang.Runnable(Apply));
        }
        public void Dispose()
        {
            disposed = true; ++generation;
            view.Loaded -= Loaded; view.Unloaded -= Unloaded; view.SizeChanged -= Resized;
        }
    }
}
