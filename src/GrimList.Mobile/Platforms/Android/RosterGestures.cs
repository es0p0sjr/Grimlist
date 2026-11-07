using Android.Views;
using Microsoft.Maui.Controls;
using NativeView = Android.Views.View;

namespace GrimList.Mobile;

/// <summary>Android tap/hold recognizer that permits parent scroll interception and cancels taps after a hold.</summary>
internal static class RosterGestures
{
    internal static Task<string?> Menu(VisualElement anchor)
    {
        if (anchor.Handler?.PlatformView is not NativeView view) return Task.FromResult<string?>(null);
        var completion = new TaskCompletionSource<string?>();
        var menu = new Android.Widget.PopupMenu(view.Context!, view);
        menu.Menu!.Add(0, 1, 0, "Duplicate");
        menu.Menu!.Add(0, 2, 1, "Delete");
        menu.MenuItemClick += (_, e) => completion.TrySetResult(e.Item?.ItemId == 1 ? "Duplicate" : "Delete");
        menu.DismissEvent += (_, _) => { completion.TrySetResult(null); menu.Dispose(); };
        menu.Show();
        return completion.Task;
    }
    internal static void Attach(VisualElement element, Action tap, Action? hold)
    {
        NativeView? native = null;
        GestureDetector? detector = null;
        Listener? listener = null;
        void Click(object? sender, EventArgs args) => tap();
        void LongClick(object? sender, NativeView.LongClickEventArgs args)
        { args.Handled = hold != null; if (hold != null) { native?.PerformHapticFeedback(FeedbackConstants.LongPress); hold(); } }
        void Touch(object? sender, NativeView.TouchEventArgs args)
        { if (args.Event != null && detector != null) { detector.OnTouchEvent(args.Event); args.Handled = true; } }
        void Disconnect()
        {
            if (native != null) { native.Touch -= Touch; native.Click -= Click; native.LongClick -= LongClick; }
            detector?.Dispose(); listener?.Dispose(); detector = null; listener = null; native = null;
        }
        void Connect()
        {
            if (element.Handler?.PlatformView is not NativeView view || ReferenceEquals(native, view)) return;
            Disconnect(); native = view;
            listener = new Listener(view); detector = new GestureDetector(view.Context!, listener) { IsLongpressEnabled = hold != null };
            view.Clickable = true; view.LongClickable = hold != null;
            view.Click += Click; view.LongClick += LongClick; view.Touch += Touch;
        }
        element.HandlerChanging += (_, _) => Disconnect();
        element.HandlerChanged += (_, _) => Connect();
        element.Loaded += (_, _) => Connect();
        element.Unloaded += (_, _) => Disconnect();
        Connect();
    }
    private sealed class Listener(NativeView view) : GestureDetector.SimpleOnGestureListener
    {
        private bool held;
        public override bool OnDown(MotionEvent? e) { held = false; return true; }
        public override bool OnSingleTapUp(MotionEvent? e) { if (!held) view.PerformClick(); return true; }
        public override void OnLongPress(MotionEvent? e) { held = true; view.PerformLongClick(); }
    }
}
