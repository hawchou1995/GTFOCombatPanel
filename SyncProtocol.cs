using System;
using System.Runtime.InteropServices;
using System.Text;

namespace HawChow.GTFOCombatPanel;

// Which figures a snapshot carries, and which of them are estimates rather than observations. The
// flags travel with the numbers so a client prints exactly the same "~" and "—" the host would.
[Flags]
public enum SyncFlags : uint
{
    None = 0,
    EnemyDamageObserved = 1u << 0,
    EnemyDamageEstimated = 1u << 1,
    FriendlyFireObserved = 1u << 2,
    FriendlyFireEstimated = 1u << 3,
    AccuracyObserved = 1u << 4,
    AccuracyEstimated = 1u << 5,
    SquadDamageKnown = 1u << 6,
    StatisticsAvailable = 1u << 7,
    SupportRole = 1u << 8,
    ScanEstimated = 1u << 9,
    LoneSurvivorPresent = 1u << 10,
    PrimaryPresent = 1u << 11,
    PrimaryAccuracyObserved = 1u << 12,
    PrimaryAccuracyEstimated = 1u << 13,
    PrimaryDamageObserved = 1u << 14,
    PrimaryDamageEstimated = 1u << 15,
    PrimaryFriendlyFireObserved = 1u << 16,
    PrimaryFriendlyFireEstimated = 1u << 17,
    SpecialPresent = 1u << 18,
    SpecialAccuracyObserved = 1u << 19,
    SpecialAccuracyEstimated = 1u << 20,
    SpecialDamageObserved = 1u << 21,
    SpecialDamageEstimated = 1u << 22,
    SpecialFriendlyFireObserved = 1u << 23,
    SpecialFriendlyFireEstimated = 1u << 24,
    ClassPresent = 1u << 25,
    MeleePresent = 1u << 26,
    MeleeDamageObserved = 1u << 27,
    MeleeDamageEstimated = 1u << 28,
    MeleeKillsObserved = 1u << 29,
    ScoutKillsObserved = 1u << 30,
    // The squad total in this row adds up the rows *that host* could measure, which is not necessarily the
    // whole squad: a host that has no row for a slotted player, or a lobby whose host runs no mod at all,
    // sums only what it can see. The flag says so, so a receiving machine can print every share built on
    // that denominator as an estimate instead of as a measurement. Absent (the default) the total is the
    // whole squad as the sending host counted it.
    SquadDamagePartial = 1u << 31,
}
// Everything a peer cannot work out on its own, as a struct a network serializer can copy field by
// field. Every member is a primitive: no strings, no collections, no game types. Names travel as
// packed UTF-8 words, so the whole file is exercised by the smoke suite without Unity.
public struct SyncSnapshot
{
    public const int NameWords = 6;                       // 24 UTF-8 bytes per weapon name
    public const int NameCapacity = NameWords * 4;

    public ulong Owner;
    public uint Flags;
    public uint Shots, Hits, WeakHits;
    public float EnemyDamage, FriendlyFire, SquadEnemyDamage;
    public uint KillMinor, KillElite, KillBoss, KillMelee, KillScout, Stagger, Revives, Downs, LoneSurvivor, FriendlyFireDeaths;
    public uint SupplyUses, ScanUses, ScanFirstMarks, ScanMarks;
    public float SupplyEfficiencySum;
    public uint SentryShots, SentryHits, MineDeployments, MinePickups, MineTriggers, GlueShots, GlueHits, GluedEnemies;
    public float SentryDamage, MineDamage, MeleeEnemyDamage;

    public uint PrimaryGearCategory, PrimaryArchetypeId, PrimaryShots, PrimaryHits, PrimaryWeakHits;
    public float PrimaryEnemyDamage, PrimaryFriendlyFire;
    public uint PrimaryName0, PrimaryName1, PrimaryName2, PrimaryName3, PrimaryName4, PrimaryName5;

    public uint SpecialGearCategory, SpecialArchetypeId, SpecialShots, SpecialHits, SpecialWeakHits;
    public float SpecialEnemyDamage, SpecialFriendlyFire;
    public uint SpecialName0, SpecialName1, SpecialName2, SpecialName3, SpecialName4, SpecialName5;

    // The two rows that carry a name but no counters of their own: the board prints them beside the two
    // weapon rows, and a client cannot name another player's gear well enough to fill them.
    public uint ClassArchetypeId;
    public uint ClassName0, ClassName1, ClassName2, ClassName3, ClassName4, ClassName5;
    public uint MeleeArchetypeId;
    public uint MeleeName0, MeleeName1, MeleeName2, MeleeName3, MeleeName4, MeleeName5;

    // ---- v7: the pre-death state of every kill, the player's own supply consumption, and the health the
    // player actually lost. They are appended after the v6 members on purpose: every v6 field keeps its
    // offset, so the only thing that changed about the wire is the length, which the envelope states and
    // which the receiver now checks exactly (SquadPackets.Parse, SyncProtocol.Decode).
    //
    // Sleeping kills: a kill is worth more when the target was never woken, and only the machine that saw
    // the killing blow knows which state it was in. "Awake" is the plain kill counter above; the figures
    // here are the sleeping part of the same tier, never an addition to it.
    public uint SleepingMinorKills, SleepingEliteKills, SleepingBossKills, SleepingMeleeKills, SleepingScoutKills;
    // Kills whose pre-death state could not be observed at all. Kept as their own figures so a receiver
    // never has to guess: "could not tell" is not "awake", and it is not "sleeping" either.
    public uint UnknownStateMinorKills, UnknownStateEliteKills, UnknownStateBossKills, UnknownStateMeleeKills, UnknownStateScoutKills;
    // The player's own supply consumption: total uses it actually consumed, and how many of those were
    // ammo packs. Only the machine that owns the backpack counts them, so they travel with - and are
    // merged like - the supply pair, never with the host-measured tool counters.
    public uint SelfSupplyUses, SelfAmmoUses;
    // Cumulative health this player actually lost. A zero means "no loss reported", which never erases a
    // figure somebody did report; see SyncProtocol.CombineAmount.
    public float HealthLost;

    public bool Has(SyncFlags flag) => (Flags & (uint)flag) != 0;
    public void Set(SyncFlags flag, bool on)
    {
        if (on) Flags |= (uint)flag; else Flags &= ~(uint)flag;
    }

    public void SetPrimaryName(string name)
    {
        var words = new[] { PrimaryName0, PrimaryName1, PrimaryName2, PrimaryName3, PrimaryName4, PrimaryName5 };
        SyncProtocol.PackName(name, words);
        PrimaryName0 = words[0]; PrimaryName1 = words[1]; PrimaryName2 = words[2];
        PrimaryName3 = words[3]; PrimaryName4 = words[4]; PrimaryName5 = words[5];
    }
    public string GetPrimaryName() => SyncProtocol.UnpackName(
        new[] { PrimaryName0, PrimaryName1, PrimaryName2, PrimaryName3, PrimaryName4, PrimaryName5 });

    public void SetSpecialName(string name)
    {
        var words = new[] { SpecialName0, SpecialName1, SpecialName2, SpecialName3, SpecialName4, SpecialName5 };
        SyncProtocol.PackName(name, words);
        SpecialName0 = words[0]; SpecialName1 = words[1]; SpecialName2 = words[2];
        SpecialName3 = words[3]; SpecialName4 = words[4]; SpecialName5 = words[5];
    }
    public string GetSpecialName() => SyncProtocol.UnpackName(
        new[] { SpecialName0, SpecialName1, SpecialName2, SpecialName3, SpecialName4, SpecialName5 });

    public void SetClassName(string name)
    {
        var words = new[] { ClassName0, ClassName1, ClassName2, ClassName3, ClassName4, ClassName5 };
        SyncProtocol.PackName(name, words);
        ClassName0 = words[0]; ClassName1 = words[1]; ClassName2 = words[2];
        ClassName3 = words[3]; ClassName4 = words[4]; ClassName5 = words[5];
    }
    public string GetClassName() => SyncProtocol.UnpackName(
        new[] { ClassName0, ClassName1, ClassName2, ClassName3, ClassName4, ClassName5 });

    public void SetMeleeName(string name)
    {
        var words = new[] { MeleeName0, MeleeName1, MeleeName2, MeleeName3, MeleeName4, MeleeName5 };
        SyncProtocol.PackName(name, words);
        MeleeName0 = words[0]; MeleeName1 = words[1]; MeleeName2 = words[2];
        MeleeName3 = words[3]; MeleeName4 = words[4]; MeleeName5 = words[5];
    }
    public string GetMeleeName() => SyncProtocol.UnpackName(
        new[] { MeleeName0, MeleeName1, MeleeName2, MeleeName3, MeleeName4, MeleeName5 });
}

// Pure rules of the exchange: what goes on the wire, what lands on the receiving side, and when the
// two sides are allowed to talk at all. No Unity, no IL2CPP, so every branch here is unit tested.
public static class SyncProtocol
{
    // Bumped whenever the struct changes shape or the envelope's layout moves. Peers exchange it in the
    // hello so a mismatched pair of builds stays silent instead of decoding each other's fields.
    //
    // 7: the pre-death state of every kill (sleeping / unknown), this player's own supply consumption and
    // its cumulative health lost travel with the row, and a host whose squad total covers only the rows it
    // could measure says so. A v6 peer is therefore refused by name (`VersionMismatch`) rather than being
    // read with the wrong length: its body is 304 bytes where this build reads 352.
    public const uint Version = 7;
    // The two packet kinds the envelope carries. They live here (rather than in the Unity-facing transport)
    // so the headless framing tests and the runtime sender agree on them by construction.
    public const ushort KindHello = 1;
    public const ushort KindSnapshot = 2;
    // The snapshot is all primitives, so it goes on the wire as the bytes it already is. Its size is
    // asserted in the smoke suite, and every packet carries that size as a length, so a build that added
    // a member can never be read as though it had not.
    public static int SnapshotSize => Marshal.SizeOf<SyncSnapshot>();

    public static byte[] Encode(in SyncSnapshot snapshot)
    {
        var bytes = new byte[SnapshotSize];
        var copy = snapshot;
        MemoryMarshal.Write(bytes, ref copy);
        return bytes;
    }

    // Exact, not "at least": the envelope declares how many bytes of body it carries, and a body that is
    // not exactly one snapshot is a build that disagrees about the struct. Reading a longer body would
    // silently decode whichever fields happen to be first; reading a shorter one is already refused. A
    // caller that holds a bigger buffer says how much of it is the packet (SquadNet.TryAccept does).
    public static bool Decode(byte[] buffer, int offset, int count, out SyncSnapshot snapshot)
    {
        snapshot = default;
        if (buffer == null || count != SnapshotSize || offset < 0 || buffer.Length - offset < SnapshotSize) return false;
        snapshot = MemoryMarshal.Read<SyncSnapshot>(new ReadOnlySpan<byte>(buffer, offset, SnapshotSize));
        return true;
    }

    // ---------------------------------------------------------------- names

    public static void PackName(string name, uint[] words)
    {
        if (words == null) return;
        for (int i = 0; i < words.Length; i++) words[i] = 0;
        if (string.IsNullOrEmpty(name)) return;
        int capacity = words.Length * 4;
        var buffer = new byte[capacity];
        var chars = name.ToCharArray();
        int length = 0;
        int index = 0;
        while (index < chars.Length)
        {
            // A surrogate pair is one code point: take both halves or neither, never half of a pair.
            int take = index + 1 < chars.Length && char.IsHighSurrogate(chars[index]) && char.IsLowSurrogate(chars[index + 1]) ? 2 : 1;
            int size = Encoding.UTF8.GetByteCount(chars, index, take);
            if (length + size > capacity) break;
            length += Encoding.UTF8.GetBytes(chars, index, take, buffer, length);
            index += take;
        }
        for (int i = 0; i < length; i++) words[i / 4] |= (uint)buffer[i] << ((i % 4) * 8);
    }

    public static string UnpackName(uint[] words)
    {
        if (words == null || words.Length == 0) return string.Empty;
        var buffer = new byte[words.Length * 4];
        int length = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            byte value = (byte)((words[i / 4] >> ((i % 4) * 8)) & 0xFF);
            if (value == 0) break;
            buffer[length++] = value;
        }
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, length);
    }

    // ---------------------------------------------------------------- capture

    // The slots that get their own weapon row on the board, passed in as plain ints so this file keeps
    // no reference to the game's enum.
    // `squadDamagePartial` says the total above covers only the rows this sender could measure. The
    // receiving side prints every share it builds on such a total as an estimate, so a host whose store is
    // missing a slotted player - a late joiner it has not tracked yet, a player whose row was pruned, or a
    // lobby whose host runs no mod at all - can never be mistaken for the whole squad's measurement.
    public static SyncSnapshot Capture(CombatPlayer player, int primarySlot, int specialSlot,
        int classSlot, int meleeSlot, double squadDamage, bool squadDamageKnown, bool squadDamagePartial = false)
    {
        var snap = new SyncSnapshot { Owner = player == null ? 0UL : player.Lookup };
        if (player == null) return snap;

        PlayerCombatStats total = player.Total;
        Capture(total, ref snap,
            SyncFlags.EnemyDamageObserved, SyncFlags.EnemyDamageEstimated,
            SyncFlags.FriendlyFireObserved, SyncFlags.FriendlyFireEstimated,
            SyncFlags.AccuracyObserved, SyncFlags.AccuracyEstimated);
        snap.EnemyDamage = (float)total.EnemyDamage;
        snap.FriendlyFire = (float)total.FriendlyFireDamage;
        snap.Shots = Clamp(total.ShotsFired);
        snap.Hits = Clamp(total.Hits);
        snap.WeakHits = Clamp(total.WeakspotHits);
        if (squadDamageKnown)
        {
            snap.SquadEnemyDamage = (float)squadDamage;
            snap.Set(SyncFlags.SquadDamageKnown, true);
            snap.Set(SyncFlags.SquadDamagePartial, squadDamagePartial);
        }
        snap.Set(SyncFlags.StatisticsAvailable, player.StatisticsAvailable);
        snap.Set(SyncFlags.SupportRole, player.SupportRole);

        ImpactCounters impact = player.Impact;
        snap.KillMinor = Clamp(impact.MinorKills);
        snap.KillElite = Clamp(impact.EliteKills);
        snap.KillBoss = Clamp(impact.BossKills);
        snap.KillMelee = Clamp(impact.MeleeKills);
        snap.KillScout = Clamp(impact.ScoutKills);
        snap.Set(SyncFlags.MeleeKillsObserved, impact.MeleeKillsObserved);
        snap.Set(SyncFlags.ScoutKillsObserved, impact.ScoutKillsObserved);
        snap.Stagger = Clamp(impact.Stagger);
        snap.Revives = Clamp(impact.Revives);
        snap.Downs = Clamp(impact.Downs);
        snap.LoneSurvivor = Clamp(impact.LoneSurvivor);
        snap.FriendlyFireDeaths = Clamp(impact.FriendlyFireDeaths);
        snap.SentryShots = Clamp(impact.SentryShots); snap.SentryHits = Clamp(impact.SentryHits);
        snap.SentryDamage = (float)impact.SentryDamage;
        snap.MineDeployments = Clamp(impact.MineDeployments); snap.MinePickups = Clamp(impact.MinePickups); snap.MineTriggers = Clamp(impact.MineTriggers);
        snap.MineDamage = (float)impact.MineDamage;
        snap.GlueShots = Clamp(impact.GlueShots); snap.GlueHits = Clamp(impact.GlueHits); snap.GluedEnemies = Clamp(impact.GluedEnemies);
        snap.SupplyUses = Clamp(impact.SupplyUses);
        snap.SupplyEfficiencySum = (float)impact.SupplyEfficiencySum;
        snap.ScanUses = Clamp(impact.ScanUses);
        snap.ScanFirstMarks = Clamp(impact.ScanFirstMarks);
        snap.ScanMarks = Clamp(impact.ScanMarks);
        snap.Set(SyncFlags.ScanEstimated, impact.ScanEstimated);
        snap.Set(SyncFlags.LoneSurvivorPresent, true);
        // v7: the split of the kill counters above by the state the target was in before the killing blow,
        // this player's own supply consumption, and the health it actually lost.
        snap.SleepingMinorKills = Clamp(impact.SleepingMinorKills);
        snap.SleepingEliteKills = Clamp(impact.SleepingEliteKills);
        snap.SleepingBossKills = Clamp(impact.SleepingBossKills);
        snap.SleepingMeleeKills = Clamp(impact.SleepingMeleeKills);
        snap.SleepingScoutKills = Clamp(impact.SleepingScoutKills);
        snap.UnknownStateMinorKills = Clamp(impact.UnknownStateMinorKills);
        snap.UnknownStateEliteKills = Clamp(impact.UnknownStateEliteKills);
        snap.UnknownStateBossKills = Clamp(impact.UnknownStateBossKills);
        snap.UnknownStateMeleeKills = Clamp(impact.UnknownStateMeleeKills);
        snap.UnknownStateScoutKills = Clamp(impact.UnknownStateScoutKills);
        snap.SelfSupplyUses = Clamp(impact.SelfSupplyUses);
        snap.SelfAmmoUses = Clamp(impact.SelfAmmoUses);
        snap.HealthLost = PositiveFinite((float)impact.HealthLost);

        WeaponCombatStats primary = SlotOf(player, primarySlot);
        if (primary != null)
        {
            snap.Set(SyncFlags.PrimaryPresent, true);
            snap.PrimaryArchetypeId = primary.ArchetypeId;
            snap.PrimaryGearCategory = primary.GearCategoryId;
            snap.PrimaryShots = Clamp(primary.Counters.ShotsFired);
            snap.PrimaryHits = Clamp(primary.Counters.Hits);
            snap.PrimaryWeakHits = Clamp(primary.Counters.WeakspotHits);
            snap.PrimaryEnemyDamage = (float)primary.Counters.EnemyDamage;
            snap.PrimaryFriendlyFire = (float)primary.Counters.FriendlyFireDamage;
            Capture(primary.Counters, ref snap,
                SyncFlags.PrimaryDamageObserved, SyncFlags.PrimaryDamageEstimated,
                SyncFlags.PrimaryFriendlyFireObserved, SyncFlags.PrimaryFriendlyFireEstimated,
                SyncFlags.PrimaryAccuracyObserved, SyncFlags.PrimaryAccuracyEstimated);
            snap.SetPrimaryName(primary.Name);
        }

        WeaponCombatStats special = SlotOf(player, specialSlot);
        if (special != null)
        {
            snap.Set(SyncFlags.SpecialPresent, true);
            snap.SpecialArchetypeId = special.ArchetypeId;
            snap.SpecialGearCategory = special.GearCategoryId;
            snap.SpecialShots = Clamp(special.Counters.ShotsFired);
            snap.SpecialHits = Clamp(special.Counters.Hits);
            snap.SpecialWeakHits = Clamp(special.Counters.WeakspotHits);
            snap.SpecialEnemyDamage = (float)special.Counters.EnemyDamage;
            snap.SpecialFriendlyFire = (float)special.Counters.FriendlyFireDamage;
            Capture(special.Counters, ref snap,
                SyncFlags.SpecialDamageObserved, SyncFlags.SpecialDamageEstimated,
                SyncFlags.SpecialFriendlyFireObserved, SyncFlags.SpecialFriendlyFireEstimated,
                SyncFlags.SpecialAccuracyObserved, SyncFlags.SpecialAccuracyEstimated);
            snap.SetSpecialName(special.Name);
        }

        WeaponCombatStats utility = SlotOf(player, classSlot);
        if (utility != null)
        {
            snap.Set(SyncFlags.ClassPresent, true);
            snap.ClassArchetypeId = utility.ArchetypeId;
            snap.SetClassName(utility.Name);
        }

        WeaponCombatStats melee = SlotOf(player, meleeSlot);
        if (melee != null)
        {
            snap.Set(SyncFlags.MeleePresent, true);
            snap.MeleeArchetypeId = melee.ArchetypeId;
            snap.SetMeleeName(melee.Name);
            snap.MeleeEnemyDamage = (float)melee.Counters.EnemyDamage;
            snap.Set(SyncFlags.MeleeDamageObserved, melee.Counters.EnemyDamageObserved);
            snap.Set(SyncFlags.MeleeDamageEstimated, melee.Counters.EnemyDamageEstimated);
        }

        return snap;
    }

    // ---------------------------------------------------------------- apply

    // Write a received snapshot onto the store. Every figure is restored, never added: the host's
    // snapshot is the whole truth about that player at that moment, and adding it to numbers this
    // machine happened to observe would double count the damage both of them saw.
    public static void Apply(CombatPlayer target, in SyncSnapshot snap, int primarySlot, int specialSlot, int classSlot, int meleeSlot)
    {
        if (target == null) return;

        PlayerCombatStats total = target.Total;
        Restore(total, snap.Shots, snap.Hits, snap.WeakHits,
            snap.Has(SyncFlags.AccuracyObserved), snap.Has(SyncFlags.AccuracyEstimated));
        Restore(total, snap.EnemyDamage,
            snap.Has(SyncFlags.EnemyDamageObserved), snap.Has(SyncFlags.EnemyDamageEstimated));
        RestoreFriendly(total, snap.FriendlyFire,
            snap.Has(SyncFlags.FriendlyFireObserved), snap.Has(SyncFlags.FriendlyFireEstimated));

        target.StatisticsAvailable = snap.Has(SyncFlags.StatisticsAvailable);
        target.SupportRole = snap.Has(SyncFlags.SupportRole);

        ImpactCounters impact = target.Impact;
        impact.MinorKills = snap.KillMinor;
        impact.EliteKills = snap.KillElite;
        impact.BossKills = snap.KillBoss;
        impact.MeleeKills = snap.KillMelee;
        impact.ScoutKills = snap.KillScout;
        impact.MeleeKillsObserved = snap.Has(SyncFlags.MeleeKillsObserved);
        impact.ScoutKillsObserved = snap.Has(SyncFlags.ScoutKillsObserved);
        impact.Stagger = snap.Stagger;
        impact.Revives = snap.Revives;
        impact.Downs = snap.Downs > int.MaxValue ? int.MaxValue : (int)snap.Downs;
        impact.LoneSurvivor = snap.LoneSurvivor > int.MaxValue ? int.MaxValue : (int)snap.LoneSurvivor;
        impact.FriendlyFireDeaths = snap.FriendlyFireDeaths > int.MaxValue ? int.MaxValue : (int)snap.FriendlyFireDeaths;
        impact.SentryShots = snap.SentryShots; impact.SentryHits = snap.SentryHits;
        impact.SentryDamage = PositiveFinite(snap.SentryDamage);
        impact.MineDeployments = snap.MineDeployments; impact.MinePickups = snap.MinePickups; impact.MineTriggers = snap.MineTriggers;
        impact.MineDamage = PositiveFinite(snap.MineDamage);
        impact.GlueShots = snap.GlueShots; impact.GlueHits = snap.GlueHits; impact.GluedEnemies = snap.GluedEnemies;
        impact.SupplyUses = snap.SupplyUses;
        impact.SupplyEfficiencySum = snap.SupplyEfficiencySum;
        impact.ScanUses = snap.ScanUses;
        impact.ScanFirstMarks = snap.ScanFirstMarks;
        impact.ScanMarks = snap.ScanMarks;
        impact.ScanEstimated = snap.Has(SyncFlags.ScanEstimated);
        // v7 fields, restored exactly like the counters above: a snapshot is the whole truth about that
        // player, so these are written, never added. "Could not tell" is restored as its own figure rather
        // than being folded into either the awake counters or the sleeping ones.
        impact.SleepingMinorKills = snap.SleepingMinorKills;
        impact.SleepingEliteKills = snap.SleepingEliteKills;
        impact.SleepingBossKills = snap.SleepingBossKills;
        impact.SleepingMeleeKills = snap.SleepingMeleeKills;
        impact.SleepingScoutKills = snap.SleepingScoutKills;
        impact.UnknownStateMinorKills = snap.UnknownStateMinorKills;
        impact.UnknownStateEliteKills = snap.UnknownStateEliteKills;
        impact.UnknownStateBossKills = snap.UnknownStateBossKills;
        impact.UnknownStateMeleeKills = snap.UnknownStateMeleeKills;
        impact.UnknownStateScoutKills = snap.UnknownStateScoutKills;
        impact.SelfSupplyUses = snap.SelfSupplyUses;
        impact.SelfAmmoUses = snap.SelfAmmoUses;
        impact.HealthLost = PositiveFinite(snap.HealthLost);
        if (snap.Has(SyncFlags.PrimaryPresent))
        {
            WeaponCombatStats weapon = target.Weapon(primarySlot, snap.PrimaryArchetypeId,
                snap.GetPrimaryName(), snap.PrimaryGearCategory, true);
            Restore(weapon.Counters, snap.PrimaryShots, snap.PrimaryHits, snap.PrimaryWeakHits,
                snap.Has(SyncFlags.PrimaryAccuracyObserved), snap.Has(SyncFlags.PrimaryAccuracyEstimated));
            Restore(weapon.Counters, snap.PrimaryEnemyDamage,
                snap.Has(SyncFlags.PrimaryDamageObserved), snap.Has(SyncFlags.PrimaryDamageEstimated));
            RestoreFriendly(weapon.Counters, snap.PrimaryFriendlyFire,
                snap.Has(SyncFlags.PrimaryFriendlyFireObserved), snap.Has(SyncFlags.PrimaryFriendlyFireEstimated));
        }

        if (snap.Has(SyncFlags.SpecialPresent))
        {
            WeaponCombatStats weapon = target.Weapon(specialSlot, snap.SpecialArchetypeId,
                snap.GetSpecialName(), snap.SpecialGearCategory, true);
            Restore(weapon.Counters, snap.SpecialShots, snap.SpecialHits, snap.SpecialWeakHits,
                snap.Has(SyncFlags.SpecialAccuracyObserved), snap.Has(SyncFlags.SpecialAccuracyEstimated));
            Restore(weapon.Counters, snap.SpecialEnemyDamage,
                snap.Has(SyncFlags.SpecialDamageObserved), snap.Has(SyncFlags.SpecialDamageEstimated));
            RestoreFriendly(weapon.Counters, snap.SpecialFriendlyFire,
                snap.Has(SyncFlags.SpecialFriendlyFireObserved), snap.Has(SyncFlags.SpecialFriendlyFireEstimated));
        }

        // The tool row carries a name only; the melee row carries both its name and the existing GearMelee
        // damage measurement so a client settlement board renders the same melee context as the host.
        if (snap.Has(SyncFlags.ClassPresent)) target.Weapon(classSlot, snap.ClassArchetypeId, snap.GetClassName(), 0, true);
        if (snap.Has(SyncFlags.MeleePresent))
        {
            WeaponCombatStats melee = target.Weapon(meleeSlot, snap.MeleeArchetypeId, snap.GetMeleeName(), 0, true);
            melee.Counters.RestoreEnemyDamage(snap.MeleeEnemyDamage,
                snap.Has(SyncFlags.MeleeDamageObserved), snap.Has(SyncFlags.MeleeDamageEstimated));
        }
    }
    // A snapshot is worth applying when it describes somebody else. Our own row is always the one
    // this machine measured itself: it is exact here and only an estimate on the host.
    public static bool Accepts(ulong localLookup, in SyncSnapshot snap)
        => snap.Owner != 0 && snap.Owner != localLookup;


    // Whether `candidate` is the same run as `current`, or a newer one. An older generation is a straggler
    // from a run that has already ended and must not be adopted over the one in hand.
    public static bool IsNewerOrSame(uint candidate, uint current)
        => candidate == current || unchecked(candidate - current) < 0x80000000u;

    // ---------------------------------------------------------------- combining two machines' rows

    // Two machines describe the same player with different eyes, and both descriptions are worth keeping:
    // the host applied every blow and read every backpack, so its damage and its counters are the blow; the
    // player's own machine fired the rays, so its hit rate, its supply uses and its scans are the exact
    // ones. `Combine` folds a second description of a row into the one already held for it, field by field,
    // so neither source can erase what the other alone could see.
    //
    // The rule per field group is: a figure that was observed beats one that was estimated; when both were
    // observed (or both estimated) the side that owns that field wins - the host for damage, the player's
    // own machine for aim, supply and scans; and when both sides are the same kind of source the newer one
    // wins, which is what makes applying the same packet twice land on the same numbers.
    public static SyncSnapshot Combine(in SyncSnapshot existing, bool existingIsHost,
        in SyncSnapshot incoming, bool incomingIsHost)
    {
        SyncSnapshot result = incoming;
        // Enemy damage and friendly fire: the host is the authority.
        Choose(existing.EnemyDamage, existing.Flags, SyncFlags.EnemyDamageObserved, SyncFlags.EnemyDamageEstimated, existingIsHost,
               incoming.EnemyDamage, incoming.Flags, SyncFlags.EnemyDamageObserved, SyncFlags.EnemyDamageEstimated, incomingIsHost, true,
               out float enemyDamage, out uint enemyFlags);
        result.EnemyDamage = enemyDamage;
        Choose(existing.FriendlyFire, existing.Flags, SyncFlags.FriendlyFireObserved, SyncFlags.FriendlyFireEstimated, existingIsHost,
               incoming.FriendlyFire, incoming.Flags, SyncFlags.FriendlyFireObserved, SyncFlags.FriendlyFireEstimated, incomingIsHost, true,
               out float friendlyFire, out uint friendlyFlags);
        result.FriendlyFire = friendlyFire;
        // Aim: whoever fired counted the rays, so the player's own machine is the authority.
        bool takeAccuracy = Choose(existing.Shots, existing.Flags, SyncFlags.AccuracyObserved, SyncFlags.AccuracyEstimated, existingIsHost,
               incoming.Shots, incoming.Flags, SyncFlags.AccuracyObserved, SyncFlags.AccuracyEstimated, incomingIsHost, false,
               out uint accuracyShots, out uint accuracyFlags);
        if (takeAccuracy) { result.Shots = accuracyShots; result.Hits = incoming.Hits; result.WeakHits = incoming.WeakHits; }
        else { result.Shots = existing.Shots; result.Hits = existing.Hits; result.WeakHits = existing.WeakHits; }
        result.Flags = (result.Flags & ~((uint)SyncFlags.AccuracyObserved | (uint)SyncFlags.AccuracyEstimated)) | accuracyFlags;

        MergeWeapon(existing, incoming, incomingIsHost, ref result, existingIsHost,
            SyncFlags.PrimaryPresent, SyncFlags.PrimaryAccuracyObserved, SyncFlags.PrimaryAccuracyEstimated,
            SyncFlags.PrimaryDamageObserved, SyncFlags.PrimaryDamageEstimated,
            SyncFlags.PrimaryFriendlyFireObserved, SyncFlags.PrimaryFriendlyFireEstimated);
        MergeWeapon(existing, incoming, incomingIsHost, ref result, existingIsHost,
            SyncFlags.SpecialPresent, SyncFlags.SpecialAccuracyObserved, SyncFlags.SpecialAccuracyEstimated,
            SyncFlags.SpecialDamageObserved, SyncFlags.SpecialDamageEstimated,
            SyncFlags.SpecialFriendlyFireObserved, SyncFlags.SpecialFriendlyFireEstimated);
        Choose(existing.MeleeEnemyDamage, existing.Flags, SyncFlags.MeleeDamageObserved, SyncFlags.MeleeDamageEstimated, existingIsHost,
               incoming.MeleeEnemyDamage, incoming.Flags, SyncFlags.MeleeDamageObserved, SyncFlags.MeleeDamageEstimated, incomingIsHost, true,
               out float meleeDamage, out uint meleeFlags);
        result.MeleeEnemyDamage = meleeDamage;
        result.Flags = (result.Flags & ~((uint)SyncFlags.MeleeDamageObserved | (uint)SyncFlags.MeleeDamageEstimated)) | meleeFlags;
        // Counters have no estimate flag: an impact either was counted or it was not, and a zero from one
        // side never erases a count the other side made. The tier totals and the state split of those same
        // kills are one block, taken whole from the side that holds it - the host's whenever the host
        // counted anything - so a cell can never print a subset larger than the count in front of it.
        bool takeIncomingTiers = TakesIncomingKills(existing, existingIsHost, incoming, incomingIsHost,
            CarriesTierKills(existing), CarriesTierKills(incoming));
        SyncSnapshot tierSource = takeIncomingTiers ? incoming : existing;
        result.KillMinor = tierSource.KillMinor;
        result.KillElite = tierSource.KillElite;
        result.KillBoss = tierSource.KillBoss;
        result.SleepingMinorKills = tierSource.SleepingMinorKills;
        result.SleepingEliteKills = tierSource.SleepingEliteKills;
        result.SleepingBossKills = tierSource.SleepingBossKills;
        result.UnknownStateMinorKills = tierSource.UnknownStateMinorKills;
        result.UnknownStateEliteKills = tierSource.UnknownStateEliteKills;
        result.UnknownStateBossKills = tierSource.UnknownStateBossKills;
        // The melee and Scout blocks each carry their own "observed" flag, and each split moves with the
        // block it belongs to.
        bool takeIncomingMelee = TakesIncomingFlaggedKills(existing, existingIsHost, incoming, incomingIsHost,
            SyncFlags.MeleeKillsObserved, CarriesMeleeKills(existing), CarriesMeleeKills(incoming));
        SyncSnapshot meleeSource = takeIncomingMelee ? incoming : existing;
        result.KillMelee = meleeSource.KillMelee;
        result.Set(SyncFlags.MeleeKillsObserved, meleeSource.Has(SyncFlags.MeleeKillsObserved));
        result.SleepingMeleeKills = meleeSource.SleepingMeleeKills;
        result.UnknownStateMeleeKills = meleeSource.UnknownStateMeleeKills;
        bool takeIncomingScout = TakesIncomingFlaggedKills(existing, existingIsHost, incoming, incomingIsHost,
            SyncFlags.ScoutKillsObserved, CarriesScoutKills(existing), CarriesScoutKills(incoming));
        SyncSnapshot scoutSource = takeIncomingScout ? incoming : existing;
        result.KillScout = scoutSource.KillScout;
        result.Set(SyncFlags.ScoutKillsObserved, scoutSource.Has(SyncFlags.ScoutKillsObserved));
        result.SleepingScoutKills = scoutSource.SleepingScoutKills;
        result.UnknownStateScoutKills = scoutSource.UnknownStateScoutKills;
        result.Stagger = CombineCount(existing.Stagger, incoming.Stagger);
        result.Revives = CombineCount(existing.Revives, incoming.Revives);
        result.Downs = CombineCount(existing.Downs, incoming.Downs);
        result.LoneSurvivor = CombineCount(existing.LoneSurvivor, incoming.LoneSurvivor);
        result.FriendlyFireDeaths = CombineCount(existing.FriendlyFireDeaths, incoming.FriendlyFireDeaths);
        result.SentryShots = CombineCount(existing.SentryShots, incoming.SentryShots);
        result.SentryHits = CombineCount(existing.SentryHits, incoming.SentryHits);
        result.MineDeployments = CombineCount(existing.MineDeployments, incoming.MineDeployments);
        result.MinePickups = CombineCount(existing.MinePickups, incoming.MinePickups);
        result.MineTriggers = CombineCount(existing.MineTriggers, incoming.MineTriggers);
        result.GlueShots = CombineCount(existing.GlueShots, incoming.GlueShots);
        result.GlueHits = CombineCount(existing.GlueHits, incoming.GlueHits);
        result.GluedEnemies = CombineCount(existing.GluedEnemies, incoming.GluedEnemies);
        // v7: nothing is left to merge above - every state figure travelled with the block it belongs to,
        // so a sleeping or unreadable kill is never assembled from one machine's figure and printed on
        // another machine's total, and an unreadable one is never folded into an awake count.
        // Health actually lost: an amount, so a zero means "nothing was reported by this side" and never
        // erases a figure the other side reported. The host applied the damage, so its figure is the one
        // that survives a tie.
        result.HealthLost = CombineAmount(existing.HealthLost, existingIsHost, incoming.HealthLost, incomingIsHost);
        float oldSentryDamage = PositiveFinite(existing.SentryDamage), newSentryDamage = PositiveFinite(incoming.SentryDamage);
        float oldMineDamage = PositiveFinite(existing.MineDamage), newMineDamage = PositiveFinite(incoming.MineDamage);
        result.SentryDamage = newSentryDamage > 0 ? newSentryDamage : oldSentryDamage;
        result.MineDamage = newMineDamage > 0 ? newMineDamage : oldMineDamage;
        // Supplies and scans move as pairs: what the player's own machine counted is the exact figure, so it
        // wins whenever it has one; an absent pair never overwrites a pair that is already held.
        if (!(incoming.SupplyUses != 0 && (existing.SupplyUses == 0 || !incomingIsHost)))
        {
            result.SupplyUses = existing.SupplyUses;
            result.SupplyEfficiencySum = existing.SupplyEfficiencySum;
        }
        // The player's own consumption is a pair too, and only the owner can count it: total uses actually
        // consumed and how many of those were ammo packs. It follows the supply pair's rule - the owner's
        // figure wins, an absent pair never displaces a pair already held - and the two numbers never part.
        if (!(incoming.SelfSupplyUses != 0 && (existing.SelfSupplyUses == 0 || !incomingIsHost)))
        {
            result.SelfSupplyUses = existing.SelfSupplyUses;
            result.SelfAmmoUses = existing.SelfAmmoUses;
        }
        if (!(incoming.ScanUses != 0 && (existing.ScanUses == 0 && existing.ScanFirstMarks == 0 && existing.ScanMarks == 0 || !incomingIsHost)))
        {
            result.ScanUses = existing.ScanUses;
            result.ScanFirstMarks = existing.ScanFirstMarks;
            result.ScanMarks = existing.ScanMarks;
            result.Set(SyncFlags.ScanEstimated, existing.Has(SyncFlags.ScanEstimated));
        }
        // The squad total is something only a host can add up, so whichever side carries it carries it. The
        // "partial" bit travels with the figure it qualifies and never on its own: a total that covers only
        // the rows its sender could measure is marked, whichever of the two descriptions it came from.
        if (!incoming.Has(SyncFlags.SquadDamageKnown) && existing.Has(SyncFlags.SquadDamageKnown))
        {
            result.SquadEnemyDamage = existing.SquadEnemyDamage;
            result.Set(SyncFlags.SquadDamageKnown, true);
            result.Set(SyncFlags.SquadDamagePartial, existing.Has(SyncFlags.SquadDamagePartial));
        }
        else if (incoming.Has(SyncFlags.SquadDamageKnown))
        {
            result.Set(SyncFlags.SquadDamagePartial, incoming.Has(SyncFlags.SquadDamagePartial));
        }
        // Everything that is simply present-or-absent is the union of the two descriptions.
        result.Set(SyncFlags.StatisticsAvailable, existing.Has(SyncFlags.StatisticsAvailable) || incoming.Has(SyncFlags.StatisticsAvailable));
        result.Set(SyncFlags.SupportRole, existing.Has(SyncFlags.SupportRole) || incoming.Has(SyncFlags.SupportRole));
        // The tool and melee rows travel as a name and an id; a description that names one is preferred to
        // one that does not, whichever machine it came from.
        CarryName(existing, incoming, incomingIsHost, ref result,
            SyncFlags.ClassPresent, existing.ClassName0, existing.ClassName1, existing.ClassName2, existing.ClassName3, existing.ClassName4, existing.ClassName5,
            existing.ClassArchetypeId, incoming.ClassName0, incoming.ClassName1, incoming.ClassName2, incoming.ClassName3, incoming.ClassName4, incoming.ClassName5,
            incoming.ClassArchetypeId);
        CarryName(existing, incoming, incomingIsHost, ref result,
            SyncFlags.MeleePresent, existing.MeleeName0, existing.MeleeName1, existing.MeleeName2, existing.MeleeName3, existing.MeleeName4, existing.MeleeName5,
            existing.MeleeArchetypeId, incoming.MeleeName0, incoming.MeleeName1, incoming.MeleeName2, incoming.MeleeName3, incoming.MeleeName4, incoming.MeleeName5,
            incoming.MeleeArchetypeId);
        return result;
    }

    // Whether a description holds any figure of a kill block. A row that counted nothing carries none of them,
    // which is not the same as a row whose every kill was awake: both leave an awake part of zero, but only the
    // second is a machine's answer about these kills.
    public static bool CarriesTierKills(in SyncSnapshot snap)
        => snap.KillMinor != 0 || snap.KillElite != 0 || snap.KillBoss != 0
            || snap.SleepingMinorKills != 0 || snap.SleepingEliteKills != 0 || snap.SleepingBossKills != 0
            || snap.UnknownStateMinorKills != 0 || snap.UnknownStateEliteKills != 0 || snap.UnknownStateBossKills != 0;

    public static bool CarriesMeleeKills(in SyncSnapshot snap)
        => snap.KillMelee != 0 || snap.SleepingMeleeKills != 0 || snap.UnknownStateMeleeKills != 0;

    public static bool CarriesScoutKills(in SyncSnapshot snap)
        => snap.KillScout != 0 || snap.SleepingScoutKills != 0 || snap.UnknownStateScoutKills != 0;

    // Which of two descriptions a kill block is taken from: the host's whenever that side holds any figure -
    // the host applied the blows and read the corpses - and the other side's otherwise, zeros and all. Two
    // descriptions from the same kind of source fall back to the newer one, which is what makes folding the
    // same packet in twice land on the same figures.
    private static bool TakesIncomingKills(in SyncSnapshot existing, bool existingIsHost, in SyncSnapshot incoming, bool incomingIsHost,
        bool existingHasFigures, bool incomingHasFigures)
        => existingIsHost != incomingIsHost
            ? incomingIsHost && incomingHasFigures || existingIsHost && !existingHasFigures
            : incomingHasFigures;

    // The same choice for a block that carries its own "observed" flag. The side that observed the kills is the
    // one whose figures are held, whichever side it is; when both did, the host's; when neither did, the block is
    // chosen like any other and the flag stays off, because neither machine claims to have seen it.
    private static bool TakesIncomingFlaggedKills(in SyncSnapshot existing, bool existingIsHost, in SyncSnapshot incoming, bool incomingIsHost,
        SyncFlags observed, bool existingHasFigures, bool incomingHasFigures)
    {
        bool existingObserved = existing.Has(observed), incomingObserved = incoming.Has(observed);
        if (existingObserved != incomingObserved) return incomingObserved;
        if (existingObserved) return incomingIsHost || !existingIsHost;
        return TakesIncomingKills(existing, existingIsHost, incoming, incomingIsHost, existingHasFigures, incomingHasFigures);
    }

    private static void MergeWeapon(in SyncSnapshot existing, in SyncSnapshot incoming, bool incomingIsHost, ref SyncSnapshot result, bool existingIsHost,
        SyncFlags present, SyncFlags accuracyObserved, SyncFlags accuracyEstimated,
        SyncFlags damageObserved, SyncFlags damageEstimated, SyncFlags friendlyObserved, SyncFlags friendlyEstimated)
    {
        if (!incoming.Has(present) && !existing.Has(present)) return;
        result.Set(present, true);
        if (!existing.Has(present)) return;                      // nothing to merge against
        bool primary = present == SyncFlags.PrimaryPresent;
        // Aim of one weapon: the player's own machine again.
        Choose(primary ? existing.PrimaryShots : existing.SpecialShots, existing.Flags, accuracyObserved, accuracyEstimated, existingIsHost,
               primary ? incoming.PrimaryShots : incoming.SpecialShots, incoming.Flags, accuracyObserved, accuracyEstimated, incomingIsHost, false,
               out uint shots, out uint accuracyFlags);
        if (primary) result.PrimaryShots = shots; else result.SpecialShots = shots;
        result.Flags = (result.Flags & ~((uint)accuracyObserved | (uint)accuracyEstimated)) | accuracyFlags;
        // Damage of one weapon: the host.
        Choose(primary ? existing.PrimaryEnemyDamage : existing.SpecialEnemyDamage, existing.Flags, damageObserved, damageEstimated, existingIsHost,
               primary ? incoming.PrimaryEnemyDamage : incoming.SpecialEnemyDamage, incoming.Flags, damageObserved, damageEstimated, incomingIsHost, true,
               out float damage, out uint damageFlags);
        if (primary) result.PrimaryEnemyDamage = damage; else result.SpecialEnemyDamage = damage;
        result.Flags = (result.Flags & ~((uint)damageObserved | (uint)damageEstimated)) | damageFlags;
        Choose(primary ? existing.PrimaryFriendlyFire : existing.SpecialFriendlyFire, existing.Flags, friendlyObserved, friendlyEstimated, existingIsHost,
               primary ? incoming.PrimaryFriendlyFire : incoming.SpecialFriendlyFire, incoming.Flags, friendlyObserved, friendlyEstimated, incomingIsHost, true,
               out float friendly, out uint friendlyFlags);
        if (primary) result.PrimaryFriendlyFire = friendly; else result.SpecialFriendlyFire = friendly;
        result.Flags = (result.Flags & ~((uint)friendlyObserved | (uint)friendlyEstimated)) | friendlyFlags;
        // The weapon's identity travels with it, and its name is never blanked by a description that has none.
        if (primary && string.IsNullOrEmpty(UnpackName(new[] { incoming.PrimaryName0, incoming.PrimaryName1, incoming.PrimaryName2, incoming.PrimaryName3, incoming.PrimaryName4, incoming.PrimaryName5 }))
            && !string.IsNullOrEmpty(UnpackName(new[] { existing.PrimaryName0, existing.PrimaryName1, existing.PrimaryName2, existing.PrimaryName3, existing.PrimaryName4, existing.PrimaryName5 })))
        {
            result.PrimaryName0 = existing.PrimaryName0; result.PrimaryName1 = existing.PrimaryName1; result.PrimaryName2 = existing.PrimaryName2;
            result.PrimaryName3 = existing.PrimaryName3; result.PrimaryName4 = existing.PrimaryName4; result.PrimaryName5 = existing.PrimaryName5;
            result.PrimaryArchetypeId = existing.PrimaryArchetypeId;
            result.PrimaryGearCategory = existing.PrimaryGearCategory;
        }
        if (!primary && string.IsNullOrEmpty(UnpackName(new[] { incoming.SpecialName0, incoming.SpecialName1, incoming.SpecialName2, incoming.SpecialName3, incoming.SpecialName4, incoming.SpecialName5 }))
            && !string.IsNullOrEmpty(UnpackName(new[] { existing.SpecialName0, existing.SpecialName1, existing.SpecialName2, existing.SpecialName3, existing.SpecialName4, existing.SpecialName5 })))
        {
            result.SpecialName0 = existing.SpecialName0; result.SpecialName1 = existing.SpecialName1; result.SpecialName2 = existing.SpecialName2;
            result.SpecialName3 = existing.SpecialName3; result.SpecialName4 = existing.SpecialName4; result.SpecialName5 = existing.SpecialName5;
            result.SpecialArchetypeId = existing.SpecialArchetypeId;
            result.SpecialGearCategory = existing.SpecialGearCategory;
        }
    }

    private static void CarryName(in SyncSnapshot existing, in SyncSnapshot incoming, bool incomingIsHost, ref SyncSnapshot result, SyncFlags present,
        uint existingName0, uint existingName1, uint existingName2, uint existingName3, uint existingName4, uint existingName5, uint existingId,
        uint incomingName0, uint incomingName1, uint incomingName2, uint incomingName3, uint incomingName4, uint incomingName5, uint incomingId)
    {
        if (incoming.Has(present) || !existing.Has(present)) return;
        result.Set(present, true);
        uint[] words = { existingName0, existingName1, existingName2, existingName3, existingName4, existingName5 };
        if (present == SyncFlags.ClassPresent)
        {
            result.ClassName0 = words[0]; result.ClassName1 = words[1]; result.ClassName2 = words[2];
            result.ClassName3 = words[3]; result.ClassName4 = words[4]; result.ClassName5 = words[5];
            result.ClassArchetypeId = existingId;
        }
        else
        {
            result.MeleeName0 = words[0]; result.MeleeName1 = words[1]; result.MeleeName2 = words[2];
            result.MeleeName3 = words[3]; result.MeleeName4 = words[4]; result.MeleeName5 = words[5];
            result.MeleeArchetypeId = existingId;
        }
    }

    // A counted figure: a count from one side is a count somebody made, and a zero never erases one.
    private static uint CombineCount(uint existing, uint incoming) => incoming == 0 ? existing : incoming;

    // A measured amount that carries no provenance bits of its own: a figure either was reported or it was
    // not, and the two descriptions of a row can each hold one. A zero never erases a figure somebody did
    // report - that is the same rule SentryDamage and MineDamage already follow - and when both sides hold
    // one the host's stands, because the host is the machine that applied the damage.
    private static float CombineAmount(float existing, bool existingIsHost, float incoming, bool incomingIsHost)
    {
        float held = PositiveFinite(existing), offered = PositiveFinite(incoming);
        if (offered <= 0) return held;
        if (held <= 0) return offered;
        return incomingIsHost || !existingIsHost ? offered : held;
    }

    // The one decision per measured field: which side's figure and which side's flags survive.
    private static bool Choose(float existingValue, uint existingFlags, SyncFlags existingObserved, SyncFlags existingEstimated, bool existingIsHost,
        float incomingValue, uint incomingFlags, SyncFlags incomingObserved, SyncFlags incomingEstimated, bool incomingIsHost, bool preferHostOnTie,
        out float value, out uint flags)
    {
        int existingRank = Rank(existingFlags, existingObserved, existingEstimated);
        int incomingRank = Rank(incomingFlags, incomingObserved, incomingEstimated);
        bool ours = incomingRank > existingRank
            || incomingRank == existingRank && incomingRank > 0
               && (existingIsHost == incomingIsHost ? true : preferHostOnTie == incomingIsHost);
        value = ours ? incomingValue : existingValue;
        flags = (ours ? incomingFlags : existingFlags) & ((uint)existingObserved | (uint)existingEstimated);
        return ours;
    }

    private static bool Choose(uint existingValue, uint existingFlags, SyncFlags existingObserved, SyncFlags existingEstimated, bool existingIsHost,
        uint incomingValue, uint incomingFlags, SyncFlags incomingObserved, SyncFlags incomingEstimated, bool incomingIsHost, bool preferHostOnTie,
        out uint value, out uint flags)
    {
        bool ours = Choose(existingValue, existingFlags, existingObserved, existingEstimated, existingIsHost,
            incomingValue, incomingFlags, incomingObserved, incomingEstimated, incomingIsHost, preferHostOnTie, out float chosen, out flags);
        value = (uint)chosen;
        return ours;
    }

    // How exact the figure behind a pair of flags is. `Observed` means the field carries a figure and
    // `Estimated` qualifies it, so a field with both is an estimate rather than a measurement - the same
    // reading `CombatMerge.Exactness` makes of a row's own flags.
    private static int Rank(uint flags, SyncFlags observed, SyncFlags estimated)
    {
        if ((flags & (uint)observed) == 0) return 0;
        return (flags & (uint)estimated) != 0 ? 1 : 2;
    }

    private static WeaponCombatStats SlotOf(CombatPlayer player, int slot)
    {
        WeaponCombatStats found = null;
        foreach (WeaponCombatStats weapon in player.Weapons)
        {
            if (weapon.Slot != slot) continue;
            if (found == null || weapon.ArchetypeId != 0) found = weapon;
            if (weapon.ArchetypeId != 0) break;
        }
        return found;
    }

    private static void Capture(PlayerCombatStats source, ref SyncSnapshot snap,
        SyncFlags damageObserved, SyncFlags damageEstimated,
        SyncFlags friendlyObserved, SyncFlags friendlyEstimated,
        SyncFlags accuracyObserved, SyncFlags accuracyEstimated)
    {
        snap.Set(damageObserved, source.EnemyDamageObserved);
        snap.Set(damageEstimated, source.EnemyDamageEstimated);
        snap.Set(friendlyObserved, source.FriendlyFireObserved);
        snap.Set(friendlyEstimated, source.FriendlyFireEstimated);
        snap.Set(accuracyObserved, source.AccuracyObserved);
        snap.Set(accuracyEstimated, source.AccuracyEstimated);
    }

    private static void Restore(PlayerCombatStats target, long shots, long hits, long weak,
        bool observed, bool estimated)
    {
        long fired = Math.Max(0, shots);
        long landed = Math.Max(0, Math.Min(hits, fired));
        target.RestoreAccuracy(fired, landed, Math.Max(0, Math.Min(weak, landed)), observed, estimated);
    }

    private static void Restore(PlayerCombatStats target, double amount, bool observed, bool estimated)
        => target.RestoreEnemyDamage(amount, observed, estimated);

    private static void RestoreFriendly(PlayerCombatStats target, double amount, bool observed, bool estimated)
        => target.RestoreFriendlyFireDamage(amount, observed, estimated);

    private static uint Clamp(long value) => value <= 0 ? 0u : value >= uint.MaxValue ? uint.MaxValue : (uint)value;
    private static float PositiveFinite(float value) => float.IsFinite(value) && value > 0 ? value : 0f;
}
