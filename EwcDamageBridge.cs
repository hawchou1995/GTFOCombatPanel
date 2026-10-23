using System;
using System.Collections.Generic;
using System.Reflection;
using Enemies;
using Player;
using UnityEngine;

namespace HawChow.GTFOCombatPanel;

internal static class EwcDamageBridge
{
    private static OptionalDamageEvents _binding;
    private static bool _watching, _retry = true, _warned, _credited;
    private sealed class Pending
    {
        internal string Kind;
        internal IntPtr Enemy, Source;
        internal int Frame;
        internal Dam_EnemyDamageBase Target;
        internal DamageSample Sample;
        internal double Recorded;
    }
    [ThreadStatic] private static List<Pending> _pending;
    internal static void Reset()
    {
        if (_pending != null)
            foreach (var entry in _pending) CombatTracker.CompleteDamage(entry.Target, entry.Sample, new InvalidOperationException("incomplete EWC event"));
        _pending?.Clear(); _credited = false;
    }
    internal static void EnsureInitialized()
    {
        if (!_watching)
        {
            _watching = true;
            AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
            { if (args.LoadedAssembly.GetName().Name == "ExtraWeaponCustomization") _retry = true; };
        }
        if (_binding != null || !_retry) return;
        _retry = false;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name != "ExtraWeaponCustomization") continue;
            try
            {
                _binding = OptionalDamageEvents.Bind(assembly.GetType("EWC.API.DamageAPI", true), Observe);
                Plugin.Logger?.LogInfo("[DIAG] EWC damage bridge armed: paired explosive/shrapnel/DOT events; actual health loss minus native credit; source slot from pCWC.");
            }
            catch (Exception error) { Warn(error); }
            return;
        }
    }
    private static void Observe(string kind, bool before, object[] args)
    {
        try
        {
            var enemy = args[1] as EnemyAgent;
            var source = args[3] as PlayerAgent;
            if (enemy?.Damage == null || source == null) return;
            _pending ??= new List<Pending>();
            for (int i = _pending.Count - 1; i >= 0; i--)
                if (_pending[i].Frame != Time.frameCount)
                { CombatTracker.CompleteDamage(_pending[i].Target, _pending[i].Sample, new InvalidOperationException("unpaired EWC event")); _pending.RemoveAt(i); }
            if (before)
            {
                if (_pending.Count >= 64) return;
                object slot = OptionalProjectileEvents.Read(args[4], "slot");
                if (slot == null) return;
                var sample = CombatTracker.CaptureEwcDamage(enemy.Damage, source, Convert.ToInt32(slot), kind == "DOT" ? DamageKind.Fire : DamageKind.Explosion);
                if (sample.Player == null) return;
                _pending.Add(new Pending { Kind = kind, Enemy = enemy.Pointer, Source = source.Pointer, Frame = Time.frameCount,
                    Target = enemy.Damage, Sample = sample, Recorded = CombatTracker.DamageCredit(enemy.Damage, sample.Player.Lookup) });
                return;
            }
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Pending entry = _pending[i];
                if (entry.Kind != kind || entry.Enemy != enemy.Pointer || entry.Source != source.Pointer) continue;
                _pending.RemoveAt(i);
                CombatTracker.CompleteEwcDamage(entry.Target, entry.Sample, entry.Recorded);
                if (!_credited && CombatTracker.DamageCredit(entry.Target, entry.Sample.Player.Lookup) > entry.Recorded)
                {
                    _credited = true;
                    Plugin.Logger?.LogInfo($"[DIAG] EWC damage measured: kind={kind}; source={entry.Sample.Player.Lookup}; slot={entry.Sample.Weapon?.Slot}; actual health-loss reconciliation active.");
                }
                return;
            }
        }
        catch (Exception error) { Warn(error); }
    }
    private static void Warn(Exception error)
    {
        if (_warned) return;
        _warned = true;
        Plugin.Logger?.LogWarning("[DIAG] EWC damage observation skipped: " + error.GetType().Name + ": " + error.Message);
    }
}
