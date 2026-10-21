using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// One FireBullet invocation on one turret. The token is what lets a native OnBulletFired notification
// raised inside that call be recognised as the shot the call already paid for, instead of being added
// on top of it or swallowed by it.
internal readonly struct SentryShotScope
{
    internal readonly IntPtr Turret;
    internal readonly long Token;

    internal SentryShotScope(IntPtr turret, long token) { Turret = turret; Token = token; }

    internal bool IsOpen => Turret != IntPtr.Zero && Token != 0L;
}

// Sentry rounds are visible through two independent streams: the master's FireBullet call, which is the
// only one that knows `doDamage`, and the turret's native OnBulletFired notification, which carries
// nothing but "a bullet left this turret". Reconciling the two by max(cumulative calls, cumulative
// events) counts a missing call as a duplicate of a later event - three calls-only rounds followed by
// three events-only rounds read as three shots - so a mode switch silently eats rounds in either
// direction, and a stream that drops out for a while is invisible until it overtakes the other.
//
// Here each call owns a scope instead. The call supplies its own shot (one when `doDamage`, none when
// the round was fired without damage), and a native notification raised inside that scope is attributed
// to the same call: it is not a second shot, and a non-damaging call suppresses its own notification.
// Outside every scope the notification has no call to pair with, so it is the only evidence that a round
// happened and it counts - that is the native-only fallback. A notification that arrives after its call
// returned cannot be paired by construction; the call is already counted, so the fallback is documented
// here rather than hidden.
internal sealed class SentryShotLedger
{
    // A call that never closes its scope (a patch that threw before the finalizer, a hook removed
    // mid-call) must not be able to suppress notifications for the rest of the expedition, so the open
    // stack is capped; the oldest scope falls off and stops pairing.
    internal const int MaxOpenCalls = 8;

    private sealed class Call
    {
        internal long Token;
        internal bool Counts;
        internal bool Claimed;
    }

    private sealed class Stream
    {
        internal readonly List<Call> Open = new List<Call>();
        internal long LastToken;
    }

    private readonly Dictionary<IntPtr, Stream> _streams = new Dictionary<IntPtr, Stream>();

    // Opens the scope for one FireBullet invocation and reports what the call itself contributes: one
    // shot when the round was fired with damage, zero when it was fired without (no damage, no shot).
    internal long BeginCall(IntPtr turret, bool counts, out SentryShotScope scope)
    {
        scope = default;
        if (turret == IntPtr.Zero) return 0;
        Stream stream = GetStream(turret);
        stream.LastToken = stream.LastToken < long.MaxValue ? stream.LastToken + 1 : 1L;
        var call = new Call { Token = stream.LastToken, Counts = counts };
        stream.Open.Add(call);
        while (stream.Open.Count > MaxOpenCalls) stream.Open.RemoveAt(0);
        scope = new SentryShotScope(turret, call.Token);
        return counts ? 1L : 0L;
    }

    internal void EndCall(SentryShotScope scope)
    {
        if (!scope.IsOpen || !_streams.TryGetValue(scope.Turret, out Stream stream)) return;
        for (int index = stream.Open.Count - 1; index >= 0; index--)
        {
            if (stream.Open[index].Token != scope.Token) continue;
            stream.Open.RemoveAt(index);
            return;
        }
    }

    // A native notification for one turret. Inside an open scope it belongs to that call: a damaging call
    // absorbs the first notification as its own shot, and a call fired without damage absorbs all of them,
    // because neither the call nor its notifications dealt damage. A second notification inside one
    // damaging call is a second round of that call (a burst), which max() also counted, so it is added.
    internal long ObserveEvent(IntPtr turret)
    {
        if (turret == IntPtr.Zero) return 0;
        if (!_streams.TryGetValue(turret, out Stream stream) || stream.Open.Count == 0) return 1L;
        Call call = stream.Open[stream.Open.Count - 1];
        if (!call.Counts) return 0;
        if (!call.Claimed) { call.Claimed = true; return 0; }
        return 1L;
    }

    internal bool HasOpenCall(IntPtr turret)
        => turret != IntPtr.Zero && _streams.TryGetValue(turret, out Stream stream) && stream.Open.Count > 0;

    internal void Forget(IntPtr turret) => _streams.Remove(turret);

    internal void Clear() => _streams.Clear();

    private Stream GetStream(IntPtr turret)
    {
        if (!_streams.TryGetValue(turret, out Stream stream)) _streams[turret] = stream = new Stream();
        return stream;
    }
}
