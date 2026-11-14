// The GTFO-API transport, as a managed double.
//
// `SquadNet.TryBindApi` looks for `GTFO.API.NetworkAPI` in the loaded assemblies by reflection and binds its
// `RegisterFreeSizedEvent` / `InvokeFreeSizedEvent` pair, so this type is the real thing that code path finds: with
// it in the suite, `SquadNet.TryApiSend` performs a genuine reflective invoke against a counter instead of silently
// doing nothing, and a physical packet on the API route can be counted separately from one on the game's own
// channel (`SNet.Core.SendBytes`). That separation is what the probe contract is measured on: one *physical* packet
// per transport, not one logical send.
//
// What this double does not simulate: GTFO-API's own envelope (key, magic, version signature), its registration
// registry, or Steam delivery. It records that the mod asked that transport to put bytes on the wire for a peer, and
// for whom - which is the whole question a probe budget is about.
using System;
using System.Collections.Generic;
using SNetwork;

namespace GTFO.API
{
    public delegate void FreeSizedEventHandler(ulong sender, byte[] payload);

    // The channel enum `InvokeFreeSizedEvent` takes. `TryBindApi` parses `GameOrderCritical` out of it by name, so
    // the member has to exist under exactly that name - the same value this build asks the game's own channel for.
    public enum NetworkChannel { GameOrderCritical }

    public static class NetworkAPI
    {
        // Every byte run this machine handed to the API transport, and for whom.
        public static int Sends;
        public static ulong LastTarget;
        public static string LastEvent;
        public static FreeSizedEventHandler Handler;
        // Set by a scenario that needs to see what happens when the API transport itself fails.
        public static bool Fail;

        public static void Reset()
        {
            Sends = 0;
            LastTarget = 0UL;
            LastEvent = null;
            Fail = false;
            PerPeer.Clear();
        }

        public static readonly Dictionary<ulong, int> PerPeer = new Dictionary<ulong, int>();

        public static int SendsTo(ulong lookup) => PerPeer.TryGetValue(lookup, out int sent) ? sent : 0;

        public static void RegisterFreeSizedEvent(string name, FreeSizedEventHandler handler)
        {
            Handler = handler;
            LastEvent = name;
        }

        public static void InvokeFreeSizedEvent(string name, byte[] payload, SNet_Player target, NetworkChannel channel)
        {
            if (Fail) throw new InvalidOperationException("the GTFO-API transport is not usable on this machine");
            Sends++;
            LastEvent = name;
            LastTarget = target == null ? 0UL : target.Lookup;
            int before = SendsTo(LastTarget);
            PerPeer[LastTarget] = before + 1;
        }
    }
}
