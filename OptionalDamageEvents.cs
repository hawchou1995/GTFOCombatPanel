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

// What a blow this machine dealt to an enemy limb is worth, for the one case the game leaves unread. On a
// client whose host runs no panel of its own, the host applies the blow and this machine is told the result
// later, so reading the limb's health before and after the call can return the same number twice. The figure
// the weapon carried into the call is then all there is - but it is the pre-armour, pre-precision value, so
// it is an estimate and is capped by the health the limb still had. When the health did move, that loss is
// the applied damage and it wins outright, which is the 1.19.0 reading and stays exactly as it was.
internal static class LocalOutgoingRule
{
    internal static double Amount(float healthBefore, float healthAfter, float dam)
    {
        if (!float.IsFinite(healthBefore) || !float.IsFinite(healthAfter)) return 0;
        double delta = Math.Max(0f, Math.Max(0f, healthBefore) - Math.Max(0f, healthAfter));
        if (delta > 0) return delta;
        if (!float.IsFinite(dam)) return 0;
        // A limb already at zero keeps it that way, and an overkill cannot credit more than the pool it emptied.
        return Math.Max(0, Math.Min(dam, Math.Max(0f, healthBefore)));
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
