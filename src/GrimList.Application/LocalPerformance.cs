using System.Diagnostics;

namespace GrimList.Application;

// Opt-in local diagnostics. Production has no listener, storage or network sink.
public static class LocalPerformance
{
    public record Sample(string Stage, double Milliseconds);
    private static readonly AsyncLocal<Action<Sample>?> listener = new();
    private sealed class Scope(Action<Sample>? previous) : IDisposable
    { public void Dispose() => listener.Value = previous; }
    public static IDisposable Capture(Action<Sample> receive)
    { var scope = new Scope(listener.Value); listener.Value = receive; return scope; }
    public static T Measure<T>(string stage, Func<T> action)
    {
        if (listener.Value is not { } receive) return action();
        var started = Stopwatch.GetTimestamp();
        try { return action(); }
        finally { receive(new(stage, Stopwatch.GetElapsedTime(started).TotalMilliseconds)); }
    }
    public static void Measure(string stage, Action action) => Measure(stage, () => { action(); return true; });
}
