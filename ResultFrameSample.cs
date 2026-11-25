using System;

namespace HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// ResultFrameSample.cs - one sample of the frame's own state, shared by every entry that asks in it
// ---------------------------------------------------------------------------------------------------
//
// The settlement board answers three gestures from one point: the hover that lifts a row, the press that
// dispatches to the page's own controls, and the wheel that moves the table. That point is the page's own cursor
// (`CM_PageBase.m_cursor`), projected once. 1.19.59 sampled it inside the repaint and inside the press dispatch,
// and the wheel - which is settled from the frame driver (`PanelBehaviour.Update`) and again from the page's own
// press dispatch (`CM_PageBase.UpdateButtonPress`, i.e. BEFORE `DispatchPressFrame`) - read whatever the last
// repaint had left in `_pointer`. The independent review's L5 is exactly that: on a frame where the pointer entered
// or left the board between the repaint and the dispatch, the wheel decided from a point the press no longer
// agreed with, and on a page whose cursor had not moved since the previous repaint it decided from a stale one.
//
// The answer is not "sample more often" - the same point asked twice in one frame is still one answer, and asking
// twice is how two entries came to disagree. It is: exactly one sample per frame, taken by the first entry that
// needs it, and every other entry in that frame reads that same sample. `ResultPointerFrameCache` is that gate; it
// is Unity-free so the rule (one sample, one frame, a new frame starts over, a forget starts over) is asserted by
// real running code in `tests/GTFOCombatPanel.Smoke`.
//
// What it is not: a cache of the pointer *value* across frames. A new frame always takes a new sample, so a cursor
// that moved, appeared or went away is seen on the very next frame - the failure this replaces was a sample from
// the previous frame being read as if it were this one.

/// <summary>
/// One sample per frame: the first caller in a frame owns the sample, every other caller in that same frame reads
/// it, and the next frame starts over.
/// </summary>
public sealed class ResultPointerFrameCache
{
    /// <summary>`int.MinValue` rather than 0: frame 0 is a real frame.</summary>
    private const int NoFrame = int.MinValue;

    private int _frame = NoFrame;
    private int _samples;

    /// <summary>The frame the current sample belongs to, or `int.MinValue` when there is none.</summary>
    public int Frame => _frame;

    /// <summary>How many samples have been taken in this cache's life (one per frame that asked).</summary>
    public int Samples => _samples;

    /// <summary>Whether this frame's sample has already been taken.</summary>
    public bool Sampled(int frame) => frame == _frame;

    /// <summary>
    /// Claim this frame's sample. True for exactly one caller per frame - the one that must take it - and false for
    /// every later caller in the same frame, which reads the sample already taken.
    /// </summary>
    public bool Claim(int frame)
    {
        if (frame == _frame) return false;
        _frame = frame;
        _samples++;
        return true;
    }

    /// <summary>Forget the frame: the next caller samples again, even if it is the same frame as the last one.</summary>
    public void Forget()
    {
        _frame = NoFrame;
    }
}
