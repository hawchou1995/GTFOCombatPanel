using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SNetwork;

namespace HawChow.GTFOCombatPanel;

// The host speaks to a client over the game's own byte channel, because the game's custom-data entry
// point cannot carry a type it has never seen: `SNet.SetupCustomData<A>` asks IL2CPP for A's pointer
// store, and a mod-defined struct has none, which is the TypeInitializationException that stopped the
// first attempt. One level lower the game takes an arbitrary run of bytes:
//
//   send    `SNet.Core.SendBytes(bytes, quality, channel, player)` puts that run on the wire.
//   receive every packet passes `SNet_Replication.RecieveBytes`, which this mod takes in a Harmony
//           prefix. A packet of ours is recognised by an unused key plus a magic string, and returning
//           false from the prefix keeps the game from ever looking for a replicator that does not exist.
//
// The bytes are assembled and taken apart by `SquadPackets`, which has no Unity, SNet or Harmony
// reference and is therefore asserted headlessly, including every length and version rule.
//
// Two transports carry the same packet. `TryBindApi` binds GTFO-API's free-sized event when the library is
// loaded, and that is the path this build prefers: the event already carries a fixed replicator key, a long
// magic and a version signature. It is what StatDisplay, AccuracyTracker and the rest of the scene broadcast
// on - and a machine that runs no mod is *not* silent about it: GTFO-API's receive path looks the event name up
// in its own registry and, finding nothing, writes `[Error : GTFO-API] [NetworkAPI]: <id> invoked an event
// <name> which isn't registered` into that machine's log once per packet (10225 for this event in one recorded
// run). Every packet this machine sends therefore costs an unmodded peer one error line, which is why the send
// side is a bounded probe. The game's own byte channel remains underneath for a machine without the library:
//
//   send    `SNet.Core.SendBytes(bytes, quality, channel, player)` puts that run on the wire.
//   receive every packet passes `SNet_Replication.RecieveBytes`, which this mod takes in a Harmony
//           prefix. A packet of ours is recognised by an unused key plus a magic string, and returning
//           false from the prefix keeps the game from ever looking for a replicator that does not exist.
//
// `MaySend` is still the last gate before either route, and it is asked with the packet's own kind. Its rule is
// "proved, named, or a greeting still inside the bounded probe" - sharing with the slot list buys an unproven peer
// `SquadCapability.ProbeBudget` greetings and no rows, because the packets are not free on the other end and because
// such a peer has no handler for a row. A mistake higher up still cannot turn into a packet for a peer this machine
// has decided to stay silent with, nor into a row for one that has only been probed. A gate that was never set
// refuses everything, which is the safe reading of "nobody said this machine may speak".
//
// The receive callback is queued, not run in place. The replication layer's thread is not this mod's to
// assume: a decoded packet is parked in a bounded queue (256) and drained from `CombatSync.Tick`, which
// the main thread calls. Nothing in the queue path touches a Unity or game object - it holds only the
// snapshot struct - so whichever thread the packet arrives on, the handlers that read game state always
// run on the main thread. A full queue drops its oldest entry rather than growing.
internal static class SquadNet
{
    // Handed the decoded packet. Set by CombatSync.
    internal static Action<ulong, uint, ulong> OnHello;
    internal static Action<ulong, uint, SyncSnapshot> OnSnapshot;
    internal static Action<ulong, uint> OnProtocolMismatch;
    // 1.19.34: the pack kinds a peer sends for its own row. A v8 peer never fires this, and this build never needs it to
    // keep reading that peer's row - it only fills in the split that row's snapshot could not carry.
    internal static Action<ulong, uint, SyncProtocol.SupplyKinds> OnSupplyKinds;

    // Whether this machine may put one of its own packets on the wire for a peer, and which packet it is: the
    // transport asks with the kind it is about to hand over, because the bounded probe this build shares with its
    // slot list pays for a greeting and for nothing else - a row for a peer that has not proved it can read one is
    // refused at this seam whatever the queue did or did not ask for. The answer is the evidence that allowed it,
    // so the transport can tell a probe credit (which buys exactly one physical packet) from a proved or named peer
    // (whose packet may go out both ways until its route is known). Set by CombatSync from the exchange's evidence;
    // left null (and then refusing everything) until it is.
    internal static Func<ulong, SquadPacketKind, SquadEvidence> MaySend;

    internal const int MaxQueue = 256;
    private struct Queued
    {
        public SquadPacketKind Kind; public ulong Sender; public ulong HelloOwner; public uint Generation; public uint Version;
        public SyncSnapshot Snapshot;
        // 1.19.34: set for `SupplyKinds` only; the snapshot is default on those.
        public SyncProtocol.SupplyKinds Kinds;
        public bool ViaApi;
    }
    private static readonly ConcurrentQueue<Queued> Queue = new ConcurrentQueue<Queued>();
    // Which transport each peer has been heard on, and when that was learned: true for GTFO-API's event,
    // false for the game's own byte channel. Written when a packet arrives, read when one is sent, so a mixed
    // squad converges on a route that is known to work in both directions instead of each half sending into
    // the other's blind side.
    //
    // A route is evidence with an age. It is recorded only for a packet of ours that actually decoded - a
    // packet the transport handed over in a shape this build could not read proves nothing about whether the
    // peer can read what this machine sends - and it expires after `RouteSeconds`, at which point the peer is
    // unknown again and receives the packet both ways until it answers. Without the expiry one unusable
    // packet was enough to lock a pair onto a transport that was delivering nothing, for the rest of the run,
    // with no line in the log saying so.
    private readonly struct RouteEntry
    {
        internal readonly bool ViaApi;
        internal readonly float Learned;
        internal RouteEntry(bool viaApi, float learned) { ViaApi = viaApi; Learned = learned; }
    }
    private const float RouteSeconds = 10f;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly ConcurrentDictionary<ulong, RouteEntry> Route = new ConcurrentDictionary<ulong, RouteEntry>();

    // Seconds since this plugin loaded. Only ordering and elapsed time are read from it, never a wall clock.
    private static float Seconds => (float)Clock.Elapsed.TotalSeconds;

    // Whether this peer's route is known and still fresh. A stale entry is dropped rather than trusted, which
    // is what puts an unknown peer back on both transports for one packet.
    private static bool TryRoute(ulong lookup, out bool viaApi)
    {
        viaApi = false;
        if (lookup == 0UL) return false;
        if (!Route.TryGetValue(lookup, out RouteEntry entry)) return false;
        if (Seconds - entry.Learned > RouteSeconds) { Route.TryRemove(lookup, out _); return false; }
        viaApi = entry.ViaApi;
        return true;
    }

    // The route a peer is currently held to, for the headless suite: true = GTFO-API, false = the game's own
    // byte channel, and null when nothing usable has been heard from that peer lately.
    internal static bool? RouteOf(ulong lookup) => TryRoute(lookup, out bool viaApi) ? viaApi : (bool?)null;

    // A new expedition, or a new lobby: what was learned about who answers on which transport described the last
    // session and not this one. Forgetting it puts every peer back to "unknown", which is the state that sends
    // both ways and therefore cannot be wrong about a peer whose route changed with the session. The probe's
    // channel rotation described the last session too, so it starts over with the same breadth.
    internal static void ResetRoutes()
    {
        Route.Clear();
        ProbePackets.Clear();
    }

    // How many probe packets this machine has handed to a transport for each peer, which is what decides which of
    // the two compatible channels the next one buys: `SquadCapability.ProbeBudget` counts *physical* packets, so the
    // credits rotate - the preferred transport first, the game's own channel second, and so on - and both channels
    // are exercised inside the budget even when the budget is small. Transport state, so it lives here: the
    // capability decides how many packets a peer may cost, this decides where each of them goes.
    private static readonly Dictionary<ulong, int> ProbePackets = new Dictionary<ulong, int>();
    private static int _queued, _unusable;
    private static readonly HashSet<string> UnusableReported = new HashSet<string>();
    private static bool _loggedOverflow, _loggedReadError, _loggedWithheld;

    // How many packets of this build's protocol were consumed here but could not be used - the shape the
    // previous release produced no diagnostic for at all, which is why "nothing is shared" could not be told
    // apart from "the packets arrive and are dropped".
    internal static int UnusablePackets => _unusable;

    // ---------------------------------------------------------------- the GTFO-API transport

    // The event name this build registers on GTFO-API. It is a name, not a version: the protocol version
    // travels inside the packet, so two builds on either side of an update still greet each other and say
    // which version they speak rather than silently disagreeing about an event name.
    private const string ApiEvent = "CombatPanelSquad";
    private static System.Reflection.MethodInfo _apiSend;
    private static bool _apiBound, _apiDisabled;
    private static int _apiFailures, _apiAttempts;
    private static object _apiChannel;
    private static bool _loggedApiBound, _loggedApiAbsent, _loggedApiFailed;
    // Frames to wait between binding attempts. The API's assembly is loaded by the chain loader, and which of
    // the two plugins is loaded first is not something this build can choose, so a first attempt that found
    // nothing is retried from the main loop rather than written off for the session.
    private static int _bindCooldown;

    // Whether the API transport is the one this machine will send on. Read by the arm diagnostic and by the
    // retry in `CombatSync.Tick`.
    internal static bool ApiBound => _apiBound;

    // Binds the API if this machine loads it. Everything is done reflectively: the plugin keeps no compile-time
    // reference to GTFO-API, so a profile without it still loads this build and falls back to the game channel.
    // Registering before the API is ready is safe - it caches the registration and replays it when its own
    // component wakes - which matters because plugin load order is not ours to choose.
    internal static bool TryBindApi()
    {
        if (_apiBound) return true;
        if (_apiDisabled) return false;
        if (_bindCooldown > 0) { _bindCooldown--; return false; }
        _bindCooldown = 60;
        try
        {
            Type api = null;
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidate = assembly.GetType("GTFO.API.NetworkAPI", false);
                if (candidate != null) { api = candidate; break; }
            }
            if (api == null)
            {
                if (!_loggedApiAbsent)
                {
                    _loggedApiAbsent = true;
                    Plugin.Logger?.LogInfo("[DIAG] squad sync transport: GTFO-API is not loaded on this machine, so the exchange uses the game's own byte channel through SNet.Core.SendBytes.");
                }
                return false;
            }
            System.Reflection.MethodInfo register = null, send = null;
            foreach (System.Reflection.MethodInfo method in api.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                // The generic overloads carry a payload type; the free-sized pair is not generic and is the one
                // this build can bind without inventing a struct with a native pointer store.
                if (method.IsGenericMethodDefinition) continue;
                System.Reflection.ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == "RegisterFreeSizedEvent" && parameters.Length == 2
                    && parameters[0].ParameterType == typeof(string)) register = method;
                else if (method.Name == "InvokeFreeSizedEvent" && parameters.Length == 4
                    && parameters[0].ParameterType == typeof(string)
                    && parameters[1].ParameterType == typeof(byte[])
                    && parameters[2].ParameterType.Name == "SNet_Player") send = method;
            }
            if (register == null || send == null)
            {
                if (!_loggedApiAbsent)
                {
                    _loggedApiAbsent = true;
                    Plugin.Logger?.LogWarning("[DIAG] squad sync transport: GTFO-API is loaded but does not expose the free-sized event pair this build binds to, so the exchange stays on the game's own byte channel.");
                }
                return false;
            }
            System.Reflection.MethodInfo handler = typeof(SquadNet).GetMethod(nameof(OnApiPayload),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (handler == null) return false;
            Delegate callback = Delegate.CreateDelegate(register.GetParameters()[1].ParameterType, handler);
            register.Invoke(null, new object[] { ApiEvent, callback });
            _apiChannel = Enum.Parse(send.GetParameters()[3].ParameterType, "GameOrderCritical");
            _apiSend = send;
            _apiBound = true;
            if (!_loggedApiBound)
            {
                _loggedApiBound = true;
                Plugin.Logger?.LogInfo("[DIAG] squad sync transport: bound to GTFO-API's free-sized event '" + ApiEvent + "', which every other mod on that transport already broadcasts on; packets carry the API's own key, magic and version signature.");
            }
            return true;
        }
        catch (Exception error)
        {
            // Not fatal and not final. A registration that failed because the API's own component had not woken
            // yet - or because the game had not finished loading - succeeds on a later attempt, so the bind is
            // retried at a slow cadence instead of being written off for the session. Only a bind that keeps
            // failing is retired, and the game's own byte channel carries the exchange meanwhile either way.
            _apiAttempts++;
            if (_apiAttempts >= 20) _apiDisabled = true;
            _bindCooldown = 600;
            if (!_loggedApiAbsent)
            {
                _loggedApiAbsent = true;
                Plugin.Logger?.LogWarning("[DIAG] squad sync transport: GTFO-API could not be bound on the first attempt and the exchange uses the game's own byte channel meanwhile; the bind is retried slowly. "
                    + (error.InnerException ?? error).GetType().Name + ": " + (error.InnerException ?? error).Message);
            }
            return false;
        }
    }

    private static bool TryApiSend(byte[] packet, SNet_Player target)
    {
        if (!_apiBound || _apiDisabled || _apiSend == null) return false;
        try
        {
            // The channel value is resolved once, when the method is bound: an enum member that this build's game
            // does not have would otherwise be re-parsed on every packet, and the failures it produced would be
            // counted against the transport rather than against the parse.
            _apiSend.Invoke(null, new object[] { ApiEvent, packet, target, _apiChannel });
            // A send that worked is proof the API is serving this machine, so the strike count starts again. The
            // latch is on *consecutive* failures: three isolated hiccups - a target destroyed between the roster
            // read and the invoke, a lobby teardown - must not retire a transport that is working.
            _apiFailures = 0;
            return true;
        }
        catch (Exception error)
        {
            _apiFailures++;
            if (_apiFailures >= 5 && !_apiDisabled)
            {
                _apiDisabled = true;
                if (!_loggedApiFailed)
                {
                    _loggedApiFailed = true;
                    Plugin.Logger?.LogWarning("[DIAG] squad sync transport: five consecutive GTFO-API sends failed, so this machine fell back to the game's own byte channel for the rest of the session: "
                        + (error.InnerException ?? error).GetType().Name + ": " + (error.InnerException ?? error).Message);
                }
            }
            return false;
        }
    }

    // Called by GTFO-API on whichever thread it received the event on. Only parsing happens here; the decoded
    // packet is parked for the main thread exactly as the game-channel path parks its own.
    private static void OnApiPayload(ulong sender, byte[] payload)
    {
        try
        {
            if (payload == null || payload.Length == 0) return;
            TryAcceptApiPayload(sender, payload, payload.Length);
        }
        catch (Exception error)
        {
            if (!_loggedReadError)
            {
                _loggedReadError = true;
                Plugin.Logger?.LogWarning("a squad packet received over GTFO-API could not be read and was dropped: " + error.Message);
            }
        }
    }

    // The GTFO-API receive path, separated from the callback so the headless suite can drive it with the
    // payload shape that transport actually produces.
    //
    // GTFO-API cannot tell its handler how long the packet is: `HandlePacket` derives the payload from
    // `packetData.Length - HeaderSize`, and `packetData` is the array the game's replication layer cached,
    // which is at least as long as the packet and often longer - the same fact the raw channel is read
    // against `size` for. So the payload handed here may end in stale bytes, and the packet is read as the
    // length its own envelope declares. Nothing past that length is ever read.
    //
    // `length` is whatever the caller knows the payload to be; zero or less means "all of it".
    internal static bool TryAcceptApiPayload(ulong sender, byte[] payload, int length)
    {
        if (payload == null || payload.Length == 0) return false;
        int available = length <= 0 || length > payload.Length ? payload.Length : length;
        int framed = SquadPackets.FramedLength(payload, available);
        // A payload with no complete header of ours is parsed as it stands, so it stays `Malformed` - counted
        // and reported - instead of being silently ignored or guessed at.
        SquadPacket packet = SquadPackets.Parse(payload, framed > 0 ? framed : available);
        if (!packet.IsOurs) return false;
        Enqueue(packet, sender, true);
        return true;
    }

    internal static void SendHello(SNet_Player target, ulong owner, uint generation)
    {
        if (target == null) return;
        Send(SquadPacketKind.Hello, SquadPackets.Hello(owner, generation, SyncProtocol.Version), target);
    }

    internal static void SendSnapshot(SNet_Player target, in SyncSnapshot snapshot, uint generation)
    {
        if (target == null) return;
        Send(SquadPacketKind.Snapshot, SquadPackets.Snapshot(snapshot, generation, SyncProtocol.Version), target);
    }

    // 1.19.34: one packet beside the row, for peers that can read it. Nothing about the row's own packet changes.
    internal static void SendSupplyKinds(SNet_Player target, in SyncProtocol.SupplyKinds kinds, uint generation)
    {
        if (target == null) return;
        Send(SquadPacketKind.SupplyKinds, SquadPackets.SupplyKinds(kinds, generation, SyncProtocol.Version), target);
    }

    // The packet's own kind travels with it, because the gate below answers per kind: the bounded probe pays for a
    // greeting and for nothing else, and the transport is the last place that can be enforced when the caller is
    // not the exchange's own queue.
    private static void Send(SquadPacketKind kind, byte[] packet, SNet_Player target)
    {
        if (packet == null) return;
        if (!MaySpeakTo(kind, target, out SquadEvidence why))
        {
            if (!_loggedWithheld)
            {
                _loggedWithheld = true;
                Plugin.Logger?.LogWarning("a squad packet was withheld: this machine has decided to stay silent with that peer - it speaks another protocol version, its bounded probe is spent, sharing is restricted to named and proven players and it is neither, or the packet was a row for a peer that has not proved it can read one (the bounded probe pays for greetings only) - so nothing is put on the wire for it.");
            }
            return;
        }
        ulong lookup = 0UL;
        try { lookup = target.Lookup; } catch (Exception) { }
        // A probe credit is one packet on one transport: `ProbeBudget` is what the other machine's log pays for, and
        // it pays per packet, not per logical send. Doing this before the two-way rule below is what stops an
        // unproven peer from being charged three and handed six.
        if (why == SquadEvidence.Probe)
        {
            SendProbe(packet, target, lookup);
            return;
        }
        // Two transports, one packet shape. GTFO-API's free-sized event is the one the rest of the scene shares
        // on and the one this build prefers: its bytes carry the API's own key, magic and version signature. A
        // machine that runs no mod does not drop them quietly - the API writes one error line into that machine's
        // own log for each packet - so this build only ever puts a bounded number of them on that route for a peer
        // that has not answered. The game's own byte channel carries the same packet behind this build's own key
        // and magic and is understood by every machine that runs this mod, whether or not it also runs the API.
        //
        // Which one a peer answers on is a fact this machine learns rather than assumes: a build with the API
        // bound and a build without it would otherwise talk past each other in one direction for the whole run.
        // Until a peer's route is known the packet goes out both ways; once a packet of that peer's has been
        // read here, only the route it actually used is kept, so the duplicate costs one packet per peer rather
        // than one per second. That memory expires (`TryRoute`): a transport that stops delivering puts the
        // peer back to "unknown" within `RouteSeconds` instead of silencing the pair for the rest of the run.
        //
        // This path is for a peer that proved it runs this build or that the operator named: nothing here is
        // budgeted, so the duplicate is the route discovery it always was.
        bool known = TryRoute(lookup, out bool viaApi);
        bool knownApi = known && viaApi;
        bool knownRaw = known && !viaApi;
        // Raw goes out unless this peer is known to answer on the API alone. An unknown peer gets both, because
        // the one packet that proves which route it uses is the one that arrives - and a peer known to be on the
        // API still gets the raw copy when the API send did not happen, because a transport that has retired
        // itself must never be the reason a squad goes silent.
        bool apiSent = !knownRaw && TryApiSend(packet, target);
        if (!knownApi || !apiSent) SendRaw(packet, target);
    }

    // One probe credit, one physical packet. The two compatible channels are rotated - the preferred transport on
    // the first greeting, the game's own channel on the second, and so on - so a budget of `ProbeBudget` packets
    // covers both without ever costing that peer more than the budget. A credit is spent even when its transport
    // fails: the next greeting on the cadence buys the other channel rather than sending a second copy now, which is
    // what keeps a failed send from turning one credit into two packets.
    private static void SendProbe(byte[] packet, SNet_Player target, ulong lookup)
    {
        ProbePackets.TryGetValue(lookup, out int sent);
        sent++;
        if (lookup != 0UL) ProbePackets[lookup] = sent;
        bool known = TryRoute(lookup, out bool routeApi);
        // A peer whose route is known is sent on that route and no other; a probe normally has no route at all,
        // because anything this machine heard from a peer proves it and takes it off the probe.
        bool viaApi = known ? routeApi : (sent % 2) == 1;
        if (viaApi && ApiUsable()) { TryApiSend(packet, target); return; }
        SendRaw(packet, target);
    }

    // Whether the GTFO-API transport can carry a packet on this machine right now. A probe credit assigned to the
    // preferred transport falls back to the game's own channel when that transport is not bound at all: that is not
    // duplication, it is the only channel there is. A transport that is bound but fails is a different matter and
    // `SendProbe` does not retry it on the other channel.
    private static bool ApiUsable() { return _apiBound && !_apiDisabled && _apiSend != null; }

    // The game's own byte channel: a run of bytes behind this build's key and magic, taken back in the
    // `SNet_Replication.RecieveBytes` prefix.
    private static void SendRaw(byte[] packet, SNet_Player target)
    {
        var channelType = SNet_ChannelType.GameOrderCritical;      // ordered: a snapshot must not overtake the one before it
        SNet.GetSendSettings(ref channelType, out SNet_SendGroup _, out SNet_SendQuality quality, out int channel);
        Il2CppStructArray<byte> bytes = packet;
        SNet.Core.SendBytes(bytes, quality, channel, target);
    }

    // Fail closed: no gate, an unreadable target or a throwing owner all mean "not a byte". The kind travels with
    // the question so the owner can answer per kind - which is what keeps the bounded probe from paying for a row -
    // and the answer comes back as the evidence that allowed it, because a probe credit buys one physical packet
    // while a proved or named peer's packet may go out both ways.
    private static bool MaySpeakTo(SquadPacketKind kind, SNet_Player target, out SquadEvidence why)
    {
        why = SquadEvidence.Nothing;
        try
        {
            Func<ulong, SquadPacketKind, SquadEvidence> gate = MaySend;
            if (gate == null || target == null) return false;
            why = gate(target.Lookup, kind);
            return why != SquadEvidence.Nothing;
        }
        catch (Exception)
        {
            why = SquadEvidence.Nothing;
            return false;
        }
    }

    // Called from the receive prefix, on whatever thread the replication layer uses. True means the packet
    // was ours and is consumed; false means it belongs to the game (or to another mod) and is passed on
    // untouched, with its own arguments and its own execution path.
    //
    // The claim is precise: only a packet whose key and magic have both been read as this mod's is taken
    // out of the game's hands. Anything that fails to parse for any other reason - including an exception
    // thrown while the array is being read - is left exactly as the game handed it over, because a packet
    // this mod is not sure about must never be swallowed on somebody else's behalf.
    internal static bool TryAccept(Il2CppStructArray<byte> bytes, uint size, ulong sender)
    {
        bool claimed = false;
        try
        {
            if (bytes == null) return false;
            byte[] buffer = bytes;
            // The declared size is what the network layer actually received; the backing array is a cache and
            // may be larger, so every bound is taken from the size and never from buffer.Length.
            int declared = size > int.MaxValue ? int.MaxValue : (int)size;
            SquadPacket packet = SquadPackets.Parse(buffer, declared);
            if (!packet.IsOurs) return false;
            // Ours, so it is consumed here whatever happens next: handing a half-read packet to the game
            // would make it look for a replicator that does not exist.
            claimed = true;
            Enqueue(packet, sender, false);
            return true;
        }
        catch (Exception error)
        {
            // A packet that announced itself as ours but blew up while being read is still consumed. Only a
            // reference to the exception message is kept; no game state is touched here, and nothing is
            // rethrown into the replication layer's stack.
            if (!_loggedReadError)
            {
                _loggedReadError = true;
                Plugin.Logger?.LogWarning("a squad packet could not be read; " + (claimed
                    ? "it was consumed rather than handed to the game: "
                    : "it was left to the game untouched: ") + error.Message);
            }
            return claimed;
        }
    }

    private static void Enqueue(in SquadPacket packet, ulong sender, bool viaApi)
    {
        if (packet.Kind == SquadPacketKind.Foreign) return;
        // Which transport this peer actually answered on - and only when the packet decoded. A packet the
        // receive path had to refuse (`Malformed`, `Unknown`) still proves the peer is running something that
        // speaks this key and magic, but it proves nothing about which transport can carry a packet this
        // build can read: remembering it as "this peer is on the API" is how one unusable packet switched the
        // working route off for the whole run.
        // 1.19.34: a kinds packet is a readable packet of this build too, so it proves the route exactly as a row does.
        if (sender != 0UL && (packet.Kind == SquadPacketKind.Hello || packet.Kind == SquadPacketKind.Snapshot
            || packet.Kind == SquadPacketKind.SupplyKinds))
            Route[sender] = new RouteEntry(viaApi, Seconds);
        // Bounded, and bounded by dropping the oldest: the counter is raised once and lowered once per drop,
        // so a stalled main thread cannot grow the queue without limit and the queue cannot be emptied by the
        // very act of noticing that it is full.
        int queued = Interlocked.Increment(ref _queued);
        while (queued > MaxQueue && Queue.TryDequeue(out _))
        {
            queued = Interlocked.Decrement(ref _queued);
            if (!_loggedOverflow)
            {
                _loggedOverflow = true;
                Plugin.Logger?.LogWarning("[DIAG] the squad packet queue is full and the oldest packet was dropped; the queue is bounded so a stalled main thread cannot grow it without limit.");
            }
        }
        Queue.Enqueue(new Queued
        {
            // Transport identity is not the body identity: a host relays rows owned by other players,
            // and snapshot packets intentionally carry no sender field (Parse leaves it zero).
            Kind = packet.Kind, Sender = sender, HelloOwner = packet.Sender, Generation = packet.Generation,
            Version = packet.Version, Snapshot = packet.Snapshot, Kinds = packet.Kinds, ViaApi = viaApi
        });
    }

    // Drains the queue. Called from the main thread by CombatSync.Tick, so every handler it invokes may
    // touch game state. Each packet is guarded on its own: one bad row cannot stop the rest.
    internal static void Drain()
    {
        int budget = MaxQueue;
        while (budget-- > 0 && Queue.TryDequeue(out Queued queued))
        {
            Interlocked.Decrement(ref _queued);
            try
            {
                switch (queued.Kind)
                {
                    case SquadPacketKind.Hello:
                        OnHello?.Invoke(queued.Sender, queued.Generation, queued.HelloOwner);
                        break;
                    case SquadPacketKind.Snapshot:
                        OnSnapshot?.Invoke(queued.Sender, queued.Generation, queued.Snapshot);
                        break;
                    // 1.19.34: the split that rode beside the row. A machine that does not send it leaves every row's
                    // kinds unknown, which is what this build prints for it.
                    case SquadPacketKind.SupplyKinds:
                        OnSupplyKinds?.Invoke(queued.Sender, queued.Generation, queued.Kinds);
                        break;
                    case SquadPacketKind.VersionMismatch:
                        OnProtocolMismatch?.Invoke(queued.Sender, queued.Version);
                        break;
                    default:
                        // Ours, consumed, and unusable. Silent until now: the packet left the game's hands and
                        // nothing said so, which is why "the squad exchanges nothing" could not be told apart
                        // from "the packets arrive in a shape this build cannot read".
                        ReportUnusable(queued);
                        break;
                }
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("a queued squad packet could not be applied and was skipped: " + error.Message);
            }
        }
    }

    // A packet of ours that arrived and could not be used. One line per shape and transport, never one per
    // packet: a lobby where every peer's packet lands in the wrong shape would otherwise write a line a
    // second for the whole run. The line names the figures the diagnosis needs - which transport, which
    // sender, what this build expected and what it was handed - because those are the only facts that tell a
    // padded payload apart from a peer on another build.
    private static void ReportUnusable(in Queued queued)
    {
        _unusable++;
        string transport = queued.ViaApi ? "GTFO-API free-sized event" : "SNet.Core.SendBytes";
        string key = queued.Kind + "/" + transport;
        if (!UnusableReported.Add(key)) return;
        Plugin.Logger?.LogWarning("[DIAG] squad sync receive: a packet of this build's protocol was consumed here and could not be used; kind="
            + queued.Kind + "; transport=" + transport + "; peer=" + queued.Sender + "; generation=" + queued.Generation
            + "; this build reads " + SquadPackets.HelloBytes + "-byte hellos and " + SquadPackets.SnapshotBytes
            + "-byte rows; the packet is dropped rather than handed to the game. This line is written once per shape and transport.");
    }

    internal static int QueuedCount { get { return _queued; } }
}

// Every packet the network layer delivers passes here. Ours are consumed before the game can try to
// match them to a replicator; everything else is handed straight back with `true`.
//
// Priority.High, because this method is crowded: the client log this release was cut from shows four
// other prefixes on it (GTFO-API's network API, the AWO replicators, AddEvents, DamageSync). A prefix
// that returns false for a packet it does not recognise - or that throws before returning - takes the
// bytes out of everyone behind it, and until now this reader could be last in that queue. Reading first
// means no other mod can swallow this mod's packets before they are seen.
[HarmonyPatch(typeof(SNet_Replication), "RecieveBytes")]
internal static class SNetReplicationRecieveBytesPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyWrapSafe]
    private static bool Prefix(Il2CppStructArray<byte> bytes, uint size, ulong messagerID)
        => !SquadNet.TryAccept(bytes, size, messagerID);
}
