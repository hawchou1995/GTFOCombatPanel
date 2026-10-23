using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace HawChow.GTFOCombatPanel;

// Adapt EWC's value-type pCWC without making it an assembly reference of the public DLL.
internal sealed class OptionalDamageEvents : IDisposable
{
    private readonly List<(EventInfo Event, Delegate Handler)> _handlers = new();
    internal static OptionalDamageEvents Bind(Type api, Action<string, bool, object[]> callback)
    {
        var result = new OptionalDamageEvents();
        try
        {
            foreach (string kind in new[] { "Explosive", "Shrapnel", "DOT" })
                foreach (bool before in new[] { true, false })
                {
                    string name = (before ? "Pre" : "Post") + kind + "Damage";
                    EventInfo evt = api.GetEvent(name, BindingFlags.Public | BindingFlags.Static)
                        ?? throw new MissingMemberException(api.FullName, name);
                    MethodInfo invoke = evt.EventHandlerType.GetMethod("Invoke");
                    ParameterInfo[] signature = invoke.GetParameters();
                    if (signature.Length != 5 || invoke.ReturnType != typeof(void)) throw new InvalidOperationException(name + " signature mismatch");
                    var args = new ParameterExpression[signature.Length];
                    var boxed = new Expression[signature.Length];
                    for (int i = 0; i < args.Length; i++)
                    { args[i] = Expression.Parameter(signature[i].ParameterType); boxed[i] = Expression.Convert(args[i], typeof(object)); }
                    var body = Expression.Invoke(Expression.Constant(callback), Expression.Constant(kind),
                        Expression.Constant(before), Expression.NewArrayInit(typeof(object), boxed));
                    Delegate handler = Expression.Lambda(evt.EventHandlerType, body, args).Compile();
                    evt.AddEventHandler(null, handler);
                    result._handlers.Add((evt, handler));
                }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    public void Dispose()
    {
        foreach (var item in _handlers) item.Event.RemoveEventHandler(null, item.Handler);
        _handlers.Clear();
    }
}

internal static class DamageObservation
{
    internal static double Uncredited(double before, double after, double recordedBefore, double recordedAfter)
    {
        if (!double.IsFinite(before) || !double.IsFinite(after) || !double.IsFinite(recordedBefore) || !double.IsFinite(recordedAfter)) return 0;
        return Math.Max(0, Math.Max(0, before - Math.Max(0, after)) - Math.Max(0, recordedAfter - recordedBefore));
    }
}

internal sealed class DamageCreditLedger
{
    private readonly Dictionary<IntPtr, Dictionary<ulong, double>> _credit = new();
    internal double Read(IntPtr enemy, ulong owner)
        => _credit.TryGetValue(enemy, out var row) && row.TryGetValue(owner, out double amount) ? amount : 0;
    internal void Add(IntPtr enemy, ulong owner, double amount)
    {
        if (enemy == IntPtr.Zero || owner == 0 || !double.IsFinite(amount) || amount <= 0) return;
        if (!_credit.TryGetValue(enemy, out var row)) _credit[enemy] = row = new Dictionary<ulong, double>();
        row[owner] = Math.Min(double.MaxValue, Read(enemy, owner) + amount);
    }
    internal void Spawn(IntPtr enemy) => _credit.Remove(enemy);
    internal void Clear() => _credit.Clear();
}
