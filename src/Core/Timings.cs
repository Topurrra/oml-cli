using System.Diagnostics;
using System.Text.Json.Nodes;

namespace OmlCli;

// `--timings`: milliseconds per phase of one command, added to its JSON report as "timings". A phase counts only
// its own time: a load inside "reopen" counts as "reopen", and Service Studio's start-up inside the first load as
// "serviceStudioInit". A phase that runs twice is summed. Off by default; when off, Measure only runs the work.
static class Timings
{
    public static bool Enabled { get; private set; }
    static readonly List<(string name, long ticks)> phases = new();
    static readonly Stack<long> childTicks = new();
    static long origin;

    // Starts a new command. Only the first command of the process counts the time before it as "startup":
    // the .NET runtime, the launcher and the load of oml's Core assembly.
    public static void Start(bool enabled)
    {
        Enabled = enabled;
        phases.Clear();
        childTicks.Clear();
        origin = Stopwatch.GetTimestamp();
        var first = !started;
        started = true;
        if (!enabled || !first) return;
        var since = DateTime.Now - Process.GetCurrentProcess().StartTime;
        phases.Add(("startup", (long)(since.TotalSeconds * Stopwatch.Frequency)));
        origin -= phases[0].ticks;
    }
    static bool started;

    public static T Measure<T>(string phase, Func<T> work)
    {
        using var _ = Phase(phase);
        return work();
    }

    public static void Measure(string phase, Action work)
    {
        using var _ = Phase(phase);
        work();
    }

    // A phase that ends when the result is disposed: `using var build = Timings.Phase("build");`. Disposing it
    // again does nothing, so a phase can end early and still be safe on every return path. Null when timings are off.
    public static Running? Phase(string name)
    {
        if (!Enabled) return null;
        childTicks.Push(0);
        return new Running(name, Stopwatch.GetTimestamp());
    }

    public sealed class Running(string name, long start) : IDisposable
    {
        bool ended;

        public void Dispose()
        {
            if (ended) return;
            ended = true;
            var elapsed = Stopwatch.GetTimestamp() - start;
            var own = elapsed - childTicks.Pop();
            if (childTicks.Count > 0) childTicks.Push(childTicks.Pop() + elapsed);
            phases.Add((name, own));
        }
    }

    // {"startup": 410, "load": 2300, ..., "other": 12, "total": 2900}: phases in the order they first ran;
    // "other" is the command's time that no phase covers.
    public static JsonObject Report()
    {
        var total = Stopwatch.GetTimestamp() - origin;
        var report = new JsonObject();
        foreach (var group in phases.GroupBy(p => p.name)) report[group.Key] = Ms(group.Sum(p => p.ticks));
        report["other"] = Ms(Math.Max(0, total - phases.Sum(p => p.ticks)));
        report["total"] = Ms(total);
        return report;
    }

    static long Ms(long ticks) => ticks * 1000 / Stopwatch.Frequency;
}
