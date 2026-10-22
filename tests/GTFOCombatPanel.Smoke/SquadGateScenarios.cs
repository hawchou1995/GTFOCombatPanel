// The one rule this round adds to the wire, asserted where it can be measured: how many byte runs
// actually reached `SNet.Core.SendBytes`. The exchange's own decision and the transport's gate are two
// separate checks on purpose - a mistake in the first must not be able to put a packet on the wire on
// its own - so both are driven here, and the transport is asserted on the stub's send counter rather
// than on its intent.
using System;
using System.Collections.Generic;
using HawChow.GTFOCombatPanel;
using SNetwork;

internal static partial class Program
{
    private static void RunSquadGateScenarios()
    {
        // ---- the gate itself -------------------------------------------------
        var capability = new SquadCapability();
        Check(!capability.MaySend(9001UL) && !capability.MaySend(0UL),
            "G1: a machine that has learned nothing about a peer sends it nothing, and a lookup of zero is never a peer");
        Check(capability.Describe(9001UL).Contains("no evidence"),
            "G2: the diagnostic says why a stranger is not spoken to");

        var listed = new List<ulong>();
        int accepted = SquadCapability.ParseAllowlist(" 9001, 9002;not-a-number 0 9003,9001,9004 , ", listed);
        Check(accepted == 4 && listed.Count == 4
            && listed.Contains(9001UL) && listed.Contains(9002UL) && listed.Contains(9003UL) && listed.Contains(9004UL),
            "G3: the configured list takes plain decimal lookups only - a name, a zero, a duplicate and an empty slot are dropped rather than guessed at");
        Check(!SquadCapability.TryParseLookup("18446744073709551616", out _)
            && !SquadCapability.TryParseLookup("-1", out _)
            && SquadCapability.TryParseLookup("18446744073709551615", out ulong exact),
            "G4: a lookup that does not fit in 64 unsigned bits is refused instead of wrapping, and the largest one that does is accepted");

        capability.SetAllowlist(listed);
        Check(capability.MaySend(9001UL, out SquadEvidence whyListed) && whyListed == SquadEvidence.Allowlisted,
            "G5: a machine named in this machine's own configuration may be spoken to before it has said anything");
        Check(!capability.MaySend(9100UL),
            "G6: a peer that is neither named nor heard from is still sent nothing");

        Check(capability.NoteInbound(9100UL) && !capability.NoteInbound(9100UL),
            "G7: a peer that spoke first is proved once, so the diagnostic is written once per peer rather than once per packet");
        Check(capability.MaySend(9100UL, out SquadEvidence whyProven) && whyProven == SquadEvidence.Proven,
            "G8: proof from the peer is recorded without anything having been sent to ask for it");

        Check(capability.NoteIncompatible(9001UL) && !capability.MaySend(9001UL),
            "G9: a peer on another protocol version is sent nothing even when it is also named in the configuration, because nothing this build sends can be read by it");
        Check(capability.Describe(9001UL).Contains("another protocol"),
            "G10: the diagnostic reports the protocol mismatch as the reason, not the missing evidence");

        Check(capability.NoteInbound(9200UL) && capability.MaySend(9200UL),
            "G11: a fresh peer proves itself before anything is sent to it");
        capability.Forget(9200UL);
        Check(!capability.MaySend(9200UL), "G12: a seat that changed hands loses what the previous occupant proved");
        capability.Forget(9001UL);
        Check(capability.MaySend(9001UL),
            "G13: forgetting a seat does not discard the operator's own list, which names machines rather than a lobby");

        capability.ResetEvidence();
        Check(!capability.MaySend(9100UL) && capability.MaySend(9001UL) && capability.Allowlisted == 4,
            "G14: a new lobby clears what was learned inside the old one and keeps the configured list");

        // ---- the transport's own gate ---------------------------------------
        SNet_Player listed_peer = new SNet_Player { Lookup = 9001UL, IsLocal = false };
        SNet_Player stranger = new SNet_Player { Lookup = 9300UL, IsLocal = false };
        int sent = SNet.Core.SendCount;

        // No gate wired: the transport has nothing to go on, so nothing leaves the machine.
        SquadNet.MaySend = null;
        SquadNet.SendHello(listed_peer, 7UL, 3u);
        Check(SNet.Core.SendCount == sent,
            "G15: a transport whose owner never spoke refuses every packet - a greeting included - rather than sending by default");

        // The owner's gate, refusing everything: still nothing on the wire.
        SquadNet.MaySend = _ => false;
        SquadNet.SendHello(listed_peer, 7UL, 3u);
        Check(SNet.Core.SendCount == sent, "G16: the transport asks its owner before every packet and obeys a refusal");

        // A gate that throws is a refusal, not an exception into the replication layer.
        SquadNet.MaySend = _ => throw new InvalidOperationException("gate fault");
        bool threw = false;
        try { SquadNet.SendHello(listed_peer, 7UL, 3u); }
        catch (Exception) { threw = true; }
        Check(!threw && SNet.Core.SendCount == sent,
            "G17: a gate that fails is read as a refusal, so a fault in this mod cannot reach the game's own send path");

        // The real capability, wired the way CombatSync wires it.
        capability.ResetEvidence();
        capability.SetAllowlist(listed);
        SquadNet.MaySend = capability.MaySend;
        SquadNet.SendHello(listed_peer, 7UL, 3u);
        Check(SNet.Core.SendCount == sent + 1 && SNet.Core.LastTarget == 9001UL,
            "G18: a named peer is the one byte run that leaves this machine, and it is addressed to that peer");
        sent = SNet.Core.SendCount;

        SquadNet.SendHello(stranger, 7UL, 3u);
        SquadNet.SendSnapshot(stranger, default, 3u);
        Check(SNet.Core.SendCount == sent,
            "G19: a stranger is sent no greeting and no row, so a machine that runs no mod is never handed bytes it has no handler for");

        capability.NoteInbound(9300UL);
        SquadNet.SendSnapshot(stranger, default, 3u);
        Check(SNet.Core.SendCount == sent + 1 && SNet.Core.LastTarget == 9300UL,
            "G20: once the stranger has spoken first, the same machine may answer it");

        SquadNet.MaySend = capability.MaySend;
        bool nullThrew = false;
        try { SquadNet.SendHello(null, 7UL, 3u); }
        catch (Exception) { nullThrew = true; }
        Check(!nullThrew, "G21: a null target is a no-op rather than an exception in the send path");

        SquadNet.MaySend = null;

        RunSquadIsolationScenarios();
    }

    // The case the shipped default actually produces: two players who both run this build, sharing a host
    // that runs none, and neither of them has named the other in its configuration. Nothing is exchanged -
    // and, more to the point, the vanilla host is handed nothing at all, for as long as the lobby lasts.
    private static void RunSquadIsolationScenarios()
    {
        var wire = new Wire();
        var vanilla = new Node(0x61UL, 0) { IsMaster = true, Modded = false };
        var first = new Node(0x62UL, 1) { Isolated = true };
        var second = new Node(0x63UL, 2) { Isolated = true };
        wire.Add(vanilla); wire.Add(first); wire.Add(second);
        first.Row(0x62UL).Total.AddEnemyDamage(120);
        second.Row(0x63UL).Total.AddEnemyDamage(80);
        for (int tick = 0; tick < 240; tick++) wire.Tick(tick * 0.25f);      // a full minute of keep-alive cadence
        Check(vanilla.HellosSeen == 0 && vanilla.SnapshotsSeen == 0,
            "I1: a host that runs no mod is sent nothing at all for a whole minute, by either client");
        Check(first.HellosSeen == 0 && second.HellosSeen == 0 && first.SnapshotsSeen == 0 && second.SnapshotsSeen == 0,
            "I2: two clients that never named each other exchange nothing either, which is the price of never probing for who runs this build");
        List<CombatPlayer> seenByFirst = first.Exchange.Project(first.Store, first.Lookup);
        Check(seenByFirst.Count == 1 && seenByFirst[0].Lookup == 0x62UL,
            "I3: each client shows its own row alone rather than a blank board or a row for somebody it never heard from");

        // The same pair, with the operator's own list filled in: they pair directly, and the host that runs no
        // mod is still handed nothing - the naming is what changed, never who is spoken to.
        first.Isolated = false;
        second.Isolated = false;
        for (int tick = 240; tick < 320; tick++) wire.Tick(tick * 0.25f);
        Check(Row(first.Exchange.Project(first.Store, first.Lookup), 0x63UL) != null
            && Row(second.Exchange.Project(second.Store, second.Lookup), 0x62UL) != null,
            "I4: naming the other client in the configuration pairs the two directly, with no host in between");
        Check(vanilla.HellosSeen == 0 && vanilla.SnapshotsSeen == 0,
            "I5: and the unmodded host is still sent nothing, because naming a client says nothing about the host");
    }
}
