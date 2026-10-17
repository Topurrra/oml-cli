using System.Collections.Concurrent;
using System.Reflection;

namespace OmlCli;

// A do-nothing implementation of any logger/tracer interface. Interface-typed results are further no-ops,
// and DoWithContext runs the callback it wraps, so model code behaves as if telemetry were off.
public class NoOp : DispatchProxy
{
    static readonly ConcurrentDictionary<Type, object> Cache = new();

    public static object For(Type iface) => Cache.GetOrAdd(iface, t =>
        typeof(DispatchProxy).GetMethod(nameof(Create), 2, Type.EmptyTypes)!
            .MakeGenericMethod(t, typeof(NoOp)).Invoke(null, null)!);

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method is null) return null;
        if (method.Name == "DoWithContext" && args is { Length: > 1 })
            return args[1] switch
            {
                Action a => Run(a),
                Delegate f => f.DynamicInvoke(),
                _ => null,
            };
        var r = method.ReturnType;
        if (r == typeof(void)) return null;
        if (r.IsInterface && r != typeof(IDisposable) && !r.IsGenericType) return For(r);
        if (r.IsGenericType && r.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return Array.CreateInstance(r.GetGenericArguments()[0], 0);
        return r.IsValueType ? Activator.CreateInstance(r) : null;
    }

    static object? Run(Action a) { a(); return null; }
}
