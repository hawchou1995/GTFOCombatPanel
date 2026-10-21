using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HawChow.GTFOCombatPanel;

// Columns in display order. Damage and its share are one column; the three
// projectile counters are one column; both put their values side by side.
public enum PanelColumn { PlayerName, DamageAndShare, FriendlyFire, Accuracy, WeakspotAccuracy, Projectiles, ImpactLabel, ImpactValue }
public enum PanelTextRole { Neutral, PlayerName }

// One firing window; pellet IDs are local to this window, never shared across weapons.
public sealed class ShotSample
{
    private readonly Dictionary<int, bool> _hits = new Dictionary<int, bool>();
    private int _pellet = -1;
    public int Fired { get; private set; }
    // Set when the pellet count came from ammunition consumed rather than from rays this build could
    // see: pellets per shot are unknown then, so the rate that follows is an estimate and says so.
    public bool Blind;
    public int Hits => _hits.Count;
    public int WeakspotHits
    {
        get { int count = 0; foreach (bool weak in _hits.Values) if (weak) count++; return count; }
    }
    public void BeginPellet() { _pellet = Fired++; }
    public void Contact(bool weakspot)
    {
        // No pellet was opened for this contact, so it cannot be pinned to a shot: drop it. Only the
        // firing window that owns the contact may open one on its behalf (CombatTracker.OnEnemyLimbHit).
        if (_pellet < 0) return;
        _hits.TryGetValue(_pellet, out bool previous);
        _hits[_pellet] = previous || weakspot;
    }
}

public sealed class PlayerCombatStats
{
    public long ShotsFired { get; private set; }
    public long Hits { get; private set; }
    public long WeakspotHits { get; private set; }
    public double EnemyDamage { get; private set; }
    public double FriendlyFireDamage { get; private set; }
    public bool AccuracyObserved { get; private set; }
    public bool AccuracyEstimated { get; private set; }
    public bool EnemyDamageObserved { get; private set; }
    public bool FriendlyFireObserved { get; private set; }
    public bool EnemyDamageEstimated { get; private set; }
    // True when the squad total this row's share is divided by was estimated rather than measured, so
    // the percentage goes out with the board's estimate marker instead of reading like a measurement.
    public bool SquadDamageEstimated { get; set; }
    public bool FriendlyFireEstimated { get; private set; }

    public void AddAccuracy(long fired, long hits, long weak, bool estimated = false)
    {
        if (fired < 0 || hits < 0 || weak < 0 || weak > hits) throw new ArgumentOutOfRangeException(nameof(fired));
        if (!estimated && hits > fired) throw new ArgumentOutOfRangeException(nameof(hits));
        ShotsFired = Add(ShotsFired, fired); Hits = Add(Hits, hits); WeakspotHits = Add(WeakspotHits, weak);
        AccuracyObserved = true; AccuracyEstimated |= estimated;
    }

    public void AdjustAccuracy(long fired, long hits, long weak, bool estimated = false)
    {
        ShotsFired = Adjust(ShotsFired, fired);
        Hits = Adjust(Hits, hits);
        WeakspotHits = Math.Min(Adjust(WeakspotHits, weak), Hits);
        AccuracyObserved = true; AccuracyEstimated |= estimated;
    }

    // Overwrite rather than accumulate. A snapshot from the host is the whole truth about that player
    // at that moment, so applying it replaces what this machine observed instead of adding to it:
    // adding would double count every hit both machines saw.
    public void RestoreAccuracy(long fired, long hits, long weak, bool observed, bool estimated)
    {
        ShotsFired = fired < 0 ? 0 : fired;
        Hits = hits < 0 ? 0 : hits > ShotsFired ? ShotsFired : hits;
        WeakspotHits = weak < 0 ? 0 : weak > Hits ? Hits : weak;
        AccuracyObserved = observed; AccuracyEstimated = estimated;
    }

    public void RestoreEnemyDamage(double amount, bool observed, bool estimated)
    {
        EnemyDamage = double.IsFinite(amount) && amount > 0 ? amount : 0;
        EnemyDamageObserved = observed; EnemyDamageEstimated = estimated;
    }

    public void RestoreFriendlyFireDamage(double amount, bool observed, bool estimated)
    {
        FriendlyFireDamage = double.IsFinite(amount) && amount > 0 ? amount : 0;
        FriendlyFireObserved = observed; FriendlyFireEstimated = estimated;
    }

    public void AddEnemyDamage(double amount, bool estimated = false)
    {
        if (!double.IsFinite(amount) || amount < 0) return;
        EnemyDamageObserved = true; EnemyDamageEstimated |= estimated;
        EnemyDamage = AddDamage(EnemyDamage, amount);
    }

    public void AddFriendlyFireDamage(double amount, bool estimated = false)
    {
        if (!double.IsFinite(amount) || amount < 0) return;
        FriendlyFireObserved = true; FriendlyFireEstimated |= estimated;
        FriendlyFireDamage = AddDamage(FriendlyFireDamage, amount);
    }

    private static long Add(long value, long increment) => value > long.MaxValue - increment ? long.MaxValue : value + increment;
    private static long Adjust(long value, long delta)
    {
        if (delta >= 0) return Add(value, delta);
        if (delta == long.MinValue) return 0;
        return Math.Max(0, value + delta);
    }
    private static double AddDamage(double value, double amount) => Math.Min(double.MaxValue, value + amount);

    // A full copy, used to build a display row without ever touching the measurement it was taken from.
    // The row the mod captures into stays the local measurement; a display row is a throwaway projection
    // of it with the remote figures merged in.
    public void CopyFrom(PlayerCombatStats other)
    {
        if (other == null) return;
        RestoreAccuracy(other.ShotsFired, other.Hits, other.WeakspotHits, other.AccuracyObserved, other.AccuracyEstimated);
        RestoreEnemyDamage(other.EnemyDamage, other.EnemyDamageObserved, other.EnemyDamageEstimated);
        RestoreFriendlyFireDamage(other.FriendlyFireDamage, other.FriendlyFireObserved, other.FriendlyFireEstimated);
        SquadDamageEstimated = other.SquadDamageEstimated;
    }
}

public sealed class WeaponCombatStats
{
    public int Slot { get; }
    public uint ArchetypeId { get; }
    public uint GearCategoryId { get; internal set; }
    // True when the game exposes no archetype id of its own for this weapon, so the entry can only be
    // matched by the gear category its damage records carry: the situation an EWC-managed or
    // otherwise modded weapon is in. Derived, so it can never disagree with the two figures.
    public bool EwcManaged => ArchetypeId == 0 && GearCategoryId != 0;
    public string Name { get; private set; }
    // True once a source that really named the weapon set it. A name that only ever came from reading the
    // backpack stays replaceable, so a placeholder (`GearStandard`) is still upgraded by the first real one.
    public bool NameAuthoritative { get; private set; }
    public PlayerCombatStats Counters { get; } = new PlayerCombatStats();
    internal WeaponCombatStats(int slot, uint id, uint gearCategoryId, string name)
    { Slot = slot; ArchetypeId = id; GearCategoryId = gearCategoryId; Name = name; }

    // The name belongs to the weapon the row was opened for. It is replaced only by something that really
    // named the weapon - the weapon that fired, or the machine that owns the row - and never by a passive
    // probe of the backpack, which is what made one archive say `HEL左轮手枪` and the board drawn a moment
    // later say `手枪` for the same row and the same figures.
    internal void SetName(string name, bool authoritative)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (!authoritative && NameAuthoritative) return;
        Name = name;
        NameAuthoritative |= authoritative;
    }

    // A throwaway copy of this weapon row, so a display projection can be edited without touching the
    // measured row it came from. Slot and archetype id belong to the target the caller created.
    public void CopyInto(WeaponCombatStats target)
    {
        if (target == null) return;
        target.SetName(Name, NameAuthoritative);
        target.Counters.CopyFrom(Counters);
    }
}

public sealed class CombatPlayer
{
    private readonly Dictionary<(int, uint), WeaponCombatStats> _weapons = new Dictionary<(int, uint), WeaponCombatStats>();
    public ulong Lookup { get; }
    public int DisplaySlot { get; set; }
    public string Name { get; set; }
    public bool StatisticsAvailable { get; set; }
    public PlayerCombatStats Total { get; } = new PlayerCombatStats();
    public ImpactCounters Impact { get; } = new ImpactCounters();
    public bool SupportRole { get; set; }
    public IEnumerable<WeaponCombatStats> Weapons => _weapons.Values;
    public CombatPlayer(ulong lookup) { Lookup = lookup; }
    public WeaponCombatStats Weapon(int slot, uint id, string name, uint gearCategoryId = 0, bool authoritative = false)
    {
        if (!_weapons.TryGetValue((slot, id), out WeaponCombatStats result))
        {
            result = new WeaponCombatStats(slot, id, gearCategoryId, name);
            _weapons.Add((slot, id), result);
            result.SetName(name, authoritative);
        }
        else
        {
            result.SetName(name, authoritative);
            if (gearCategoryId != 0) result.GearCategoryId = gearCategoryId;
        }
        return result;
    }
    public WeaponCombatStats FindWeapon(int slot, uint id)
    {
        _weapons.TryGetValue((slot, id), out WeaponCombatStats result); return result;
    }
    public WeaponCombatStats FindWeaponByGearCategory(uint gearCategoryId)
    {
        if (gearCategoryId == 0) return null;
        WeaponCombatStats match = null;
        foreach (WeaponCombatStats weapon in _weapons.Values)
        {
            if (weapon.GearCategoryId != gearCategoryId) continue;
            if (match != null && !ReferenceEquals(match, weapon)) return null;
            match = weapon;
        }
        return match;
    }
    // A display projection of this row: the local measurement copied out, so the remote figures can be
    // merged into it without a single write landing on the row the mod keeps capturing into.
    public CombatPlayer Clone()
    {
        var copy = new CombatPlayer(Lookup)
        {
            DisplaySlot = DisplaySlot, Name = Name,
            StatisticsAvailable = StatisticsAvailable, SupportRole = SupportRole
        };
        copy.Total.CopyFrom(Total);
        copy.Impact.CopyFrom(Impact);
        foreach (WeaponCombatStats weapon in _weapons.Values)
        {
            WeaponCombatStats target = copy.Weapon(weapon.Slot, weapon.ArchetypeId, weapon.Name, weapon.GearCategoryId, weapon.NameAuthoritative);
            weapon.CopyInto(target);
        }
        return copy;
    }


    // Which weapon a damage record belongs to. Every bullet and melee record carries the gear
    // category of the weapon that produced it, so a hit is attributed to the weapon that actually
    // fired rather than to whatever the player happens to be holding when the record lands: that is
    // what keeps penetration, delayed projectile hits and modded weapons on their own rows. The
    // record's category wins only when it names exactly one of this player's weapons.
    public static WeaponCombatStats MatchDamage(CombatPlayer player, WeaponCombatStats held, uint gearCategoryId)
    {
        if (player == null || gearCategoryId == 0) return held;
        return player.FindWeaponByGearCategory(gearCategoryId) ?? held;
    }
    public void RecordAccuracy(WeaponCombatStats weapon, long fired, long hits, long weak, bool estimated = false)
    {
        Total.AddAccuracy(fired, hits, weak, estimated); weapon?.Counters.AddAccuracy(fired, hits, weak, estimated);
    }
    public void AdjustAccuracy(WeaponCombatStats weapon, long fired, long hits, long weak, bool estimated = false)
    {
        Total.AdjustAccuracy(fired, hits, weak, estimated); weapon?.Counters.AdjustAccuracy(fired, hits, weak, estimated);
    }
    public void RecordDamage(WeaponCombatStats weapon, double amount, bool friendly, bool estimated = false)
    {
        if (friendly) { Total.AddFriendlyFireDamage(amount, estimated); weapon?.Counters.AddFriendlyFireDamage(amount, estimated); }
        else { Total.AddEnemyDamage(amount, estimated); weapon?.Counters.AddEnemyDamage(amount, estimated); }
    }
}

public sealed class CombatStatsStore
{
    private readonly Dictionary<ulong, CombatPlayer> _players = new Dictionary<ulong, CombatPlayer>();
    public IEnumerable<CombatPlayer> Players => _players.Values;
    public CombatPlayer GetOrCreate(ulong lookup)
    {
        if (!_players.TryGetValue(lookup, out CombatPlayer player))
        {
            player = new CombatPlayer(lookup);
            _players.Add(lookup, player);
        }
        return player;
    }
    public bool TryGet(ulong lookup, out CombatPlayer player) => _players.TryGetValue(lookup, out player);
    // A copy of the rows, for a walk that has to index them (the exchange's projection and the rows it sends).
    // The store itself is never handed out, so nothing outside it can add to a measurement.
    public List<CombatPlayer> Snapshot() => new List<CombatPlayer>(_players.Values);
    // Drop rows for identities that no longer occupy a live game slot.
    public int PruneExcept(ISet<ulong> retainedLookups)
    {
        if (retainedLookups == null) return 0;
        var removed = new List<ulong>();
        foreach (ulong lookup in _players.Keys)
            if (!retainedLookups.Contains(lookup)) removed.Add(lookup);
        for (int index = 0; index < removed.Count; index++) _players.Remove(removed[index]);
        return removed.Count;
    }
    public void Reset() => _players.Clear();
}

public sealed class ExpeditionLifecycle
{
    private bool _armed;
    private bool _started;
    public bool ShouldReset(string state)
    {
        if (state == "Generating")
        {
            if (_armed && !_started) return false;
            _armed = true; _started = false; return true;
        }
        if (state == "InLevel")
        {
            if (_started) return false;
            _started = true;
            if (_armed) return false;
            _armed = true; return true;
        }
        if (state == "Lobby" || state == "NoLobby") { _armed = false; _started = false; }
        return false;
    }
}

public static class CombatStatsFormatter
{
    public const string Separator = " ";
    public static readonly PanelColumn[] Columns =
    {
        PanelColumn.PlayerName, PanelColumn.DamageAndShare, PanelColumn.FriendlyFire,
        PanelColumn.Accuracy, PanelColumn.WeakspotAccuracy, PanelColumn.Projectiles
    };
    public static PanelTextRole TextRoleFor(PanelColumn column) => column == PanelColumn.PlayerName ? PanelTextRole.PlayerName : PanelTextRole.Neutral;
    public static bool IsRightAligned(PanelColumn column) => true;
    // A Steam name may carry TextMeshPro markup: "<#fa0>\"<#0ff>A1pha<#f00>\"" is a real one, read from a live
    // session's log. The table draws with rich text switched off, so an unhandled tag prints itself and the six
    // element cut of that name was the literal "<#fa0>". Strip recognised markup first, then cut the visible text.
    private static readonly string[] RichTextTags =
    {
        "color", "b", "i", "u", "s", "smallcaps", "uppercase", "lowercase", "size", "sprite", "align", "indent",
        "line", "margin", "mark", "mspace", "pos", "space", "voffset", "cspace", "font", "gradient", "link",
        "material", "page", "br", "nobr", "style"
    };
    // Only markup the game itself would interpret is removed; a stray '<' that is not a tag stays a literal.
    private static bool IsRichTextTag(string content)
    {
        string body = content[0] == '/' ? content.Substring(1) : content;
        if (body.Length > 0 && body[0] == '#') return true;
        int stop = 0;
        while (stop < body.Length && (char.IsLetterOrDigit(body[stop]) || body[stop] == '-')) stop++;
        if (stop == 0) return false;
        string word = body.Substring(0, stop).ToLowerInvariant();
        foreach (string tag in RichTextTags) if (word == tag) return true;
        return false;
    }
    private static bool LooksLikeRichTextTag(string text, int start, out int end)
    {
        end = start;
        int close = text.IndexOf('>', start + 1);
        if (close < 0 || close - start > 64) return false;
        string content = text.Substring(start + 1, close - start - 1);
        if (content.Length == 0 || content.IndexOf('<') >= 0) return false;
        if (!IsRichTextTag(content)) return false;
        end = close;
        return true;
    }
    // The name as the player typed it, with only the markup the game itself would interpret removed.
    // No length cap: a long name widens the measured column rather than being cut into a fragment.
    public static string FullPlayerName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var plain = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '<' && LooksLikeRichTextTag(name, i, out int close)) { i = close; continue; }
            if (!char.IsControl(name[i])) plain.Append(name[i]);
        }
        return plain.ToString();
    }
    // Kept for the places that still want a fixed budget, and used by the smoke suite to pin the cut.
    public static string TruncatePlayerName(string name, int maximumTextElements = 6)
    {
        if (maximumTextElements <= 0) return string.Empty;
        string plain = FullPlayerName(name);
        if (plain.Length == 0) return string.Empty;
        var info = new StringInfo(plain);
        return info.SubstringByTextElements(0, Math.Min(info.LengthInTextElements, maximumTextElements));
    }
    public static string FormatDamage(double amount) => (double.IsFinite(amount) && amount >= 0 ? amount : 0).ToString("#,0.0", CultureInfo.InvariantCulture);
    public static string DamageText(PlayerCombatStats stats, bool friendlyFire = false)
    {
        if (stats == null) return "—";
        bool observed = friendlyFire ? stats.FriendlyFireObserved : stats.EnemyDamageObserved;
        if (!observed) return "—";
        bool estimated = friendlyFire ? stats.FriendlyFireEstimated : stats.EnemyDamageEstimated;
        double value = friendlyFire ? stats.FriendlyFireDamage : stats.EnemyDamage;
        return (estimated ? "~" : string.Empty) + FormatDamage(value);
    }
    public static string FormatPercent(long numerator, long denominator)
    {
        if (denominator <= 0 || numerator < 0) return "—";
        double ratio = (double)numerator * 100 / denominator;
        return double.IsFinite(ratio) ? ratio.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";
    }
    public static string FormatShare(PlayerCombatStats stats, double? squadDamage)
    {
        if (stats == null || !stats.EnemyDamageObserved) return "—";
        if (!squadDamage.HasValue || !double.IsFinite(squadDamage.Value) || squadDamage.Value <= 0) return "—";
        double share = stats.EnemyDamage / squadDamage.Value * 100;
        if (!double.IsFinite(share) || share < 0) return "—";
        string mark = stats.EnemyDamageEstimated || stats.SquadDamageEstimated ? "~" : string.Empty;
        return mark + share.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }
    private static string EstimateMark(PlayerCombatStats stats) => stats != null && stats.AccuracyEstimated ? "~" : string.Empty;
    public static string AccuracyText(PlayerCombatStats stats)
        => stats != null && stats.AccuracyObserved ? EstimateMark(stats) + FormatPercent(stats.Hits, stats.ShotsFired) : "—";
    public static string WeakspotText(PlayerCombatStats stats)
        => stats != null && stats.AccuracyObserved ? EstimateMark(stats) + FormatPercent(stats.WeakspotHits, stats.Hits) : "—";
    public static string[] Fields(PlayerCombatStats stats, PanelColumn column, double? squadDamage = null)
    {
        switch (column)
        {
            case PanelColumn.DamageAndShare: return new[] { DamageText(stats), FormatShare(stats, squadDamage) };
            case PanelColumn.FriendlyFire: return new[] { FriendlyFireText(stats) };
            case PanelColumn.Accuracy: return new[] { AccuracyText(stats) };
            case PanelColumn.WeakspotAccuracy: return new[] { WeakspotText(stats) };
            case PanelColumn.Projectiles: return ProjectileFields(stats);
            default: return new[] { string.Empty };
        }
    }
    public static string FriendlyFireText(PlayerCombatStats stats) => DamageText(stats, true);
    public static string[] ProjectileFields(PlayerCombatStats stats)
    {
        if (stats == null || !stats.AccuracyObserved) return new[] { "—" };
        return new[]
        {
            EstimateMark(stats) + stats.ShotsFired.ToString(CultureInfo.InvariantCulture),
            stats.Hits.ToString(CultureInfo.InvariantCulture),
            stats.WeakspotHits.ToString(CultureInfo.InvariantCulture)
        };
    }
    public static string ProjectilesText(PlayerCombatStats stats) => string.Join(Separator, ProjectileFields(stats));
    public static string Value(PlayerCombatStats stats, PanelColumn column, double? squadDamage = null)
        => column == PanelColumn.PlayerName ? string.Empty : string.Join(Separator, Fields(stats, column, squadDamage));
    public static string Summary(PlayerCombatStats stats, double? squadDamage = null, bool includeShare = false)
        => CombatStrings.Damage + " " + DamageText(stats)
            + (includeShare ? Separator + CombatStrings.Share + " " + FormatShare(stats, squadDamage) : string.Empty)
            + "\n" + CombatStrings.FriendlyFire + " " + DamageText(stats, true)
            + "\n" + CombatStrings.Accuracy + " " + AccuracyText(stats) + "  " + CombatStrings.Weakspot + " " + WeakspotText(stats)
            + "\n" + CombatStrings.Pellets + " " + ProjectilesText(stats);
}

