// The bounded probe measured on the one number that matters to the other machine: how many packets of this
// build's protocol actually left this one.
//
// Every earlier measurement of the probe stopped at `SquadExchange.Tick` - it counted the packets the exchange
// *asked for*. That is exactly the seam the defect lived in: `Tick` charged the third greeting to
// `SquadCapability` before the packet was handed over, the transport's own gate then asked the same counter
// ("is there evidence?") and answered `Nothing`, so the third of the three packets was silently withheld while
// the exchange had already counted it. A suite that stops at the queue cannot see that, and a suite that
// re-implements the gate cannot fail. So nothing below inspects the exchange's intent: every packet it asks for
// goes through the production transport - `SquadNet.SendHello` / `SendSnapshot` / `SendSupplyKinds`, with
// `SquadNet.MaySend` pointing at the very gate `CombatSync` installs (`SquadCapability.MayPutOnWire`) - and the
// assertions are made on `SNet.Core.SendCount`, the stub that stands where `SNet.Core.SendBytes` is.
//
// What is asserted: three packets really leave for an unproven peer and the fourth is refused, no row ever goes
// to it, another peer's budget is its own, proofs and state changes behave, and a transport that refuses
// everything can neither refund nor multiply a reserved greeting.
using System;
using System.Collections.Generic;
using HawChow.GTFOCombatPanel;
using System.IO;
using SNetwork;

internal static partial class Program
{
    private static void RunSquadProbeWireGateScenarios()
    {
        SquadNet.ResetRoutes();

        // ---- 1: an unproven peer on the real send path ------------------------------------------------
        const ulong local = 0x5101UL, silent = 0x5102UL, other = 0x5103UL;
        var rig = new ProbeRig(local, true, silent, other);
        rig.Exchange.Capability.SetBroadcast(true);
        rig.ArmGate();
        for (int tick = 0; tick < 600; tick++) rig.Pump(tick * 1f);          // ten simulated minutes
        Console.WriteLine("PROBE-WIRE-GATE: an unmodded peer received " + rig.Packets(silent) + " packet(s) through"
            + " the real transport gate (" + rig.Hellos(silent) + " greeting(s), " + rig.RowsTo(silent) + " row(s))");
        Check(rig.Packets(silent) == SquadCapability.ProbeBudget,
            "PG1: an unproven peer is handed exactly ProbeBudget (" + SquadCapability.ProbeBudget
            + ") packets that reach SNet.Core.SendBytes, not one fewer and not one more (got " + rig.Packets(silent)
            + " - two of three is what the gate did while the exchange had already counted the third)");
        Check(rig.Hellos(silent) == SquadCapability.ProbeBudget && rig.RowsTo(silent) == 0 && rig.Kinds(silent) == 0,
            "PG2: all of them are greetings - not one row and not one pack-kind block is put on the wire for a peer that has not proved it can read one");
        Check(rig.Exchange.Capability.ProbesSent(silent) == SquadCapability.ProbeBudget
            && rig.Exchange.Capability.ProbesDelivered(silent) == SquadCapability.ProbeBudget,
            "PG3: the budget the exchange charged and the packets the transport handed over are the same figure, which is the defect this round fixes");
        Check(rig.Packets(other) == SquadCapability.ProbeBudget
            && rig.Exchange.Capability.ProbesDelivered(other) == SquadCapability.ProbeBudget,
            "PG4: the second unproven peer costs its own budget and does not borrow the first one's, so per-peer accounting survives multiple targets (got "
            + rig.Packets(other) + ")");

        // ---- 2: the fourth packet of every kind is refused at the transport --------------------------
        SNet_Player silentPlayer = rig.Player(silent);
        int settled = SNet.Core.SendCount;
        SquadNet.SendHello(silentPlayer, local, rig.Exchange.Generation);
        SquadNet.SendSnapshot(silentPlayer, default, rig.Exchange.Generation);
        SquadNet.SendSupplyKinds(silentPlayer, default, rig.Exchange.Generation);
        Check(SNet.Core.SendCount == settled,
            "PG5: once the budget is spent the transport refuses the fourth greeting AND a row AND a pack-kind block for that peer - an unproven peer is never handed data");
        for (int attempt = 0; attempt < 10; attempt++) rig.Exchange.Capability.NoteProbeSent(silent);
        for (int attempt = 0; attempt < 10; attempt++) SquadNet.SendHello(silentPlayer, local, rig.Exchange.Generation);
        Check(SNet.Core.SendCount == settled && rig.Exchange.Capability.ProbesSent(silent) == SquadCapability.ProbeBudget,
            "PG6: asking the exchange to charge the peer again does not refill the budget, and repeating the send does not put a fourth packet on the wire");
        bool askedForMore = false;
        for (int tick = 600; tick < 900; tick++)
            foreach (SquadSend send in rig.Exchange.Tick(tick * 1f, rig.Store))
                if (send.Target == silent || send.Target == other) askedForMore = true;
        Check(!askedForMore,
            "PG7: and the exchange itself stops asking for that peer, so the queue is bounded by the same budget the transport enforces");

        // ---- 3: what the states do to the budget ----------------------------------------------------
        rig.Exchange.ResetForExpedition();                                    // a new expedition
        int beforeExpedition = rig.Packets(silent);
        for (int tick = 0; tick < 600; tick++) rig.Pump(tick * 1f);
        Check(rig.Packets(silent) - beforeExpedition == SquadCapability.ProbeBudget,
            "PG8: a new expedition renews the probe and is exactly as bounded - ProbeBudget more packets and then silence (got "
            + (rig.Packets(silent) - beforeExpedition) + ")");
        rig.Exchange.Capability.Forget(silent);                               // the seat changed hands
        int beforeSeat = rig.Packets(silent);
        for (int tick = 0; tick < 600; tick++) rig.Pump(tick * 1f);
        Check(rig.Packets(silent) - beforeSeat == SquadCapability.ProbeBudget,
            "PG9: the seat changing hands gives the new occupant its own bounded probe, never the previous occupant's spent one (got "
            + (rig.Packets(silent) - beforeSeat) + ")");

        // ---- 4: a peer that answers is served, and only it -------------------------------------------
        var answerRig = new ProbeRig(local + 0x10, true, silent, other);
        answerRig.Exchange.Capability.SetBroadcast(true);
        answerRig.ArmGate();
        for (int tick = 0; tick < 10; tick++) answerRig.Pump(tick * 1f);
        bool answered = false;
        SquadDecision hello = answerRig.Exchange.OnHello(silent, answerRig.Exchange.Generation, 10f, out answered);
        int rowsBefore = answerRig.RowsTo(silent);
        for (int tick = 10; tick < 70; tick++) answerRig.Pump(tick * 1f);
        Check(hello.Accepted && answerRig.Exchange.Capability.EvidenceOf(silent) == SquadEvidence.Proven,
            "PG10: a peer that sends this machine a packet of this build's protocol is proved, and proof outranks the probe");
        Check(answerRig.RowsTo(silent) > rowsBefore,
            "PG11: and it starts receiving rows through the same gate the probe was refused by (got "
            + (answerRig.RowsTo(silent) - rowsBefore) + " row(s) in a minute)");
        Check(answerRig.Packets(other) == SquadCapability.ProbeBudget,
            "PG12: while the other peer, which never answers, is still capped at its own ProbeBudget packet(s) over the same minute (got "
            + answerRig.Packets(other) + ")");

        // ---- 5: a transport that refuses everything grants nothing and refunds nothing ----------------
        var failedRig = new ProbeRig(local + 0x20, true, silent);
        failedRig.Exchange.Capability.SetBroadcast(true);
        SquadNet.MaySend = null;                                              // not armed, or the gate is broken
        for (int tick = 0; tick < 120; tick++) failedRig.Pump(tick * 1f);
        Check(failedRig.Packets(silent) == 0 && failedRig.Exchange.Capability.ProbesDelivered(silent) == 0,
            "PG13: a transport whose gate refuses everything puts nothing on the wire, whatever the exchange decided");
        Check(failedRig.Exchange.Capability.ProbesSent(silent) == SquadCapability.ProbeBudget,
            "PG14: the packets the exchange decided on are still charged, so a transport failure cannot hand the peer a fresh budget");
        failedRig.ArmGate();                                                  // the transport becomes usable
        int redeemed = 0;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int before = SNet.Core.SendCount;
            SquadNet.SendHello(failedRig.Player(silent), local, failedRig.Exchange.Generation);
            if (SNet.Core.SendCount > before) redeemed += SNet.Core.SendCount - before;
        }
        Check(redeemed + failedRig.Packets(silent) <= SquadCapability.ProbeBudget,
            "PG15: the reserved-but-undelivered greetings can still be handed over, and the total the peer can ever receive stays inside ProbeBudget (got "
            + (redeemed + failedRig.Packets(silent)) + " over the whole run)");

        // ---- 6: a peer on another protocol is silent even with a reservation outstanding --------------
        var mismatchRig = new ProbeRig(local + 0x30, true, silent);
        mismatchRig.Exchange.Capability.SetBroadcast(true);
        mismatchRig.ArmGate();
        mismatchRig.Pump(0f);
        mismatchRig.Exchange.OnPeerIncompatible(silent);
        int mismatchBaseline = mismatchRig.Packets(silent);
        for (int tick = 1; tick < 60; tick++) mismatchRig.Pump(tick * 1f);
        Check(mismatchRig.Exchange.Capability.MayPutOnWire(silent, SquadPacketKind.Hello) == SquadEvidence.Nothing
            && mismatchRig.Packets(silent) == mismatchBaseline,
            "PG16: a peer that answered with a protocol this build does not speak is sent nothing even though a greeting was still reserved for it");

        // ---- 7: switching sharing off owes an unproven peer nothing ----------------------------------
        var switchRig = new ProbeRig(local + 0x40, true, silent);
        switchRig.Exchange.Capability.SetBroadcast(true);
        switchRig.ArmGate();
        switchRig.Pump(0f);
        switchRig.Exchange.Capability.SetBroadcast(false);
        int switchedOff = switchRig.Packets(silent);
        for (int tick = 1; tick < 60; tick++) switchRig.Pump(tick * 1f);
        Check(switchRig.Packets(silent) == switchedOff
            && switchRig.Exchange.Capability.ProbesSent(silent) == 0,
            "PG17: turning sharing off clears the reserved probe and sends nothing more, so the switch is not a way past the budget");

        // ---- 8: a greeting that is reserved but not yet sent must not pay for data --------------------
        // This is the seam the previous round left open. The budget is charged where the greeting is decided and
        // handed over at the gate, so between those two moments this machine holds a reserved greeting for a peer
        // it has still not spoken to. A row or a pack-kind block reaching the transport in that window has to be
        // refused on its own account: not merely because the exchange's queue happens not to ask for one, and
        // never at the price of the greeting it is not part of.
        var kindRig = new ProbeRig(local + 0x50, true, silent);
        kindRig.Exchange.Capability.SetBroadcast(true);
        SquadNet.MaySend = null;                        // decided but not handed over: the transport is not armed
        kindRig.Pump(0f);
        Check(kindRig.Packets(silent) == 0
            && kindRig.Exchange.Capability.ProbesSent(silent) == 1
            && kindRig.Exchange.Capability.ProbesDelivered(silent) == 0,
            "PG18: the exchange decides on one greeting for an unproven peer and the unarmed transport hands over none, so exactly one reservation is outstanding");
        kindRig.ArmGate();
        SNet_Player kindPlayer = kindRig.Player(silent);
        int kindWire = SNet.Core.SendCount;
        SquadNet.SendSnapshot(kindPlayer, default, kindRig.Exchange.Generation);
        SquadNet.SendSupplyKinds(kindPlayer, default, kindRig.Exchange.Generation);
        Check(SNet.Core.SendCount == kindWire,
            "PG19: a row and a pack-kind block for a peer that has not proved it can read one are refused by the transport even while a greeting is still reserved for it - data is proven-or-named, not \"possible once a probe is pending\"");
        Check(kindRig.Exchange.Capability.ProbesDelivered(silent) == 0
            && kindRig.Exchange.Capability.ProbesSent(silent) == 1,
            "PG20: the refused data did not spend the greeting's reservation, so the probe is neither shortened nor lengthened by it");
        SquadNet.SendHello(kindPlayer, local, kindRig.Exchange.Generation);
        Check(SNet.Core.SendCount == kindWire + 1 && kindRig.Exchange.Capability.ProbesDelivered(silent) == 1,
            "PG21: the reserved greeting is then handed over by that same gate, exactly once, and still costs exactly one probe");
        SquadNet.SendSnapshot(kindPlayer, default, kindRig.Exchange.Generation);
        Check(SNet.Core.SendCount == kindWire + 1,
            "PG22: and a row for that peer is still refused after the greeting went out, because delivering a probe buys no data channel");

        // ---- 9: proved and named peers keep their rows, whatever the probe says ----------------------
        const ulong proven = 0x5104UL, named = 0x5105UL;
        var dataRig = new ProbeRig(local + 0x60, true, proven, named);
        dataRig.Exchange.Capability.SetBroadcast(true);
        dataRig.ArmGate();
        dataRig.Exchange.Capability.NoteInbound(proven);
        dataRig.Exchange.SetAllowlist(new List<ulong> { named });
        int dataWire = SNet.Core.SendCount;
        SquadNet.SendSnapshot(dataRig.Player(proven), default, dataRig.Exchange.Generation);
        SquadNet.SendSnapshot(dataRig.Player(named), default, dataRig.Exchange.Generation);
        SquadNet.SendSupplyKinds(dataRig.Player(proven), default, dataRig.Exchange.Generation);
        SquadNet.SendSupplyKinds(dataRig.Player(named), default, dataRig.Exchange.Generation);
        Check(SNet.Core.SendCount == dataWire + 4,
            "PG23: a row and a pack-kind block still reach a peer that proved it runs this build and one the operator named - the kind rule must narrow the unproven case only, never the channel");
        Check(dataRig.Exchange.Capability.ProbesSent(proven) == 0 && dataRig.Exchange.Capability.ProbesSent(named) == 0,
            "PG24: and neither of them was charged a probe, so serving a proved or named peer costs the budget nothing");

        // ---- 10: the production wiring, as written in `CombatSync` ----------------------------------
        // A kind-aware gate is only kind-aware if the runtime hands it the kind, and the runtime half cannot be
        // loaded here (Unity, SNet and Harmony are absent), so this is a source check on the exact wiring - the
        // same shape the rest of this suite uses for code it cannot execute. It fails if the transport ever goes
        // back to a lookup-only gate, which is what let a row through for an unproven peer.
        string combatSync = ProbeSource("CombatSync.cs");
        string squadNet = ProbeSource("SquadNet.cs");
        Console.WriteLine("WIRING: SQUAD_PROBE_KIND_GATE (static source check; not a runtime behaviour test)");
        Check(combatSync != null && combatSync.Contains("SquadNet.MaySend = MayBeSpokenTo;")
            && combatSync.Contains("private static SquadEvidence MayBeSpokenTo(ulong lookup, SquadPacketKind kind)")
            && combatSync.Contains("Exchange.Capability.MayPutOnWire(lookup, kind)"),
            "PG25: the runtime wires the transport's gate to `MayPutOnWire(lookup, kind)`, so every packet asks with the kind it is about to carry");
        Check(squadNet != null && squadNet.Contains("internal static Func<ulong, SquadPacketKind, SquadEvidence> MaySend;"),
            "PG26: and the transport's own gate type carries the packet kind and the evidence that allowed it, so no caller can ask the question without saying what it is about to send and none can see a probe credit as a plain yes");
        // ---- 11: what one probe credit costs on the wire, with both compatible transports usable -----
        // `ProbeBudget` is what the other machine's log pays for, and it pays per packet. Two real transports are
        // reachable from this build - GTFO-API's free-sized event and the game's own byte channel - so the budget
        // has to be spent on *physical* packets: three greetings sent both ways would cost that machine six error
        // lines. The double in `ApiTransportStub` is the type `SquadNet.TryBindApi` binds by reflection, so every
        // number below is read off a real call of a production send path (`TryApiSend`, `SendRaw`).
        Check(SquadNet.TryBindApi() && SquadNet.ApiBound,
            "PG27: the production API binding finds the GTFO-API transport, so both compatible channels are really usable in this scenario");
        GTFO.API.NetworkAPI.Reset();

        const ulong channelLocal = 0x5111UL, channelSilent = 0x5112UL, channelOther = 0x5113UL;
        var channelRig = new ProbeRig(channelLocal, true, channelSilent, channelOther);
        channelRig.Exchange.Capability.SetBroadcast(true);
        channelRig.ArmGate();
        for (int tick = 0; tick < 600; tick++) channelRig.Pump(tick * 1f);
        Console.WriteLine("PROBE-WIRE-CHANNELS: unproven peer physical=" + channelRig.Physical(channelSilent)
            + " (api=" + channelRig.ApiTo(channelSilent) + ", raw=" + channelRig.RawTo(channelSilent) + ")");
        Check(channelRig.Physical(channelSilent) == SquadCapability.ProbeBudget,
            "PG28: with both transports usable an unproven peer is handed exactly ProbeBudget physical packets - not one per transport per greeting, which was six (got "
            + channelRig.Physical(channelSilent) + ")");
        Check(channelRig.ApiTo(channelSilent) >= 1 && channelRig.RawTo(channelSilent) >= 1,
            "PG29: and those packets cover both compatible channels, so a peer that runs this build without GTFO-API is still found and one that only reads GTFO-API is too");
        Check(channelRig.ApiTo(channelSilent) == 2 && channelRig.RawTo(channelSilent) == 1,
            "PG30: the allocation is the documented rotation - the preferred transport on the first greeting, the game's own channel on the second, the preferred transport on the third (got api="
            + channelRig.ApiTo(channelSilent) + ", raw=" + channelRig.RawTo(channelSilent) + ")");
        Check(channelRig.Physical(channelOther) == SquadCapability.ProbeBudget
            && channelRig.ApiTo(channelOther) == 2 && channelRig.RawTo(channelOther) == 1,
            "PG31: and a second unproven peer costs its own three physical packets on the same rotation (got "
            + channelRig.Physical(channelOther) + ")");
        Check(channelRig.RowsTo(channelSilent) == 0 && channelRig.Kinds(channelSilent) == 0,
            "PG32: while not one row or pack-kind block reaches it on either transport");

        SNet_Player channelPlayer = channelRig.Player(channelSilent);
        int channelWire = SNet.Core.SendCount + GTFO.API.NetworkAPI.Sends;
        SquadNet.SendSnapshot(channelPlayer, default, channelRig.Exchange.Generation);
        SquadNet.SendSupplyKinds(channelPlayer, default, channelRig.Exchange.Generation);
        Check(SNet.Core.SendCount + GTFO.API.NetworkAPI.Sends == channelWire,
            "PG33: a row and a pack-kind block are still refused for an unproven peer with the API transport bound, and on neither channel");

        // ---- 12: a proved peer and a known route are untouched --------------------------------------
        // Nothing about the probe rule may narrow the channel for a peer that proved it runs this build: an unknown
        // route still gets both copies, which is how the route is learned at all.
        const ulong provenChannel = 0x5114UL;
        var provenRig = new ProbeRig(channelLocal + 0x10, true, provenChannel);
        provenRig.Exchange.Capability.SetBroadcast(true);
        provenRig.ArmGate();
        provenRig.Exchange.Capability.NoteInbound(provenChannel);
        GTFO.API.NetworkAPI.Reset();
        int provenRawBefore = SNet.Core.SendCount;
        SquadNet.SendSnapshot(provenRig.Player(provenChannel), default, provenRig.Exchange.Generation);
        Check(SNet.Core.SendCount - provenRawBefore == 1 && GTFO.API.NetworkAPI.Sends == 1,
            "PG34: a proved peer's row still goes out both ways while its route is unknown (raw="
            + (SNet.Core.SendCount - provenRawBefore) + ", api=" + GTFO.API.NetworkAPI.Sends + ")");
        byte[] helloFromProven = SquadPackets.Hello(provenChannel, provenRig.Exchange.Generation, SyncProtocol.Version);
        bool routeLearned = SquadNet.TryAccept(helloFromProven, (uint)helloFromProven.Length, provenChannel)
            && SquadNet.RouteOf(provenChannel) == false;
        // The decoded packet is drained with no handler installed, so the route is learned without feeding a hello
        // to whatever scenario runs next.
        Action<ulong, uint, ulong> savedHello = SquadNet.OnHello;
        SquadNet.OnHello = (sender, generation, owner) => { };
        SquadNet.Drain();
        SquadNet.OnHello = savedHello;
        Check(routeLearned, "PG35: a packet of that peer's own teaches the transport which route it answers on");
        GTFO.API.NetworkAPI.Reset();
        provenRawBefore = SNet.Core.SendCount;
        SquadNet.SendSnapshot(provenRig.Player(provenChannel), default, provenRig.Exchange.Generation);
        Check(SNet.Core.SendCount - provenRawBefore == 1 && GTFO.API.NetworkAPI.Sends == 0,
            "PG36: and a known route costs exactly one physical packet on that route, never a copy on the other channel");

        // ---- 13: a credit whose transport fails is not re-sent on the other channel -------------------
        const ulong failingChannel = 0x5115UL;
        var failingRig = new ProbeRig(channelLocal + 0x20, true, failingChannel);
        failingRig.Exchange.Capability.SetBroadcast(true);
        failingRig.ArmGate();
        SquadNet.ResetRoutes();
        GTFO.API.NetworkAPI.Reset();
        int failingApiBefore = GTFO.API.NetworkAPI.Sends;
        int failingRawBefore = SNet.Core.SendCount;
        GTFO.API.NetworkAPI.Fail = true;
        failingRig.Pump(0f);                              // the first greeting is assigned the preferred transport
        Check(SNet.Core.SendCount == failingRawBefore && GTFO.API.NetworkAPI.Sends == failingApiBefore,
            "PG37: a probe credit assigned to the preferred transport that fails puts nothing on the wire at all - it is not re-sent on the game's own channel");
        Check(failingRig.Exchange.Capability.ProbesDelivered(failingChannel) == 1,
            "PG38: and the credit is spent anyway, so a failed send cannot buy the peer a fourth packet");
        GTFO.API.NetworkAPI.Fail = false;
        for (int tick = 1; tick < 600; tick++) failingRig.Pump(tick * 1f);
        Check(failingRig.Physical(failingChannel) == SquadCapability.ProbeBudget - 1
            && failingRig.ApiTo(failingChannel) == 1 && failingRig.RawTo(failingChannel) == 1,
            "PG39: the rest of the budget then goes out on the game's channel and the preferred one, so the whole session costs that peer at most ProbeBudget packets (got "
            + failingRig.Physical(failingChannel) + ", api=" + failingRig.ApiTo(failingChannel)
            + ", raw=" + failingRig.RawTo(failingChannel) + ")");

        SquadNet.MaySend = null;
        SquadNet.ResetRoutes();
    }

    // The source of one file of the plugin project, for the wiring check above: the test walks up from its own
    // binary to the directory that holds `GTFOCombatPanel.csproj`, which is where the plugin's `.cs` files live.
    private static string ProbeSource(string file)
    {
        try
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
                directory = directory.Parent;
            if (directory == null) return null;
            string path = Path.Combine(directory.FullName, file);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception) { return null; }
    }

    // One machine wired the way the runtime wires it: its own exchange decides, and every packet it asks for is
    // handed to the production transport with the production gate installed. The counters below are read off
    // `SNet.Core.SendCount`, so they count what reached `SNet.Core.SendBytes` rather than what was intended.
    private sealed class ProbeRig
    {
        public readonly ulong Lookup;
        public readonly List<string> Diag = new List<string>();
        public SquadExchange Exchange = new SquadExchange();
        public readonly List<CombatPlayer> Store = new List<CombatPlayer>();

        private readonly Dictionary<ulong, SNet_Player> _players = new Dictionary<ulong, SNet_Player>();
        private readonly Dictionary<ulong, int> _hellos = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> _rows = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> _kinds = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> _raw = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> _api = new Dictionary<ulong, int>();

        public ProbeRig(ulong local, bool localIsMaster, params ulong[] peers)
        {
            Lookup = local;
            Exchange.Report = Diag.Add;
            Exchange.Configure(local, 0, 1, 2, 3);
            var roster = new List<SquadPeer> { new SquadPeer(local, 0, true, false, localIsMaster) };
            for (int index = 0; index < peers.Length; index++)
            {
                roster.Add(new SquadPeer(peers[index], index + 1, false, false, false));
                _players[peers[index]] = new SNet_Player { Lookup = peers[index], IsLocal = false };
                var row = new CombatPlayer(peers[index]) { DisplaySlot = index + 1 };
                row.Total.AddEnemyDamage(100 + index);
                Store.Add(row);
            }
            Exchange.SetRoster(roster, localIsMaster);
            var own = new CombatPlayer(local) { DisplaySlot = 0 };
            own.Total.AddEnemyDamage(250);
            Store.Insert(0, own);
        }

        // The gate `CombatSync` installs on the transport, verbatim.
        public void ArmGate() { SquadNet.MaySend = Exchange.Capability.MayPutOnWire; }

        public SNet_Player Player(ulong lookup) => _players.TryGetValue(lookup, out SNet_Player player) ? player : null;

        // Physical packets per peer, counted at the two transports themselves: the game's own channel by the stub that
        // stands where `SNet.Core.SendBytes` is, GTFO-API by the double `SquadNet.TryApiSend` invokes reflectively.
        // `Counted` is one logical greeting or row the exchange asked for; `RawTo`/`ApiTo` are what each transport was
        // actually handed, which is the figure a probe budget is about.
        public int RawTo(ulong peer) => Counted(_raw, peer);
        public int ApiTo(ulong peer) => Counted(_api, peer);
        public int Physical(ulong peer) => RawTo(peer) + ApiTo(peer);
        public int Hellos(ulong peer) => Counted(_hellos, peer);
        public int RowsTo(ulong peer) => Counted(_rows, peer);
        public int Kinds(ulong peer) => Counted(_kinds, peer);
        public int Packets(ulong peer) => Hellos(peer) + RowsTo(peer) + Kinds(peer);

        private static int Counted(Dictionary<ulong, int> counter, ulong peer)
            => counter.TryGetValue(peer, out int value) ? value : 0;

        // One tick: the exchange's decision, then the production transport for every packet it asks for, which
        // is the loop `CombatSync.Tick` runs.
        public void Pump(float now)
        {
            foreach (SquadSend send in Exchange.Tick(now, Store))
            {
                SNet_Player target = Player(send.Target);
                if (target == null) continue;                    // a target the roster cannot resolve is skipped, as in the runtime
                int rawBefore = SNet.Core.SendCount;
                int apiBefore = GTFO.API.NetworkAPI.Sends;
                switch (send.Kind)
                {
                    case SquadSendKind.Hello:
                        SquadNet.SendHello(target, Lookup, send.Generation);
                        break;
                    case SquadSendKind.SupplyKinds:
                        SquadNet.SendSupplyKinds(target, send.Kinds, send.Generation);
                        break;
                    default:
                        SquadNet.SendSnapshot(target, send.Snapshot, send.Generation);
                        break;
                }
                int rawWire = SNet.Core.SendCount - rawBefore;
                int apiWire = GTFO.API.NetworkAPI.Sends - apiBefore;
                int wire = rawWire + apiWire;
                if (wire <= 0) continue;
                _raw[send.Target] = Counted(_raw, send.Target) + rawWire;
                _api[send.Target] = Counted(_api, send.Target) + apiWire;
                switch (send.Kind)
                {
                    case SquadSendKind.Hello: _hellos[send.Target] = Counted(_hellos, send.Target) + wire; break;
                    case SquadSendKind.SupplyKinds: _kinds[send.Target] = Counted(_kinds, send.Target) + wire; break;
                    default: _rows[send.Target] = Counted(_rows, send.Target) + wire; break;
                }
            }
        }
    }
}
