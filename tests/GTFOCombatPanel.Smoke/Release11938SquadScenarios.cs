using System;
using System.Collections.Generic;
using HawChow.GTFOCombatPanel;

// 1.19.38: the bounded probe.
//
// The rule this file replaces was "sharing with the slot list means greeting every human peer forever": a
// hello every two seconds five times, then one every ten seconds for the rest of the session, plus a row a
// second, all of it sent to machines there is no evidence about. The field run beside a machine that runs no
// mod measured what that costs on the *other* end: 13051 log lines, 10249 of them
// `[Error : GTFO-API] [NetworkAPI]: <id> invoked an event <name> which isn't registered`, and 10225 of those
// for `CombatPanelSquad`. GTFO-API does not drop an unregistered free-sized event quietly; it writes one error
// line into the receiving machine's log for every packet.
//
// So the wire rule is now: a peer that has proved it runs this build (`NoteInbound`) or that the operator named
// (`AllowlistedPeers`) is sent rows and keeps the greeting cadence exactly as before; an unproven peer gets
// `SquadCapability.ProbeBudget` greeting packets for the whole session, never a row, and then nothing at all
// until it answers, a new expedition starts or its seat changes hands. `ShareWithEveryPlayer = false` does not
// probe at all.
//
// Everything below drives the production types (`SquadExchange.Tick`, `SquadCapability`) in memory; nothing here
// touches Unity, SNet, GTFO-API or the configuration file, so nothing here proves what a real lobby's log looks
// like - it proves how many packets this build decides to put on the wire for a given evidence state.
internal static partial class Program
{
    private static void RunRelease11938SquadScenarios()
    {
        // ---- 1: one peer that never answers, for ten simulated minutes --------------------------------
        const ulong local1 = 0x2101UL, silent1 = 0x2102UL;
        var probeLineSink = new List<string>();
        SquadExchange exchange1 = ProbeExchange(probeLineSink, local1, true, silent1);
        exchange1.Capability.SetBroadcast(true);
        List<CombatPlayer> rows1 = ProbeRows(local1, silent1);
        int inBudget = 0, afterBudget = 0, spentTick = -1;
        bool spent = false;
        bool probeOnlyHellos = true;
        for (int tick = 0; tick < 600; tick++)                       // 600 s of 1 s ticks
        {
            float now = tick * 1f;
            foreach (SquadSend send in exchange1.Tick(now, rows1))
            {
                if (send.Target != silent1) continue;
                if (spent) afterBudget++;
                else
                {
                    inBudget++;
                    if (send.Kind != SquadSendKind.Hello) probeOnlyHellos = false;
                }
            }
            if (!spent && !exchange1.Capability.MaySend(silent1)) { spent = true; spentTick = tick; }
        }
        Check(spent && spentTick >= 0,
            "S1: the probe budget really is spent inside the run, so the gate answers `Nothing` for the silent peer");
        Check(inBudget == SquadCapability.ProbeBudget,
            "S1: a peer that never answers receives exactly ProbeBudget (" + SquadCapability.ProbeBudget
            + ") packets in ten simulated minutes, not one per keep-alive interval (got " + inBudget + ")");
        Check(inBudget <= SquadCapability.ProbeBudget,
            "S1: and never more than the budget, whatever the cadence would have produced on its own");
        Check(afterBudget == 0,
            "S1: from the packet that spends the budget to the end of the session that peer receives nothing at all (got "
            + afterBudget + ")");
        Check(probeOnlyHellos,
            "S1: the bounded probe carries greetings only - not one row is handed to a peer that has not proved it can read one");
        Check(probeLineSink.FindAll(line => line.Contains("has now been sent all")).Count == 1,
            "S1: the budget being spent is written once for that peer, not once per frame");
        Check(!probeLineSink.Exists(line => line.Contains("0 of 0 human peer(s)")),
            "S1: the discovery line never claims \"0 of 0 human peer(s)\" - it says the real figure or nothing");
        Check(probeLineSink.Exists(line => line.Contains("bounded probe")),
            "S1: and the discovery line names the bounded probe, so a reader can tell probing from an open broadcast");

        // ---- 2: three peers that never answer, against the field baseline ------------------------------
        const ulong local2 = 0x2201UL, silent2A = 0x2202UL, silent2B = 0x2203UL, silent2C = 0x2204UL;
        var sink2 = new List<string>();
        SquadExchange exchange2 = ProbeExchange(sink2, local2, true, silent2A, silent2B, silent2C);
        exchange2.Capability.SetBroadcast(true);
        List<CombatPlayer> rows2 = ProbeRows(local2, silent2A, silent2B, silent2C);
        int total2 = 0;
        for (int tick = 0; tick < 600; tick++)
            foreach (SquadSend send in exchange2.Tick(tick * 1f, rows2))
                if (send.Target == silent2A || send.Target == silent2B || send.Target == silent2C) total2++;
        Console.WriteLine("PROBE-BUDGET: 3 silent peers receive " + total2
            + " packets (baseline 10225 CombatPanelSquad error lines)");
        Check(total2 == 3 * SquadCapability.ProbeBudget,
            "S2: three silent peers cost three budgets in total (" + total2 + " packets over ten simulated minutes)");
        Check(total2 <= 3 * SquadCapability.ProbeBudget,
            "S2: and that total is bounded by peers x ProbeBudget, which is what turns 10225 field error lines into "
            + (3 * SquadCapability.ProbeBudget) + " packets");
        Check(sink2.FindAll(line => line.Contains("has now been sent all")).Count == 3,
            "S2: each of the three peers gets its own one-off budget line, and no peer gets a second");

        // ---- 3: a proven peer and an allowlisted peer keep the old cadence ----------------------------
        const ulong local3 = 0x2301UL, proven3 = 0x2302UL, named3 = 0x2303UL;
        var sink3 = new List<string>();
        SquadExchange exchange3 = ProbeExchange(sink3, local3, false, proven3, named3);   // a client: one row per peer per second
        exchange3.Capability.SetBroadcast(true);
        var allowlist3 = new List<ulong> { named3 };
        exchange3.SetAllowlist(allowlist3);
        exchange3.Capability.NoteInbound(proven3);
        List<CombatPlayer> rows3 = ProbeRows(local3, proven3, named3);
        int toProven = 0, toNamed = 0, rowsToProven = 0, rowsToNamed = 0;
        const int window = 60;
        for (int tick = 0; tick < window; tick++)
        {
            foreach (SquadSend send in exchange3.Tick(tick * 1f, rows3))
            {
                if (send.Target == proven3)
                {
                    toProven++;
                    if (send.Kind == SquadSendKind.Snapshot) rowsToProven++;
                }
                else if (send.Target == named3)
                {
                    toNamed++;
                    if (send.Kind == SquadSendKind.Snapshot) rowsToNamed++;
                }
            }
        }
        Check(rowsToProven == window,
            "S3: a peer that proved it runs this build still receives one row per second - the snapshot cadence is unchanged (got "
            + rowsToProven + " of " + window + ")");
        Check(rowsToNamed == window,
            "S3: a peer named in AllowlistedPeers receives the same one row per second without ever having spoken (got "
            + rowsToNamed + " of " + window + ")");
        Check(rowsToProven == rowsToNamed && toProven == toNamed,
            "S3: proven and allowlisted peers are treated identically in volume, so nothing about the new gate slowed the old cadence");
        Check(exchange3.Capability.EvidenceOf(proven3) == SquadEvidence.Proven
            && exchange3.Capability.EvidenceOf(named3) == SquadEvidence.Allowlisted,
            "S3: and each of them is decided by its own evidence, not by a probe that happens to be unspent");

        // ---- 4: a silent peer that finally speaks is served again -------------------------------------
        bool answered = false;
        exchange1.OnHello(silent1, exchange1.Generation, 600f, out answered);
        bool rowsReturned = false;
        int rowsAfterAnswer = 0;
        for (int tick = 600; tick < 620; tick++)
        {
            foreach (SquadSend send in exchange1.Tick(tick * 1f, rows1))
            {
                if (send.Target != silent1) continue;
                if (send.Kind == SquadSendKind.Snapshot) { rowsAfterAnswer++; rowsReturned = true; }
            }
        }
        Check(rowsReturned && rowsAfterAnswer > 0,
            "S4: a peer whose budget was spent and that then sends this machine a packet of this build's protocol starts receiving rows again (got "
            + rowsAfterAnswer + ")");
        Check(exchange1.Capability.EvidenceOf(silent1) == SquadEvidence.Proven,
            "S4: because its own packet is the proof that outranks a spent probe");

        // ---- 5: a new lobby and a new seat both reset the budget --------------------------------------
        var sink5 = new List<string>();
        SquadExchange exchange5 = ProbeExchange(sink5, local1, true, silent1);
        exchange5.Capability.SetBroadcast(true);
        int probesBefore = 0;
        for (int tick = 0; tick < 60; tick++)
            foreach (SquadSend send in exchange5.Tick(tick * 1f, rows1))
                if (send.Target == silent1) probesBefore++;
        Check(probesBefore == SquadCapability.ProbeBudget && !exchange5.Capability.MaySend(silent1),
            "S5: the peer's budget is spent before anything is reset");
        exchange5.Capability.Forget(silent1);
        Check(exchange5.Capability.MaySend(silent1),
            "S5: the seat changing hands gives the new occupant its own bounded probe instead of the previous one's spent budget");
        int probesAfterForget = 0;
        for (int tick = 60; tick < 120; tick++)
            foreach (SquadSend send in exchange5.Tick(tick * 1f, rows1))
                if (send.Target == silent1) probesAfterForget++;
        Check(probesAfterForget == SquadCapability.ProbeBudget,
            "S5: and that new occupant costs the same bounded " + SquadCapability.ProbeBudget + " packets, never more");
        exchange5.Capability.ResetEvidence();
        Check(exchange5.Capability.MaySend(silent1),
            "S5: a new lobby (ResetEvidence) resets the probe budget as well as what was proved inside the old one");
        int probesAfterLobby = 0;
        for (int tick = 120; tick < 180; tick++)
            foreach (SquadSend send in exchange5.Tick(tick * 1f, rows1))
                if (send.Target == silent1) probesAfterLobby++;
        Check(probesAfterLobby == SquadCapability.ProbeBudget,
            "S5: and the new lobby's probe is bounded in exactly the same way");
        int probesAfterExpedition = 0;
        for (int tick = 180; tick < 240; tick++)
            foreach (SquadSend send in exchange5.Tick(tick * 1f, rows1))
                if (send.Target == silent1) probesAfterExpedition++;
        Check(probesAfterExpedition == 0,
            "S5: nothing more is sent until a reset actually happens (got " + probesAfterExpedition + ")");
        exchange5.ResetForExpedition();
        int probesNewExpedition = 0;
        for (int tick = 240; tick < 300; tick++)
            foreach (SquadSend send in exchange5.Tick(tick * 1f, rows1))
                if (send.Target == silent1) probesNewExpedition++;
        Check(probesNewExpedition == SquadCapability.ProbeBudget,
            "S5: a new expedition probes the peer again - a machine whose plugin only loaded later is still found - and stays bounded");

        // ---- 6: ShareWithEveryPlayer = false never probes ---------------------------------------------
        const ulong local6 = 0x2601UL, silent6 = 0x2602UL, named6 = 0x2603UL;
        var sink6 = new List<string>();
        SquadExchange exchange6 = ProbeExchange(sink6, local6, false, silent6, named6);
        exchange6.Capability.SetBroadcast(false);
        exchange6.SetAllowlist(new List<ulong> { named6 });
        List<CombatPlayer> rows6 = ProbeRows(local6, silent6, named6);
        int toSilent6 = 0, toNamed6 = 0;
        for (int tick = 0; tick < 600; tick++)
        {
            foreach (SquadSend send in exchange6.Tick(tick * 1f, rows6))
            {
                if (send.Target == silent6) toSilent6++;
                else if (send.Target == named6) toNamed6++;
            }
        }
        Check(toSilent6 == 0,
            "S6: with ShareWithEveryPlayer off an unproven peer receives nothing at all - not even a greeting (got " + toSilent6 + ")");
        Check(!exchange6.Capability.MaySend(silent6)
            && exchange6.Capability.EvidenceOf(silent6) == SquadEvidence.Nothing,
            "S6: and the gate says so: no evidence, no packet");
        Check(toNamed6 > 0,
            "S6: while the peer named in the configuration keeps being served (got " + toNamed6 + " packets)");

        Console.WriteLine("PROBE-RULE: unproven=" + SquadCapability.ProbeBudget
            + " greeting(s) per expedition and no row; proven/allowlisted=unchanged cadence; sharing off=0 packets");
    }

    // One machine with a local seat and the given human peers, exactly as the runtime builds them from the
    // game's own slot list. `sink` collects the exchange's own diagnostic lines.
    private static SquadExchange ProbeExchange(List<string> sink, ulong local, bool localIsMaster, params ulong[] peers)
    {
        var exchange = new SquadExchange();
        if (sink != null) exchange.Report = sink.Add;
        exchange.Configure(local, 0, 1, 2, 3);
        var roster = new List<SquadPeer> { new SquadPeer(local, 0, true, false, localIsMaster) };
        for (int index = 0; index < peers.Length; index++)
            roster.Add(new SquadPeer(peers[index], index + 1, false, false, false));
        exchange.SetRoster(roster, localIsMaster);
        return exchange;
    }

    // A measured row per lookup, so a host really has something to send and "no row went out" means the gate
    // refused it rather than that there was nothing to send.
    private static List<CombatPlayer> ProbeRows(ulong local, params ulong[] peers)
    {
        var rows = new List<CombatPlayer>();
        var own = new CombatPlayer(local) { DisplaySlot = 0 };
        own.Total.AddEnemyDamage(250);
        rows.Add(own);
        for (int index = 0; index < peers.Length; index++)
        {
            var peer = new CombatPlayer(peers[index]) { DisplaySlot = index + 1 };
            peer.Total.AddEnemyDamage(100 + index);
            rows.Add(peer);
        }
        return rows;
    }
}
