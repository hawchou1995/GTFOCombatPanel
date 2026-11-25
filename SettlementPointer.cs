using System;

namespace HawChow.GTFOCombatPanel;

// 1.19.44: the settlement page's own pointer and its own main button, ported from the panel the user already runs
// (`conflictmodlist/src/TeamVersionsDriver.cs` HandleMouse/CursorWorldPosition/MainButtonStatus and
// `shared/PanelPointer.cs` PointerMath/ClosePressGate/PressOwnership), with the same rules and none of the globals.
//
// What changes for this mod: the sample is the *settlement page's* `CM_PageBase.CursorWorldPosition` projected through
// the camera this build already proves for that page, and the physical mouse is only a labelled fallback for the frames
// in which that page cannot answer. `CM_PageBase.MainButtonStatus` is the press the game itself dispatches on, so a mod
// control pressed with a hidden system cursor answers exactly like a native one; `Input.GetMouseButton(0)` stands in
// only when the page's own state cannot be read, and the source is printed so a log can tell the two apart.
//
// Nothing here is framework state: no `Cursor.visible`, no lock, no capture, no permission, no global input mode. Every
// rule below is a pure function of values the caller read this frame, which is what lets the headless suite assert them.

// Where one frame's pointer sample came from. `PhysicalMouse` is a labelled fallback, never a silent substitute.
public enum ResultPointerSource
{
    None = 0,
    PageCursor = 1,
    PhysicalMouse = 2
}

// One frame's pointer, already in the window space every measured rectangle of this mod lives in (y down from the top).
// `Valid == false` means "no usable pointer this frame", and the caller must treat it as no hit at all - never as the
// previous frame's coordinates.
public readonly struct ResultPointerSample
{
    public readonly bool Valid;
    public readonly float X;
    public readonly float Y;
    public readonly ResultPointerSource Source;
    public readonly string Note;

    public ResultPointerSample(bool valid, float x, float y, ResultPointerSource source, string note)
    {
        Valid = valid; X = x; Y = y; Source = source; Note = note ?? string.Empty;
    }

    public static readonly ResultPointerSample None = new ResultPointerSample(false, 0f, 0f, ResultPointerSource.None, "not sampled");

    public static ResultPointerSample Invalid(ResultPointerSource source, string note) => new ResultPointerSample(false, 0f, 0f, source, note);

    public string Describe()
        => (Valid ? "pointer=(" + X.ToString("0") + "," + Y.ToString("0") + ")" : "pointer=none")
            + ", source=" + Name(Source)
            + (string.IsNullOrEmpty(Note) ? string.Empty : " (" + Note + ")");

    public static string Name(ResultPointerSource source)
    {
        switch (source)
        {
            case ResultPointerSource.PageCursor: return "page-cursor";
            case ResultPointerSource.PhysicalMouse: return "physical-mouse";
            default: return "none";
        }
    }
}

// The one conversion between a camera projection and window pixels.
//
// `CM_PageBase.CursorWorldPosition` is a world point; the camera answers in screen pixels with y counted from the
// bottom, and every rectangle this mod measures and draws counts y down from the top. That is one flip, done here and
// nowhere else - not per canvas mode, not twice, and never on an unprojected guess. A point at or behind the camera
// (`z <= 0`) projects to a mirrored coordinate that is not a hit, so it is refused rather than flipped.
public static class ResultPointerMath
{
    public static bool TryFromWorldProjection(float screenX, float screenYFromBottom, float depth, float screenHeight,
        out float guiX, out float guiY, out string note)
    {
        guiX = 0f; guiY = 0f;
        if (!float.IsFinite(screenHeight) || screenHeight <= 0f) { note = "no usable window height"; return false; }
        if (!float.IsFinite(screenX) || !float.IsFinite(screenYFromBottom)) { note = "the projection is not a number"; return false; }
        if (!float.IsFinite(depth) || depth <= 0f) { note = "the cursor is at or behind the menu camera (z<=0)"; return false; }
        guiX = screenX;
        guiY = screenHeight - screenYFromBottom;
        note = string.Empty;
        return true;
    }
}

// The raster facts of one page camera, and what they let this mod *prove* about the grid it is drawing in.
//
// 1.19.56 (review 2/3/10): `Camera.WorldToScreenPoint` already answers in **window** pixels - the bottom-left of the
// screen, 0..Screen.width / 0..Screen.height - `pixelRect` offset included. A letterboxed camera therefore does not hand
// back viewport-relative numbers, and adding `pixelRect.x/y` or scaling by `pixelWidth/Screen.width` converts a point
// that was already a window point. That is what 1.19.55 did and it is wrong. The conversion is the one 1.19.54 used -
// flip y - and the facts below only decide *how much of it can be proven*:
//
//   direct    the camera renders this whole window (`pixelRect` is the window and its own raster is the window's), or
//             the page's canvas is `ScreenSpaceOverlay` and the projection was taken with a null camera. Proven.
//   unproven  a camera that renders into a texture, or metadata this frame could not read. The point is still flipped
//             with `Screen.height` - the 1.19.54 behaviour, kept rather than guessed at - and every line says `unproven`
//             instead of claiming a mapping was validated. Nothing on the page is made unmeasurable because of it: a
//             rectangle this mod cannot prove must still be measured, or the scan would lose the page's own controls.
public readonly struct ResultPointerViewport
{
    public readonly float CameraWidth, CameraHeight;
    public readonly float ViewportX, ViewportY, ViewportWidth, ViewportHeight;
    public readonly float ScreenWidth, ScreenHeight;

    public ResultPointerViewport(float cameraWidth, float cameraHeight, float viewportX, float viewportY,
        float viewportWidth, float viewportHeight, float screenWidth, float screenHeight)
    {
        CameraWidth = cameraWidth; CameraHeight = cameraHeight;
        ViewportX = viewportX; ViewportY = viewportY; ViewportWidth = viewportWidth; ViewportHeight = viewportHeight;
        ScreenWidth = screenWidth; ScreenHeight = screenHeight;
    }

    // True only when the camera really renders this window and its own raster is this window's own pixel grid: the case
    // in which the projection is *proven* to be a window point rather than assumed to be one.
    public bool Whole =>
        CameraWidth > 0f && CameraHeight > 0f
        && Math.Abs(ViewportX) <= 0.5f && Math.Abs(ViewportY) <= 0.5f
        && Math.Abs(ViewportWidth - ScreenWidth) <= 0.5f && Math.Abs(ViewportHeight - ScreenHeight) <= 0.5f
        && Math.Abs(CameraWidth - ScreenWidth) <= 0.5f && Math.Abs(CameraHeight - ScreenHeight) <= 0.5f;

    public bool Usable =>
        float.IsFinite(CameraWidth) && float.IsFinite(CameraHeight)
        && float.IsFinite(ViewportX) && float.IsFinite(ViewportY)
        && float.IsFinite(ViewportWidth) && float.IsFinite(ViewportHeight)
        && float.IsFinite(ScreenWidth) && float.IsFinite(ScreenHeight)
        && CameraWidth > 0f && CameraHeight > 0f && ViewportWidth > 0f && ViewportHeight > 0f
        && ScreenWidth > 0f && ScreenHeight > 0f;

    // One point, in window pixels with y counted down from the window's top edge. **Flip only** - the projection is
    // already a window point, so the viewport rectangle takes no part in this arithmetic. Only what makes a number
    // meaningless is refused: a window with no height, or a point that is not a number.
    public bool TryWindow(float cameraX, float cameraYFromBottom, out float x, out float y)
    {
        x = 0f; y = 0f;
        if (!float.IsFinite(cameraX) || !float.IsFinite(cameraYFromBottom)) return false;
        if (!float.IsFinite(ScreenHeight) || !(ScreenHeight > 0f)) return false;
        x = cameraX;
        y = ScreenHeight - cameraYFromBottom;
        return float.IsFinite(x) && float.IsFinite(y);
    }

    public string Describe()
        => "camera=" + CameraWidth.ToString("0") + "x" + CameraHeight.ToString("0")
            + ",viewport=(" + ViewportX.ToString("0") + "," + ViewportY.ToString("0") + ","
            + ViewportWidth.ToString("0") + "x" + ViewportHeight.ToString("0") + ")"
            + ",window=" + ScreenWidth.ToString("0") + "x" + ScreenHeight.ToString("0")
            + ",space=" + (Whole ? "direct" : "unproven");
}

// One projection, turned into the window pixels this mod draws and hits in. The pointer, the measured control rectangles
// and the drawn board all ask this, so none of them can move without the others. The call never turns "the camera's
// raster could not be proven" into "this rectangle does not exist": it answers the same flip 1.19.54 answered and says the
// mapping is `unproven`, so the one thing a reader may not conclude is that a conversion was validated here.
public static class ResultPointerSpace
{
    public static bool TryWindow(ResultPointerViewport viewport, float cameraX, float cameraYFromBottom, float depth,
        out float x, out float y, out bool proven, out string note)
    {
        x = 0f; y = 0f; proven = false;
        if (!float.IsFinite(depth) || depth <= 0f)
        {
            note = "the point is at or behind the camera (z<=0)";
            return false;
        }
        if (!float.IsFinite(cameraX) || !float.IsFinite(cameraYFromBottom))
        {
            note = "the projection is not a number";
            return false;
        }
        if (!float.IsFinite(viewport.ScreenHeight) || !(viewport.ScreenHeight > 0f))
        {
            note = "the window height could not be read";
            return false;
        }
        proven = viewport.Whole;
        note = proven ? "direct" : "unproven";
        return viewport.TryWindow(cameraX, cameraYFromBottom, out x, out y);
    }

    // The same call when only the point is wanted.
    public static bool TryWindow(ResultPointerViewport viewport, float cameraX, float cameraYFromBottom, float depth,
        out float x, out float y, out string note)
        => TryWindow(viewport, cameraX, cameraYFromBottom, depth, out x, out y, out _, out note);
}

// The identity of one pointer session: the page it was taken on, where the press came from, whether the window has
// focus, and the window it was measured in.
//
// A sample only answers for the session it was taken in. When any of these changes, the previous frame's press state
// describes a different pointer, and carrying it over is exactly how a switch would invent a down or an up edge that
// nobody performed - so the caller resets its gates and its previous-down state instead.
public readonly struct ResultPointerSession : IEquatable<ResultPointerSession>
{
    public readonly IntPtr Page;
    public readonly ResultPointerSource Source;
    public readonly bool Focused;
    public readonly int Width;
    public readonly int Height;

    public ResultPointerSession(IntPtr page, ResultPointerSource source, bool focused, int width, int height)
    {
        Page = page; Source = source; Focused = focused; Width = width; Height = height;
    }

    public static readonly ResultPointerSession None = new ResultPointerSession(IntPtr.Zero, ResultPointerSource.None, false, 0, 0);

    public bool Equals(ResultPointerSession other)
        => Page == other.Page && Source == other.Source && Focused == other.Focused
            && Width == other.Width && Height == other.Height;

    public override bool Equals(object obj) => obj is ResultPointerSession other && Equals(other);

    public override int GetHashCode() => Page.GetHashCode() ^ ((int)Source << 24) ^ (Focused ? 1 : 0) ^ (Width * 31) ^ (Height * 17);

    public string Describe()
        => "page=" + (Page == IntPtr.Zero ? "none" : Page.ToString("X")) + ", source=" + ResultPointerSample.Name(Source)
            + ", focused=" + (Focused ? "yes" : "no") + ", window=" + Width + "x" + Height;
}

// Which source the main button was read from this frame. `None` means neither answered, which is not the same thing as
// "the button is up": the caller then only resets its edges and leaves every other input path alone.
public enum ResultPressSource
{
    None = 0,
    Page = 1,
    Physical = 2
}

// One frame of button state, with the two edges derived exactly once from one source.
//
// The caller keeps the previous *down* state; this is the only place a press or a release is turned into an edge, so a
// frame that samples twice cannot fire twice, and a frame whose source changed cannot inherit the old edge (the caller
// passes `previousDown = down` for the first frame of a session, which yields neither edge).
public readonly struct ResultPressFrame
{
    public readonly bool Available;
    public readonly bool Down;
    public readonly bool Pressed;
    public readonly bool Released;
    public readonly ResultPressSource Source;

    public ResultPressFrame(bool available, bool down, bool pressed, bool released, ResultPressSource source)
    {
        Available = available; Down = down; Pressed = pressed; Released = released; Source = source;
    }

    public static readonly ResultPressFrame Unavailable = new ResultPressFrame(false, false, false, false, ResultPressSource.None);

    public static ResultPressFrame Of(bool available, bool down, bool previousDown, ResultPressSource source)
    {
        if (!available) return Unavailable;
        bool pressed = down && !previousDown;
        bool released = !down && previousDown;
        return new ResultPressFrame(true, down, pressed, released, source);
    }

    public static string Name(ResultPressSource source)
    {
        switch (source)
        {
            case ResultPressSource.Page: return "page-main-button";
            case ResultPressSource.Physical: return "physical-mouse";
            default: return "none";
        }
    }
}

// Which of this mod's own surfaces a point falls on, in the order the pointer must be drawn (the controls stand on the
// plate). `None` means the point is not on anything this mod painted this frame, and the native cursor is left alone.
public enum ResultPointerSurface
{
    None = 0,
    Board = 1,
    Toggle = 2,
    Close = 3
}

public static class ResultPointerHit
{
    // The same half-open hit test every other rectangle in this mod uses, and the same struct the drawing uses.
    public static bool Inside(LayoutRect rect, float x, float y)
        => rect.Width > 0f && rect.Height > 0f && x >= rect.X && x < rect.Right && y >= rect.Y && y < rect.Bottom;

    // The surface a point belongs to, from the rectangles this repaint really painted. A surface that was not painted
    // (`drawn == false`) is never a surface: a hidden board and its withheld close are not places the pointer may be
    // replaced, which is the "never a cursor over something nobody drew" rule.
    public static ResultPointerSurface SurfaceOf(LayoutRect plate, bool plateDrawn, LayoutRect toggle, bool toggleDrawn,
        LayoutRect close, bool closeDrawn, float x, float y)
    {
        if (closeDrawn && Inside(close, x, y)) return ResultPointerSurface.Close;
        if (toggleDrawn && Inside(toggle, x, y)) return ResultPointerSurface.Toggle;
        if (plateDrawn && Inside(plate, x, y)) return ResultPointerSurface.Board;
        return ResultPointerSurface.None;
    }
}

// A press this mod consumed, kept until it is really over.
//
// A control that answers on the press itself has taken the player's intent for that press; letting the game's own
// dispatch see the same press is what makes one click press two things. The ownership therefore lives exactly as long as
// the press does, and it is dropped on the first fact that says it is over - a confirmed release, the page changing, a
// page that is no longer active, a window that lost focus, or the bounded fallback for a release nobody could read.
//
// 1.19.46 (UI review F1): the claim carries the *identity of the page it was made on*, by native pointer, and nothing
// else. It used to be judged against this mod's layout cache (`ResultsDisplay._page`), which the very hide the press
// performs is about to drop - `ApplyBoardVisibility(false)` leaves a repaint that runs `ResetPlacement`, and `_page`
// becomes null in the same frame. The claim was then read as "no page", cleared, and the same physical press went on to
// the game's own dispatch: one click, two things. A layout cache is not evidence about who owns a press; the page
// object the press began on is.
public struct ResultPressOwnership
{
    // The bound, because a release that is never observed must not swallow input for the rest of the expedition.
    public const double MaxSeconds = 1.0;

    public bool Armed { get; private set; }
    // The page the press began on (native pointer), never a layout cache and never "the page I am showing now".
    public IntPtr Page { get; private set; }
    public double ArmedAt { get; private set; }

    // Claim one press for `page`. A claim with no identity is not a claim: it could suppress the press on any page.
    public void Arm(IntPtr page, double now)
    {
        if (page == IntPtr.Zero) { Clear(); return; }
        Armed = true;
        Page = page;
        ArmedAt = double.IsFinite(now) ? now : 0d;
    }

    // Only an already owned press is confirmed, and only by a frame that really saw the button down. A frame whose button
    // state could not be read is *not* a confirmation: the claim is dropped rather than kept on hope.
    //
    // 1.19.46 (UI review F1 close-out): a confirmation does *not* move the clock. The bound is the one the blueprint
    // (`shared/PanelPointer.cs` PressOwnership) applies - one second from the press itself - and renewing `ArmedAt` on
    // every held frame turned that into a sliding window that never closed: a `MainButtonStatus` stuck down, or a
    // release that never reached this page, held input for as long as the page stayed up. The clock therefore starts at
    // `Arm` and is never restarted; only a new press makes a new claim.
    public void Observe(bool confirmedDown)
    {
        if (!Armed) return;
        if (!confirmedDown) Clear();
    }

    public void Clear()
    {
        Armed = false;
        Page = IntPtr.Zero;
        ArmedAt = 0d;
    }

    // Whether the game's own dispatch should skip this press.
    //
    // `candidate` is the page the dispatch is running for, by native pointer. Everything that is not "the same page the
    // claim was made on, still active, still focused, still down and still inside its bound" answers false - and clears
    // the claim, so a stale one can never come back and a page this mod did not press never loses its input.
    public bool ShouldSuppress(IntPtr candidate, bool pageActive, bool focused, double now)
    {
        if (!Armed) return false;
        if (Page == IntPtr.Zero || candidate != Page || !pageActive || !focused) { Clear(); return false; }
        if (!double.IsFinite(now) || now - ArmedAt > MaxSeconds) { Clear(); return false; }
        return true;
    }
}

// One frame's press decision, made once and read by both callers that may ask inside that frame.
//
// 1.19.46 (UI review F1 close-out): the page's own press dispatch (`CM_PageBase.UpdateButtonPress`, through the prefix)
// and this mod's own `Update` may run in either order inside one frame, and the press that starts on one of this mod's
// controls is exactly the press both of them see. The decision therefore lives here, once per frame, instead of inside
// whichever caller happened to run first: the dispatch's prefix may make the decision - and claim the press - *before*
// this mod's own poll has run, which is the one sequence the previous round could not answer. It found no claim yet,
// passed the press through, and the mod answered the same click a moment later: one click, two things.
//
// `Mine` is the whole answer: the press began on a control this mod really painted, the control was drawn recently
// enough to answer, and its gate fired. What is *done* about it is the action layer's business (`PollChromeInput`), and
// only once per frame (`ResultPressAnswer`).
public readonly struct ResultPressDispatch
{
    public readonly int Frame;
    // Whether this frame was a result-page frame and was really evaluated. `false` is `None`, not "no press".
    public readonly bool Live;
    public readonly ResultPressFrame Press;
    public readonly ResultPointerSession Session;
    public readonly bool NewSession;
    public readonly bool PointerValid;
    public readonly float PointerX;
    public readonly float PointerY;
    public readonly bool Fresh;
    public readonly bool Focused;
    public readonly bool SameWindow;
    public readonly bool ToggleInside;
    public readonly bool CloseInside;
    public readonly bool Toggle;
    public readonly bool Close;

    public ResultPressDispatch(int frame, ResultPressFrame press, ResultPointerSession session, bool newSession,
        bool pointerValid, float pointerX, float pointerY, bool fresh, bool focused, bool sameWindow,
        bool toggleInside, bool closeInside, bool toggle, bool close)
    {
        Frame = frame; Live = true; Press = press; Session = session; NewSession = newSession;
        PointerValid = pointerValid; PointerX = pointerX; PointerY = pointerY;
        Fresh = fresh; Focused = focused; SameWindow = sameWindow;
        ToggleInside = toggleInside; CloseInside = closeInside; Toggle = toggle; Close = close;
    }

    // Not a result-page frame: nothing was decided, so nothing may be claimed or answered.
    public static readonly ResultPressDispatch None = default;

    public bool Down => Press.Pressed;
    public bool Held => Press.Down;
    public bool Up => Press.Released;
    // This press is this mod's, so the page's own dispatch must not act on it as well.
    public bool Mine => Live && (Toggle || Close);
}

// The action layer's own memory: the one frame whose press this mod has already answered.
//
// The decision above is shared with the page's own dispatch, so both callers can see the same press as this mod's; the
// *action* must still happen exactly once. Deliberately a frame number and nothing else - not a rectangle and not the
// layout - because the press that hides the board invalidates the layout inside the same frame
// (`ApplyBoardVisibility(false)` -> repaint -> `ResetPlacement` -> `ForgetChrome`), and a ledger that forgot itself with
// the layout would let that one press be answered twice.
public struct ResultPressAnswer
{
    public int Frame { get; private set; }

    public static readonly ResultPressAnswer None = new ResultPressAnswer(-1);

    public ResultPressAnswer(int frame) { Frame = frame; }

    // Whether this frame's press was already answered.
    public bool Covers(int frame) => frame >= 0 && Frame == frame;

    // Book this frame as answered. Nothing about a later frame is ever remembered.
    public ResultPressAnswer Take(int frame) => new ResultPressAnswer(frame);
}
