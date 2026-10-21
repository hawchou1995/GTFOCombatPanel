using System;
using System.Collections.Generic;
using System.Reflection;

namespace HawChow.GTFOCombatPanel;

// Only managed event metadata crosses this boundary; the public DLL never references an EWC type.
internal sealed class OptionalProjectileEvents : IDisposable
{
    private readonly List<(EventInfo Event, Delegate Handler)> _handlers = new List<(EventInfo, Delegate)>();

    internal static OptionalProjectileEvents Bind(Type api, Action<object> spawn,
        Action<object, object> hit, Action<object> destroy, Action<object, object> postHit = null)
    {
        if (api == null) return null;
        var binding = new OptionalProjectileEvents();
        try
        {
            binding.Add(api, "OnProjectileSpawned", spawn);
            binding.Add(api, "OnProjectileHit", hit);
            if (postHit != null) binding.Add(api, "OnProjectilePostHit", postHit);
            binding.Add(api, "OnProjectileDestroyed", destroy);
            return binding;
        }
        catch { binding.Dispose(); throw; }
    }

    private void Add(Type api, string name, Delegate callback)
    {
        EventInfo evt = api.GetEvent(name, BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingMemberException(api.FullName, name);
        Delegate handler = Delegate.CreateDelegate(evt.EventHandlerType, callback.Target, callback.Method);
        evt.AddEventHandler(null, handler);
        _handlers.Add((evt, handler));
    }

    public void Dispose()
    {
        foreach (var handler in _handlers) handler.Event.RemoveEventHandler(null, handler.Handler);
        _handlers.Clear();
    }
    internal static bool Identity(object projectile, out IntPtr pointer, out ulong token)
    {
        pointer = Read(projectile, "Pointer") is IntPtr address ? address : IntPtr.Zero;
        token = 0;
        if (pointer == IntPtr.Zero || Read(projectile, "PlayerIndex") is not ushort player
            || Read(projectile, "SyncID") is not ushort sync || Read(projectile, "ShotIndex") is not ushort shot) return false;
        token = ((ulong)player << 32) | ((ulong)sync << 16) | shot;
        return true;
    }


    internal static object Read(object value, string name)
    {
        if (value == null) return null;
        Type type = value.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        PropertyInfo property = type.GetProperty(name, flags);
        if (property != null) return property.GetValue(value);
        return type.GetField(name, flags)?.GetValue(value);
    }
}
