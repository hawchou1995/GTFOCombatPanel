using HarmonyLib;
using Player;

namespace HawChow.GTFOCombatPanel;

// The one native call that still knows who the pack belonged to. `PlayerAgent.GiveAmmoRel(giverAgent,
// ammoStandardRel, ammoSpecialRel, ammoClassRel)` runs on the machine that opened the pack, once per
// receiver, and its receiver agent is the instance the call lands on. The `pAmmoGive` that follows
// carries the receiver and the three shares and no giver, and the authority relays it, so the pair has to
// be remembered here for the pickup to be credited to the player who used the pack. Nothing is written
// to the game: the prefix only reads the three parameters of the call it is already inside.
[HarmonyPatch(typeof(PlayerAgent), nameof(PlayerAgent.GiveAmmoRel))]
internal static class SupplyGiverPatch
{
    [HarmonyPrefix]
    private static void Prefix(PlayerAgent __instance, PlayerAgent giverAgent, float ammoStandardRel, float ammoSpecialRel, float ammoClassRel)
        => SupplyTracker.NoteGive(__instance, giverAgent, ammoStandardRel, ammoSpecialRel, ammoClassRel);
}
