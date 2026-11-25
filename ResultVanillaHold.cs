using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// ResultVanillaHold.cs - what this mod took from one vanilla result-page block, and nothing else
// ---------------------------------------------------------------------------------------------------
//
// The settlement board puts the game's own presentational blocks away so it can stand in front of them. Two
// independent things can be taken from such a block, and 1.19.59 wrote both of them into ONE record:
//
//   * the MOVE - the block's `localPosition` moved off the page plus `SetActive(false)`, taken for a block the
//     classifier proved carries no control of the player's (`VanillaHidePlan.WholeBlock`), or for a branch of one
//     that carries none (`SafeBranches`);
//   * the DRAWING - the block's own `CanvasGroup` at zero alpha and its renderers switched off, taken for
//     everything else (a block whose body holds a `Button`, the early board's frame).
//
// Sharing the record made two defects inevitable, and both were found by the independent review of the round:
//
//   H1  `SuppressVisual` created the record with `Target = null` and `Position = default`. A block whose drawing had
//       been switched off first and which was moved later therefore kept that empty move half, and the move itself
//       wrote `localPosition` from a position nobody had ever read. F8 / a page change / leaving the screens then
//       "put back" (0, 0, 0) instead of the place the page drew the block at - permanently, because the record is
//       written once. The move half is now taken at the first *move*, whatever the drawing half did before it.
//   M3  the same record hardcoded `Active = true` for a drawing-only hold, so the restore switched ON a block the
//       page had left switched off; and the drawing half used to ADD a `CanvasGroup` to the game's own object when
//       the block had none, which nothing here may destroy (a destroy that lands a frame after the next frame's
//       re-add is a race, and until it lands the game's object graph has a component it never asked for). No
//       component is added any more, and a half is only put back when it was really taken.
//
// The ledger below is therefore Unity-free: it holds the state itself (positions, flags, object handles) and
// answers the two questions the Unity side asks - "is this half still mine" and "what exactly do I put back" -
// so the state transitions (capture once, restore only what was taken) are asserted by real running code in
// `tests/GTFOCombatPanel.Smoke`, not by reading this assembly's source text. The handles are `object` on purpose:
// this file references no Unity type, which is what lets the smoke project compile and drive it.
//
// What the ledger deliberately cannot carry: a raycast flag. `CanvasGroup.interactable` and
// `CanvasGroup.blocksRaycasts` are never recorded and never written (H2) - a hidden block must keep answering
// exactly like the one the page drew, so the drawing half owns alpha and renderer enablement alone.

/// <summary>
/// One vanilla block this mod has touched: the move half, the drawing half, and nothing else.
/// </summary>
public sealed class ResultVanillaHold
{
    private static readonly object[] NoObjects = new object[0];
    private static readonly bool[] NoFlags = new bool[0];
    private static readonly float[] NoAlphas = new float[0];

    internal ResultVanillaHold(IntPtr key)
    {
        Key = key;
        Renderers = NoObjects; RendererEnabled = NoFlags;
        Canvas = NoObjects; CanvasAlpha = NoAlphas;
    }

    /// <summary>The native pointer of the block's own node - the identity of one record.</summary>
    public IntPtr Key { get; }

    // ---- the move half ----------------------------------------------------------------------------
    /// <summary>True once the move half has been taken; the recorded position and active state are the page's own.</summary>
    public bool MoveHeld { get; internal set; }
    /// <summary>The `Transform` that was moved, or null.</summary>
    public object Target { get; internal set; }
    /// <summary>The `GameObject` that was switched off, or null.</summary>
    public object Host { get; internal set; }
    /// <summary>The block's own `localPosition` before the first move, one float per axis.</summary>
    public float X { get; internal set; }
    public float Y { get; internal set; }
    public float Z { get; internal set; }
    /// <summary>The block's own `activeSelf` before the first move.</summary>
    public bool ActiveSelf { get; internal set; }

    // ---- the drawing half -------------------------------------------------------------------------
    /// <summary>True once the drawing half has been taken.</summary>
    public bool VisualHeld { get; internal set; }
    /// <summary>The `CanvasGroup` the block ALREADY had, or null. This mod never adds one.</summary>
    public object Group { get; internal set; }
    /// <summary>That group's own alpha before it was switched off.</summary>
    public float GroupAlpha { get; internal set; } = 1f;
    /// <summary>The `Renderer`s this mod really switched off, and the state each really had.</summary>
    public object[] Renderers { get; internal set; }
    public bool[] RendererEnabled { get; internal set; }
    /// <summary>The `CanvasRenderer`s whose alpha this mod really changed, and the alpha each really had.</summary>
    public object[] Canvas { get; internal set; }
    public float[] CanvasAlpha { get; internal set; }

    /// <summary>Whether anything at all is held for this block.</summary>
    public bool Any => MoveHeld || VisualHeld;

    /// <summary>
    /// The move half's position, answered only when a move was really taken. The drawing half can never answer
    /// this - that is H1: a block whose drawing was switched off has no position recorded, and the move takes it
    /// from the page at the moment it really moves the block.
    /// </summary>
    public bool TryRestorePosition(out float x, out float y, out float z)
    {
        x = X; y = Y; z = Z;
        return MoveHeld;
    }

    /// <summary>
    /// The move half's active state, answered only when a move was really taken. A drawing-only hold answers
    /// false, so nothing here can switch on a block the page had left switched off (M3).
    /// </summary>
    public bool TryRestoreActive(out bool active)
    {
        active = ActiveSelf;
        return MoveHeld;
    }

    /// <summary>The block's own group alpha, answered only when a group was there and was really switched off.</summary>
    public bool TryRestoreGroup(out float alpha)
    {
        alpha = GroupAlpha;
        return VisualHeld && Group != null;
    }
}

/// <summary>
/// The ledger of held blocks: keyed by the block's own pointer, holding each of the two halves independently and
/// idempotently.
/// </summary>
public sealed class ResultVanillaHoldLedger
{
    private readonly Dictionary<IntPtr, ResultVanillaHold> _holds = new Dictionary<IntPtr, ResultVanillaHold>();

    /// <summary>How many blocks are held (either half).</summary>
    public int Count => _holds.Count;

    /// <summary>Every record, for the one restore.</summary>
    public IEnumerable<ResultVanillaHold> Holds => _holds.Values;

    public bool TryGetValue(IntPtr key, out ResultVanillaHold hold) => _holds.TryGetValue(key, out hold);

    /// <summary>
    /// Take the move half. The FIRST caller wins: every later call answers with the position and active state the
    /// page had before this mod moved anything, even though the block has been moved (and switched off) since.
    /// That is what makes the write idempotent, and what the caller must write the block's own position from - never
    /// from the state it just read back out of the moved block.
    /// </summary>
    public ResultVanillaHold HoldMove(IntPtr key, object target, object host, float x, float y, float z, bool activeSelf)
    {
        ResultVanillaHold hold = For(key);
        if (hold.MoveHeld) return hold;
        hold.Target = target; hold.Host = host;
        hold.X = x; hold.Y = y; hold.Z = z;
        hold.ActiveSelf = activeSelf;
        hold.MoveHeld = true;
        return hold;
    }

    /// <summary>
    /// Take the drawing half. Idempotent the same way: the arrays handed in the first time are the ones that will be
    /// put back, and a later frame cannot capture a state this mod itself already changed (a second zero alpha would
    /// otherwise become the "recorded" alpha and the block would never come back).
    /// </summary>
    public ResultVanillaHold HoldVisual(IntPtr key, object group, float groupAlpha,
        object[] renderers, bool[] rendererEnabled, object[] canvas, float[] canvasAlpha)
    {
        ResultVanillaHold hold = For(key);
        if (hold.VisualHeld) return hold;
        hold.Group = group; hold.GroupAlpha = groupAlpha;
        hold.Renderers = renderers ?? new object[0];
        hold.RendererEnabled = rendererEnabled ?? new bool[0];
        hold.Canvas = canvas ?? new object[0];
        hold.CanvasAlpha = canvasAlpha ?? new float[0];
        hold.VisualHeld = true;
        return hold;
    }

    /// <summary>Forget one block, after it has been put back.</summary>
    public bool Drop(IntPtr key) => _holds.Remove(key);

    /// <summary>Forget every block. Called by the restore, after every record has been used.</summary>
    public void Clear() => _holds.Clear();

    private ResultVanillaHold For(IntPtr key)
    {
        if (_holds.TryGetValue(key, out ResultVanillaHold hold)) return hold;
        hold = new ResultVanillaHold(key);
        _holds[key] = hold;
        return hold;
    }
}
