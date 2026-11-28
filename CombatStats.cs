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

    // 1.19.65: the mirror image of `AddEnemyDamage`, for the one figure this machine may have to take back - an
    // estimate it made for its own blow that a later, attributable health reading proved the enemy never lost
    // (`CombatTracker`'s per-instance balance). It only ever moves the number down and never below zero, and it
    // is never called for a measurement: a credit this machine read across the call is confirmed.
    public void WithdrawEnemyDamage(double amount)
    {
        if (!double.IsFinite(amount) || amount <= 0 || !(EnemyDamage > 0)) return;
        EnemyDamage = Math.Max(0, EnemyDamage - amount);
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

// Which row of one inventory slot a display should read.
//
// A player's rows are keyed by (slot, archetype id), so one slot can hold several of them: the game opens a
// placeholder row for the equipped slot before anything names the weapon (`ArchetypeId = 0`, name
// `GearClass`), and the real weapon - or the tool that really fired, a sentry among them - is a second row in
// the same slot. Reading "the first row of this slot" therefore depended on dictionary order and could hand
// the board the placeholder: a row with no real name and no figures, which is how one column showed data for
// only part of the squad. The most specific real row wins instead, in this order:
//
//   * a row the game gave an archetype id of its own (`ArchetypeId != 0`) - a real weapon, not a placeholder;
//   * a row whose name a real source set (`NameAuthoritative`), so `GearClass` loses to `Sentry`;
//   * a row that has actually counted something.
//
// Ties keep the best row seen first, so a projection holding two equally good rows is answered with the same
// one on every frame rather than with whichever the dictionary happened to yield.
public static class WeaponRows
{
    public static WeaponCombatStats For(IEnumerable<WeaponCombatStats> rows, int slot)
    {
        WeaponCombatStats best = null;
        int bestRank = int.MinValue;
        if (rows == null) return null;
        foreach (WeaponCombatStats row in rows)
        {
            if (row == null || row.Slot != slot) continue;
            int rank = Rank(row);
            if (best != null && rank <= bestRank) continue;
            best = row; bestRank = rank;
        }
        return best;
    }

    // What a row of one slot is worth: whether it is a real weapon at all, whether something really named it,
    // and whether it counted anything. A row that is both unnamed and without an archetype id is the
    // placeholder its slot was opened with, and it never wins over a row that is either.
    public static int Rank(WeaponCombatStats row)
    {
        if (row == null) return int.MinValue;
        int rank = 0;
        if (row.ArchetypeId != 0) rank += 4;
        if (row.NameAuthoritative) rank += 2;
        if (Counted(row)) rank += 1;
        return rank;
    }

    private static bool Counted(WeaponCombatStats row)
        => row.Counters != null
           && (row.Counters.ShotsFired > 0 || row.Counters.Hits > 0 || row.Counters.EnemyDamage > 0
               || row.Counters.FriendlyFireDamage > 0);
    // 1.19.64: the rows of one slot that the one-row-per-slot projection does not draw, when the slot really held
    // more than one weapon. The recorded Survival r3 log is why this exists: slot 2 fired `收束器霰弹枪`,
    // `HEL步枪` and `战斗霰弹枪` in one expedition, the board drew the one `WeaponRows.For` chose, and the other
    // two rows - which `CombatPlayer.Total` does count - had nowhere to be seen.
    //
    // What is *not* another weapon, and never becomes one here:
    //   * the row the slot was opened with (`ArchetypeId = 0`, name `GearClass`): it has no identity of its own, so
    //     it is neither counted nor named even when it exists beside a real row;
    //   * a repeated copy of a weapon somebody already reported: rows are keyed by `(slot, archetype id)`, so a
    //     second snapshot of the same weapon merges into the row its identity already owns - nothing here adds two
    //     rows together, and the figure a caller reads stays one measurement;
    //   * a row that never counted anything: opening a row is not using the weapon, and naming it would invent one;
    //   * a row whose name is the drawn row's name (`收束器霰弹枪` under two archetype ids is one weapon seen twice).
    //
    // Only measured rows are returned, so a caller can print them as the figures they are. Order: damage first,
    // then name, so the block a reader sees is the same one on every frame.
    public static List<WeaponCombatStats> Others(IEnumerable<WeaponCombatStats> rows, int slot, WeaponCombatStats drawn)
    {
        var others = new List<WeaponCombatStats>();
        if (rows == null) return others;
        string drawnName = NameOf(drawn);
        foreach (WeaponCombatStats row in rows)
        {
            if (row == null || ReferenceEquals(row, drawn) || row.Slot != slot) continue;
            if (row.ArchetypeId == 0 || !Counted(row)) continue;
            string name = NameOf(row);
            if (name.Length == 0) continue;
            if (drawn != null && string.Equals(name, drawnName, StringComparison.OrdinalIgnoreCase)) continue;
            bool seen = false;
            for (int index = 0; index < others.Count; index++)
            {
                if (!string.Equals(NameOf(others[index]), name, StringComparison.OrdinalIgnoreCase)) continue;
                seen = true;
                break;
            }
            if (!seen) others.Add(row);
        }
        others.Sort((left, right) =>
        {
            int byDamage = right.Counters.EnemyDamage.CompareTo(left.Counters.EnemyDamage);
            return byDamage != 0 ? byDamage : string.CompareOrdinal(NameOf(left), NameOf(right));
        });
        return others;
    }

    private static string NameOf(WeaponCombatStats row)
        => row == null || string.IsNullOrEmpty(row.Name) ? string.Empty : row.Name.Trim();
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
    // A projection can combine a missing total with real per-weapon rows from another source.
    // Their sum is a known lower bound, not a new damage event and not an exact squad total.
    public void ReconcileDamageLowerBound()
    {
        double enemy = 0, friendly = 0;
        foreach (WeaponCombatStats weapon in _weapons.Values)
        {
            if (weapon.Counters.EnemyDamageObserved) enemy += weapon.Counters.EnemyDamage;
            if (weapon.Counters.FriendlyFireObserved) friendly += weapon.Counters.FriendlyFireDamage;
        }
        if (double.IsFinite(enemy) && enemy > Total.EnemyDamage + Math.Max(0.01, enemy * 0.00001))
            Total.RestoreEnemyDamage(enemy, true, true);
        if (double.IsFinite(friendly) && friendly > Total.FriendlyFireDamage + Math.Max(0.01, friendly * 0.00001))
            Total.RestoreFriendlyFireDamage(friendly, true, true);
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
    // A Steam name may carry TextMeshPro markup. With rich text disabled, an unhandled tag prints itself.
    // Strip recognised markup before cutting to six visible text elements.
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
    // The name as it may be *displayed*, which is the name above plus the one thing the wire can do to it: a name
    // travels in a fixed number of bytes, so a cut can land inside a markup tag and leave the opening '<' - or
    // `<co`, or `<color=#FF40` - of a tag that was never closed behind. The table draws with rich text switched off,
    // so that residue would be printed literally: the archived run4 settlement board shows exactly that, `Long Gun<`
    // where the game's own name was `<color=#FF4005>Long Gun</color>`.
    //
    // Two different kinds of evidence decide a fragment, and a bare '<' or a `</` is only removed on one of them:
    //
    //   * the fragment after '<' is itself a real prefix of a tag this game interprets (`<co`, `<color=#FF40`): no
    //     ordinary name contains that, so the cut is proved by what is left;
    //   * the name is at the wire's own capacity (`SyncSnapshot.NameCapacity` bytes of UTF-8), which is what a cut
    //     *is*, and/or the name still carries a complete opening tag, so a closing tag demonstrably was open. Only
    //     then is a bare '<' or a `</`-fragment removed - the residue of a closing tag whose every other character
    //     the field dropped. A short name that ends in '<' or in a literal `</` (`1 <`, `A</`, `Gun</c`) is left
    //     exactly as it is, and nothing here invents the characters the wire already dropped: what is left is what
    //     is measured and drawn (`N1`).
    // The wire's own name field, in UTF-8 bytes: `SyncSnapshot.NameWords` (6) * 4. It is repeated here because this
    // file is compiled into the supply-attribution test project as well, which does not link `SyncProtocol.cs`; the
    // smoke suite asserts the two are the same number, so the two cannot drift apart silently.
    public const int NameCapacityBytes = 24;
    public static string VisibleName(string name)
    {
        string plain = FullPlayerName(name);
        if (string.IsNullOrEmpty(plain)) return plain;
        bool atCapacity = false;
        try { atCapacity = Encoding.UTF8.GetByteCount(name) >= NameCapacityBytes; } catch { }
        bool hadOpenTag = HasOpenTag(name);
        for (int i = 0; i < plain.Length; i++)
        {
            if (plain[i] != '<' || !IsDanglingTagFragment(plain, i, atCapacity, hadOpenTag)) continue;
            return plain.Substring(0, i).TrimEnd();
        }
        return plain;
    }
    // Whether the name still carries a complete opening tag this game would interpret (`<color=#FF4005>`). That is the
    // proof that a closing tag was open, so a `</`-residue left behind can be recognised as markup (`N1`).
    private static bool HasOpenTag(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        for (int i = 0; i + 2 < name.Length; i++)
        {
            if (name[i] != '<' || name[i + 1] == '/') continue;
            if (LooksLikeRichTextTag(name, i, out _) && IsRichTextTag(name.Substring(i + 1, name.IndexOf('>', i + 1) - i - 1))) return true;
        }
        return false;
    }
    // `atCapacity` is the proof that the field really cut this name, and `hadOpenTag` that a closing tag was open: a
    // bare '<' or a `</`-fragment is only a fragment with one of them. A literal `A</` or `Gun</c` in a short name is
    // ordinary text and stays exactly as it is (`N1`).
    private static bool IsDanglingTagFragment(string text, int start, bool atCapacity, bool hadOpenTag)
    {
        if (text.IndexOf('>', start + 1) >= 0) return false;
        string body = text.Substring(start + 1);
        if (body.Length > 24 || body.IndexOf('<') >= 0 || body.IndexOf(' ') >= 0) return false;
        bool closing = body.Length > 0 && body[0] == '/';
        if (closing) body = body.Substring(1);
        // A closing fragment without either proof is not treated as markup: the '/' alone is not evidence.
        if (closing && !atCapacity && !hadOpenTag) return false;
        // A closing tag can be cut after a single letter: `</c` is what a 24-byte cut of `<color=#FF4005>大锤</color>`
        // leaves behind. An *opening* fragment still needs two letters, so a literal `A<c` stays exactly as it is.
        if (body.Length == 0) return atCapacity || closing;
        if (body[0] == '#') return IsColourPrefix(body.Substring(1));
        int equals = body.IndexOf('=');
        string word = equals >= 0 ? body.Substring(0, equals) : body;
        if (word.Length < (closing ? 1 : 2)) return false;
        foreach (string tag in RichTextTags)
        {
            if (tag.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    private static bool IsColourPrefix(string body)
    {
        if (body.Length == 0) return true;
        foreach (char glyph in body)
        {
            if ("0123456789abcdefABCDEF".IndexOf(glyph) < 0) return false;
        }
        return true;
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

