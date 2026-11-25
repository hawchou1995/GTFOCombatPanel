using System;
using System.Collections.Generic;
using HawChow.GTFOCombatPanel;
using SNetwork;

// 1.19.43: the one late greeting, measured where it costs something - on the packets the other machine really receives.
//
// The recorded PE Pure session ended with three greetings per peer and not one packet back, and this machine's log can
// only say that no usable host data arrived; it cannot say which build the host runs. What it *can* do is ask once more,
// late, and stop: `SquadExchange.Tick` spends the bounded probe inside the first seconds of an expedition, which is
// exactly when the host may still be loading this build, and `SquadCapability` now reserves one further greeting for
// this expedition's host alone.
//
// Nothing below inspects the exchange's intent: every packet it asks for goes through the production transport
// (`SquadNet.SendHello` with `SquadNet.MaySend` pointing at `SquadCapability.MayPutOnWire`, the gate `CombatSync`
// installs), the answer is injected through the production receive path (`SquadNet.TryAccept` + `SquadNet.Drain`), and the
// counters are read off `SNet.Core.SendCount`, the stub that stands where `SNet.Core.SendBytes` is.
internal static partial class Program
{
    private static void RunSquadLateHelloScenarios()
    {
        SquadNet.ResetRoutes();
        Check(SquadNet.TryBindApi(), "LH0: the production API binding is available in this scenario, so both compatible channels are real");
        bool apiBound = SquadNet.ApiBound;
        GTFO.API.NetworkAPI.Reset();

        // ---- 1: a client whose host never answers ------------------------------------------------
        const ulong local = 0x5201UL, host = 0x5202UL, other = 0x5203UL;
        var rig = new ProbeRig(local, false, host, other);
        rig.Exchange.SetRoster(new List<SquadPeer>
        {
            new SquadPeer(local, 0, true, false, false),
            new SquadPeer(host, 1, false, false, true),
            new SquadPeer(other, 2, false, false, false)
        }, false);
        rig.Exchange.Capability.SetBroadcast(true);
        rig.ArmGate();
        for (int tick = 0; tick < 8; tick++) rig.Pump(tick * 1f);
        Console.WriteLine("LH-EARLY: host hellos=" + rig.Hellos(host) + " physical=" + rig.Physical(host)
            + " (api=" + rig.ApiTo(host) + ",raw=" + rig.RawTo(host) + "), other physical=" + rig.Physical(other)
            + ", rowsToHost=" + rig.RowsTo(host));
        Check(rig.Hellos(host) == SquadCapability.ProbeBudget,
            "LH1: the initial burst is exactly ProbeBudget (" + SquadCapability.ProbeBudget + ") greetings for the host, all of them in the first seconds of the expedition (got "
                + rig.Hellos(host) + ")");
        Check(rig.RowsTo(host) == 0 && rig.RowsTo(other) == 0 && rig.Kinds(host) == 0,
            "LH2: an unproven host is sent no row and no pack-kind block - the probe still pays for greetings only");

        // ---- 2: the late window, and the one packet it buys ---------------------------------------
        for (int tick = 8; tick < 40; tick++) rig.Pump(tick * 1f);
        Check(rig.Hellos(host) == SquadCapability.ProbeBudget,
            "LH3: nothing at all is sent between the end of the burst and the late window, so the recovery is not a slow cadence (got "
                + rig.Hellos(host) + ")");
        int beforeLate = rig.Physical(host);
        for (int tick = 40; tick <= 60; tick++) rig.Pump(tick * 1f);
        Console.WriteLine("LH-LATE: host physical=" + rig.Physical(host) + " (api=" + rig.ApiTo(host) + ",raw=" + rig.RawTo(host)
            + "), other physical=" + rig.Physical(other) + " lateSent=" + rig.Exchange.Capability.LateProbesSent(host)
            + " lateDelivered=" + rig.Exchange.Capability.LateProbesDelivered(host));
        Check(rig.Physical(host) - beforeLate == SquadCapability.LateProbeBudget,
            "LH4: one late greeting is put on the wire once the window opens, and exactly one packet (got "
                + (rig.Physical(host) - beforeLate) + ")");
        Check(rig.Exchange.Capability.LateProbesSent(host) == SquadCapability.LateProbeBudget
                && rig.Exchange.Capability.LateProbesDelivered(host) == SquadCapability.LateProbeBudget,
            "LH5: the reservation the exchange charged and the packet the transport handed over are the same figure, so the logical and physical gates agree");
        Check(apiBound
                ? rig.RawTo(host) == 2 && rig.ApiTo(host) == 2
                : rig.RawTo(host) == SquadCapability.ProbeBudget + SquadCapability.LateProbeBudget,
            "LH6: the late greeting is the fourth physical packet, so the rotation buys the other compatible channel (api="
                + rig.ApiTo(host) + ",raw=" + rig.RawTo(host) + ", apiBound=" + apiBound + ")");
        Check(rig.Physical(other) == SquadCapability.ProbeBudget,
            "LH7: the late greeting belongs to this expedition's host alone - the other unproven peer is still capped at its own budget (got "
                + rig.Physical(other) + ")");
        Check(rig.RowsTo(host) == 0,
            "LH8: and it is still a greeting - not one row is sent to a host that has not proved it can read one");
        Check(rig.Diag.Exists(line => line.Contains("one late greeting")),
            "LH9: the late greeting is reported once, with the numbers a reader needs, instead of leaving the silence unexplained");

        // ---- 3: one round, never a cadence, and never a second reservation ------------------------
        for (int tick = 61; tick < 400; tick++) rig.Pump(tick * 1f);
        Check(rig.Hellos(host) == SquadCapability.ProbeBudget + SquadCapability.LateProbeBudget,
            "LH10: five more minutes of silence send nothing further: the recovery is one round, not a reopened channel (got "
                + rig.Hellos(host) + ")");
        Check(rig.Exchange.Capability.MayPutOnWire(host, SquadPacketKind.Hello) == SquadEvidence.Nothing
                && rig.Exchange.Capability.MayPutOnWire(host, SquadPacketKind.Snapshot) == SquadEvidence.Nothing,
            "LH11: asking the wire gate again buys neither a greeting nor a row, whatever the queue did or did not ask for");
        int settled = SNet.Core.SendCount;
        SNet_Player hostPlayer = rig.Player(host);
        for (int attempt = 0; attempt < 10; attempt++) SquadNet.SendHello(hostPlayer, local, rig.Exchange.Generation);
        for (int attempt = 0; attempt < 10; attempt++) SquadNet.SendSnapshot(hostPlayer, default, rig.Exchange.Generation);
        Check(SNet.Core.SendCount == settled,
            "LH12: repeating the send puts nothing more on the wire - a failed or repeated attempt cannot buy the host another packet");

        // ---- 4: the answer, through the real receive path ---------------------------------------
        Action<ulong, uint, ulong> savedHello = SquadNet.OnHello;
        Action<ulong, uint, SyncSnapshot> savedSnapshot = SquadNet.OnSnapshot;
        ulong heard = 0UL;
        int rowsHeard = 0;
        SquadNet.OnHello = (sender, generation, owner) => { heard = sender; rig.Exchange.OnHello(sender, generation, 62f, out _); };
        SquadNet.OnSnapshot = (sender, generation, snapshot) => { rowsHeard++; };
        byte[] answer = SquadPackets.Hello(host, rig.Exchange.Generation, SyncProtocol.Version);
        bool claimed = SquadNet.TryAccept(answer, (uint)answer.Length, host);
        SquadNet.Drain();
        SquadNet.OnHello = savedHello;
        SquadNet.OnSnapshot = savedSnapshot;
        Check(claimed && heard == host,
            "LH13: an answer from the host is claimed by this build's receive prefix and reaches the exchange through `Drain`");
        Check(rig.Exchange.Capability.EvidenceOf(host) == SquadEvidence.Proven && rig.Exchange.HostAnswered,
            "LH14: and it is what proves the host runs this build - the greeting never does, which is the whole point of asking for one");
        int rowsBeforeAnswer = rig.RowsTo(host);
        for (int tick = 400; tick < 460; tick++) rig.Pump(tick * 1f);
        Check(rig.RowsTo(host) > rowsBeforeAnswer && rig.RowsTo(host) > 0,
            "LH15: two-way recovery - this machine's own row now reaches that host through the same gate the greeting was refused by (rows="
                + rig.RowsTo(host) + ")");
        Check(rowsHeard == 0, "LH16: nothing was invented on the receive side - the snapshot handler was never called by a hello");

        // ---- 5: a new expedition is a new, equally bounded recovery ------------------------------
        const ulong resetHost = 0x5212UL;
        var resetRig = new ProbeRig(0x5211UL, false, resetHost);
        resetRig.Exchange.SetRoster(new List<SquadPeer>
        {
            new SquadPeer(0x5211UL, 0, true, false, false),
            new SquadPeer(resetHost, 1, false, false, true)
        }, false);
        resetRig.Exchange.Capability.SetBroadcast(true);
        resetRig.ArmGate();
        for (int tick = 0; tick < 60; tick++) resetRig.Pump(tick * 1f);
        int expeditionOne = resetRig.Physical(resetHost);
        Check(expeditionOne == SquadCapability.ProbeBudget + SquadCapability.LateProbeBudget,
            "LH17: one expedition costs an unanswered host the bounded probe plus the one late greeting, and not a packet more (got "
                + expeditionOne + ")");
        resetRig.Exchange.ResetForExpedition();
        Check(resetRig.Exchange.Capability.LateProbesSent(resetHost) == 0
                && resetRig.Exchange.Capability.ProbesSent(resetHost) == 0,
            "LH18: the next expedition starts with both reservations renewed, so the previous run's spent probe is not inherited");
        for (int tick = 60; tick < 130; tick++) resetRig.Pump(tick * 1f);
        Check(resetRig.Physical(resetHost) - expeditionOne == SquadCapability.ProbeBudget + SquadCapability.LateProbeBudget,
            "LH19: and spends exactly the same bound again (got " + (resetRig.Physical(resetHost) - expeditionOne) + ")");

        // ---- 6: sharing off owes nobody anything, the late greeting included --------------------
        const ulong silentHost = 0x5222UL;
        var offRig = new ProbeRig(0x5221UL, false, silentHost);
        offRig.Exchange.SetRoster(new List<SquadPeer>
        {
            new SquadPeer(0x5221UL, 0, true, false, false),
            new SquadPeer(silentHost, 1, false, false, true)
        }, false);
        offRig.Exchange.Capability.SetBroadcast(false);       // ShareWithEveryPlayer = false
        offRig.ArmGate();
        for (int tick = 0; tick < 90; tick++) offRig.Pump(tick * 1f);
        Check(offRig.Physical(silentHost) == 0 && offRig.Exchange.Capability.LateProbesSent(silentHost) == 0,
            "LH20: with ShareWithEveryPlayer off not one packet is put on the wire for an unproven host, the late greeting included");

        // ---- 6b: the late greeting never pays for a packet the ordinary quota can still pay for -------------
        //
        // The ordinary reservation is what a peer costs while it lasts, and the late one is the *recovery* for a quota that
        // is already spent. A transport that is decided-but-not-armed leaves the reservations outstanding, which is exactly
        // the window this asks about: with three reserved and none delivered, the fourth logical packet must still be paid
        // from the ordinary counter, and the late counter must stay at zero until the ordinary three are really handed over.
        const ulong quotaLocal = 0x5231UL, quotaHost = 0x5232UL;
        var quotaRig = new ProbeRig(quotaLocal, false, quotaHost);
        quotaRig.Exchange.SetRoster(new List<SquadPeer>
        {
            new SquadPeer(quotaLocal, 0, true, false, false),
            new SquadPeer(quotaHost, 1, false, false, true)
        }, false);
        quotaRig.Exchange.Capability.SetBroadcast(true);
        SquadNet.MaySend = null;                                  // decided, never handed over
        for (int tick = 0; tick < 60; tick++) quotaRig.Pump(tick * 1f);
        Check(quotaRig.Physical(quotaHost) == 0
                && quotaRig.Exchange.Capability.ProbesSent(quotaHost) == SquadCapability.ProbeBudget
                && quotaRig.Exchange.Capability.LateProbesSent(quotaHost) == SquadCapability.LateProbeBudget,
            "LH28: an unarmed transport leaves the ordinary three and the one late greeting reserved, and puts nothing on the wire");
        quotaRig.ArmGate();
        SNet_Player quotaPlayer = quotaRig.Player(quotaHost);
        int[] ordinaryAfter = new int[4];
        int[] lateAfter = new int[4];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            SquadNet.SendHello(quotaPlayer, quotaLocal, quotaRig.Exchange.Generation);
            ordinaryAfter[attempt] = quotaRig.Exchange.Capability.ProbesDelivered(quotaHost);
            lateAfter[attempt] = quotaRig.Exchange.Capability.LateProbesDelivered(quotaHost);
        }
        Check(ordinaryAfter[0] == 1 && ordinaryAfter[1] == 2 && ordinaryAfter[2] == 3
                && lateAfter[0] == 0 && lateAfter[1] == 0 && lateAfter[2] == 0,
            "LH29: the first three deliveries are paid from the ordinary reservation, whatever the late one is worth (ordinary="
                + string.Join(",", ordinaryAfter) + "; late=" + string.Join(",", lateAfter) + ")");
        Check(lateAfter[3] == SquadCapability.LateProbeBudget && ordinaryAfter[3] == SquadCapability.ProbeBudget,
            "LH30: only the fourth packet past the ordinary quota is charged to the late greeting, and to nothing else");
        Check(quotaRig.Exchange.Capability.MayPutOnWire(quotaHost, SquadPacketKind.Hello) == SquadEvidence.Nothing,
            "LH31: after both reservations are delivered the gate answers nothing, so the total stays at "
                + (SquadCapability.ProbeBudget + SquadCapability.LateProbeBudget) + " packets");

        // ---- 6c: a change of master or role conserves both reservations --------------------------------------
        const ulong roleLocal = 0x5241UL, roleHost = 0x5242UL, roleNext = 0x5243UL;
        var roleRig = new ProbeRig(roleLocal, false, roleHost, roleNext);
        roleRig.Exchange.SetRoster(new List<SquadPeer>
        {
            new SquadPeer(roleLocal, 0, true, false, false),
            new SquadPeer(roleHost, 1, false, false, true),
            new SquadPeer(roleNext, 2, false, false, false)
        }, false);
        roleRig.Exchange.Capability.SetBroadcast(true);
        roleRig.ArmGate();
        for (int tick = 0; tick < 60; tick++) roleRig.Pump(tick * 1f);
        int oldHostPhysical = roleRig.Physical(roleHost);
        Check(oldHostPhysical == SquadCapability.ProbeBudget + SquadCapability.LateProbeBudget,
            "LH32: the first host costs the bounded probe plus the one late greeting (got " + oldHostPhysical + ")");
        int lateBefore = roleRig.Exchange.Capability.LateProbesSent(roleHost);
        roleRig.Exchange.SetRoster(new List<SquadPeer>
        {
            new SquadPeer(roleLocal, 0, true, false, false),
            new SquadPeer(roleHost, 1, false, false, false),
            new SquadPeer(roleNext, 2, false, false, true)      // the host seat changes hands
        }, false);
        Check(roleRig.Exchange.Capability.LateProbesSent(roleHost) == lateBefore
                && roleRig.Exchange.Capability.LateProbesSent(roleNext) == 0,
            "LH33: the new host starts with its own unspent late greeting, and the old one's is neither transferred nor re-granted");
        int nextBefore = roleRig.Physical(roleNext);
        for (int tick = 60; tick < 130; tick++) roleRig.Pump(tick * 1f);
        Check(roleRig.Exchange.Capability.LateProbesDelivered(roleNext) <= SquadCapability.LateProbeBudget
                && roleRig.Physical(roleNext) - nextBefore <= SquadCapability.ProbeBudget + SquadCapability.LateProbeBudget,
            "LH34: and the new host's whole cost stays inside the same bound (lateDelivered="
                + roleRig.Exchange.Capability.LateProbesDelivered(roleNext) + ", spent=" + (roleRig.Physical(roleNext) - nextBefore) + ")");
        Check(roleRig.Physical(roleHost) == oldHostPhysical,
            "LH35: while the peer that lost the seat is sent nothing further by either reservation");

        // This machine becomes the host: a host owes nobody a greeting, so the late one is never asked for.
        const ulong hostLocal = 0x5251UL, hostPeer = 0x5252UL;
        var hostRig = new ProbeRig(hostLocal, true, hostPeer);
        hostRig.Exchange.Capability.SetBroadcast(true);
        hostRig.ArmGate();
        for (int tick = 0; tick < 130; tick++) hostRig.Pump(tick * 1f);
        Check(hostRig.Exchange.Capability.LateProbesSent(hostPeer) == 0
                && hostRig.Physical(hostPeer) == SquadCapability.ProbeBudget,
            "LH36: as the host this machine reserves no late greeting at all - the probe stays exactly three packets (got "
                + hostRig.Physical(hostPeer) + ")");

        // ---- 7: the two gates, in the order the runtime writes them ------------------------------
        string exchange = ProbeSource("SquadExchange.cs");
        string capability = ProbeSource("SquadCapability.cs");
        string squadNet = ProbeSource("SquadNet.cs");
        int nothingGate = exchange.IndexOf("if (evidence == SquadEvidence.Nothing) { withheld++; continue; }", StringComparison.Ordinal);
        int greetGate = exchange.IndexOf("if (!Handshake.ShouldGreet(peer.Lookup, now)) continue;", StringComparison.Ordinal);
        Console.WriteLine("LH-WIRING: nothingGate=" + nothingGate + " greetGate=" + greetGate);
        Check(nothingGate >= 0 && greetGate > nothingGate,
            "LH21: the evidence gate is still asked before the greeting cadence in `Tick`, which is why the recovery had to be asked for on its own");
        Check(exchange.Contains("Discovery.ProbesSent(lateHost) >= SquadCapability.ProbeBudget && Discovery.NoteLateProbe(lateHost)"),
            "LH22: the late greeting is only asked for after the whole bounded probe was spent, and only against its own reservation");
        Check(exchange.Contains("if (_localIsMaster || _master == 0UL || _master == _local) return false;")
                && exchange.Contains("if (!peer.IsHuman || peer.IsLocal) return false;"),
            "LH23: a host owes nobody a greeting, and the placeholder master a client without a named host carries is never greeted by this path");
        Check(capability.Contains("public const int LateProbeBudget = 1;")
                && capability.Contains("if (peer == 0UL || !_broadcast || _incompatible.Contains(peer)) return false;"),
            "LH24: one late greeting per peer per expedition, behind the same sharing switch and the same protocol-version exclusion as the probe");
        Check(capability.Contains("if (LateProbesDelivered(peer) < LateProbesSent(peer))")
                && capability.Contains("_lateDelivered[peer] = LateProbesDelivered(peer) + 1;")
                && capability.Contains("if (sent >= LateProbeBudget) return false;"),
            "LH25: the wire gate hands the late reservation over exactly once, on the same terms as an ordinary one");
        Check(squadNet.Contains("internal static Func<ulong, SquadPacketKind, SquadEvidence> MaySend;"),
            "LH26: the transport's only gate is still `MaySend`, so nothing here opened a second send path");
        Check(!exchange.Contains("SquadEvidence.Proven =") && !capability.Contains("Broadcast = 3"),
            "LH27: neither the authority rule nor the removed unproven-pass branch came back");

        SquadNet.MaySend = null;
        SquadNet.ResetRoutes();
        GTFO.API.NetworkAPI.Reset();
    }
}
