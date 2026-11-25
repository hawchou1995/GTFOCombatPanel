[assembly: System.Reflection.AssemblyVersion(HawChow.GTFOCombatPanel.PluginInfo.Version + ".0")]
[assembly: System.Reflection.AssemblyFileVersion(HawChow.GTFOCombatPanel.PluginInfo.Version + ".0")]

namespace HawChow.GTFOCombatPanel;

internal static class PluginInfo
{
    internal const string Guid = "HawChow.GTFOCombatPanel";
    internal const string Name = "GTFOCombatPanel";
    // 1.19.49 (2026-11-23): the local carrier was 1.19.40 while the sources already carried the 1.19.41-1.19.48 work, so the
    // version could not tell two builds apart. This number is the one source of the assembly version, the file version, the
    // loader line, the settlement save's own "version" field and the Thunderstore manifest, and every fix delivery bumps the
    // patch digit before it is built - never the old number with different code behind it.
    // 1.19.50 (2026-11-23, review of the 1.19.49 build): the round-owned obstacle list, the reported margin and the absence
    // bound. 1.19.49 is a review build that was never packaged or published; this number is the one source of the assembly
    // version, the file version, the loader line, the settlement save's own "version" field and the Thunderstore manifest, and
    // every fix delivery bumps the patch digit before it is built - never an old number with different code behind it.
    // 1.19.51 (2026-11-23, the recorded 1080p success page): the success band's lower wall is the interaction that reaches the
    // board's own column, so a control standing beside the board no longer ends the band (the recorded page read 249..439 =
    // 190 px while the page offered 249..966 = 717 px, and showed eight of its thirty rows). The placed board's line now
    // states the rows the plate really holds and the plate's own height.
    // 1.19.52 (2026-11-23, review of the 1.19.51 build): F1 the success strip hangs from the *drawn* completion floor as well
    // as the laid-out edge (the recorded page read own=198 while the part drew to 333, so the board's top covered the
    // captions), and a standing board is deferred if the section draws past its band; F2/F6 the board's own column is
    // measured by the fit (two-phase, bounded) and used by the strict, the fail-open and the re-measured band alike,
    // never the page's footer control; F4 a refreshed grid with a new width is re-centred before it is committed; F5 the
    // success page may put the board in the free band below its actions when the strip above cannot show the whole table.
    // 1.19.53 (2026-11-23, the last open item of the 1.19.51 review): F1 the *band* now hangs from the same combined
    // anchor the on-screen check protects (`ResultSuccessBandAnchor.Resolve`: max(measured icon row, drawn floor)) - 1.19.52
    // raised `completion` to 333 but still resolved the band from `IconRowBottom()` = 233, so the region line said 349
    // while the board was placed from 249 and then refused for ever by the very check that knows the floor; the check also
    // judges the real plate (`board.Y`, header included) now, not the band's top. F6 the column uses the measured width
    // with `TryPlace`'s own centring (no implicit `min(width, screen - 48)`). The fit reserves the header in its box, so a
    // short band shows every row at the largest glyph that really fits (the recorded 426 px lower band: 8 px, 30/30)
    // instead of answering a glyph the placement then refuses.
    // 1.19.56 (2026-11-24, independent review of the 1.19.55 build): the pointer conversion is the 1.19.54 flip again - `Camera.WorldToScreenPoint` already answers in window pixels, so 1.19.55 converted a point that needed none (C); the scan and the hide now ask one classifier (`ResultVanillaClassify`), so a decoration inside the page own artifact/report blocks is neither a wall nor impossible to hide; the failure band applies the live title gap exactly once; `ResetPlacement` captures the page and the scroll owner before it clears them; an early board no longer short-circuits the poll and a success page with no provable footer gets a board at all (D).
    // 1.19.55 (2026-11-24, the recorded 2026-11-23 four-run session at 2560x1440 / 1280x960 / 1920x1080 / 1920x1080):
    // D the success board enters early and non-interactively the moment the page is live and this machine holds its own rows
    //   (`ResultEarlyBoardPolicy`), so no page needs F8 to show a table; the two loops behind "it only appears after F8" are
    //   fixed at the root - `Refresh` asked `_holdPage` (written only by a successful placement) whether this was a new page, so
    //   every refused repaint ran `ForgetPage` (895 `released` / 2,873 `completion parts` lines, `strictRefusals=1 of 60`), and
    //   `ResetPlacement` wiped `_widthProbe` every frame so the two-phase column could never finish. A host failure band's top
    //   is the live mission-failed title's own bottom again, so the table can never stand over it.
    // A the vanilla blocks are put away branch by branch when a presentational block carries an unregistered, switched-off
    //   `CM_Item`: the player's setting is honoured, the live action is untouched, and the page root is still never touched.
    // B the toggle is committed with a real clearance and a conflict is an overlap deeper than the page's own breathing, so a
    //   2 px hover drift no longer pauses and releases the button 32/30 times a session.
    // C the pointer, every measured rectangle and the drawn board go through one camera-raster -> window-pixel conversion
    //   (`ResultPointerSpace`), and one bounded `settlement space` line per page/window puts the GUI mouse position, the page
    //   pointer, the physical mouse and the committed rectangle side by side for the 4:3 page.
    // E the release line is once per page and the per-page budgets survive a repaint.
    // 1.19.57 (2026-11-24, independent review of the 1.19.56 build, five point fixes): the classifier's block identity is
    // aligned onto the node both sides belong to (`SameNode`: transform, then GameObject) instead of comparing a component's
    // pointer with an ancestor transform's - the comparison that could never be true, so no node was ever "inside" a
    // decoration block; the page is handed in and the three switches decide which block is a decoration at all
    // (`ResultVanillaBlockPolicy`), so a block the player asked to keep keeps the 1.19.54 fail-safe. `Registered` outranks
    // `ContainerRoot`/`VisualOnly`, `Unknown` is never `WholeBlock`, an unreadable registry is "registered" rather than
    // "free to hide", and only a branch that is provably inside the page's own block, carries no action and really renders
    // is moved - so a mixed block gives up its own visuals without ever taking a control with it. A standing early board is
    // kept across the retries of its own page (`ResultEarlyBoardPolicy.KeepsStanding`) instead of being reset and restored
    // every cadence, and the mature board replaces it once, from the same measurement.
    // 1.19.58 (independent review of the 1.19.57 build, one point fix): `HideSafeBranches` refused a branch only through
    // `CarriesAction` - "is *this node* an action" - so a branch whose own node is a plate with a renderer while a
    // *descendant* is a registered `CM_Item` was read as a proven visual and moved. The second, independent question is
    // asked beside it (`LiveActionIn(node, page)`: does anything below sit in the page's registry), and it answers true
    // when the registry or the subtree cannot be read - so an unreadable read can only leave a branch where the game put it.
    // 1.19.59 (2026-11-24, the delivery carrier of the reviewed 1.19.58 source): the five point fixes of the
    // independent review had already been written behind the 1.19.58 number, so a delivered DLL could not be told
    // apart from the reviewed one - the move half is taken at the first real move, the drawing half owns no raycast
    // and gives a button back, only the state that was really taken is put back, a taken block is not measured again
    // every frame, and one pointer sample serves the whole frame. The code is unchanged from that review build; only
    // this number moves. This number is the one source of the assembly version, the file version, the loader line,
    // the settlement save's own "version" field and the Thunderstore manifest, and every fix delivery bumps the patch
    // digit before it is built - never an old number with different code behind it.
    // 1.19.60 (2026-11-24, the recorded 2026-11-24 client session): two point fixes, each one a rule that was
    // answered at the wrong point in the pipeline.
    // (1) The public statistics row set. A client page put two seats that run no mod on the level HUD with a bare
    //     row - dashes for a weapon, zeros for a kill - because this machine's own medkit had opened a row for
    //     them in the capture store and every row in that store was projected onto the board. `Project` now asks
    //     `SquadRowEligibility` at that one boundary: this machine's own row, a row this run really received
    //     through the exchange (which the receiver-side gate has already qualified), or a seat this machine is
    //     the authority for. Not the roster, not a handshake, not how many medkits were applied, not a row of
    //     zeros and not a name. The receiver's credit stays in the store, so a trusted row that arrives late
    //     merges on top of it and no real zero and no unknown is touched.
    // (2) The client's automatic first board. `ResultEarlyBoardPolicy.Allows` gained `client`/`boundsFinal`: on a
    //     client page the early board is placed only into the strip the mature board would use (the page's own
    //     reveal has answered and the lower wall is a control it really measured), so the recorded 1151x540 at
    //     12 px no longer becomes 949x450 at 10 px one second later - the first board *is* the final board and
    //     the only thing left for the mature placement is growth. No frame count, no budget and no glyph cap is
    //     introduced anywhere, and a host page's early board is unchanged.
    // 1.19.61 (the recorded 2026-11-25 client failure page, 2560x1440): the client footer slot has one boundary, and
    // every consumer reads that one.
    // (1) The wall carries its source. `ResultClientWallGate.Observe` keeps a sample per source: tightening is
    //     immediate (the higher edge is always the safe one), relaxing needs the *same* source to read the same value
    //     on two different complete frames, and a weaker source (the layout box) can never replace a stronger one (the
    //     native slot, the rendered glyphs). `Settled` is that same two-sample question, not "the retained value did
    //     not move": the recorded 1344 -> 1334 -> 1344 oscillation authorises nothing, and a blind frame breaks the
    //     streak. That is the rule this page needs - it went 1344 -> 1334 -> about 1392, 1.19.60 relaxed to it, and the
    //     band landed at 1371 with the board at 1373 while the per-repaint check read footerTop 1338.
    // (2) The authority is the same native return position the host measures, and nothing else: the client's own
    //     `m_btnGoToLobby` laid-out rectangle (never asked whether it is clickable - on a client it permanently is
    //     not, and that is not the same as absent) and the waiting line's actually rendered glyph vertices
    //     (`textInfo.meshInfo[..].vertices`), whichever reaches higher. The layout box, another pressable control and
    //     the page's own screen-derived foot authorise nothing; with neither native source readable the page keeps the
    //     vanilla screen and is measured again, and the fail-open/F8 band is refused the same way - including the
    //     extension that would cross the wall to reach the window's foot.
    // (3) One boundary for the fit and for the per-repaint check: the client branch reports `clientBand.WallTop`, the
    //     wall the band's foot was cut from (1.19.60 reported the gate's value instead), and a standing board whose
    //     footer rises is tightened on that frame (`ResultClientStandingBand`, `MoveStandingFailureBoard`) instead of
    //     being withdrawn for a re-fit on every repaint.
    // (4) One measurement per frame and page (`ClientSlot`), and only the two named native objects - no subtree scan
    //     for unrelated or hidden text. The host failure page, the success pages, the fixed-layout contract and the
    //     scoring/protocol paths are untouched.
    // This number is the one source of the assembly version, the file version, the loader line, the settlement
    // save's own "version" field and the Thunderstore manifest, and every fix delivery bumps the patch digit
    // before it is built - never an old number with different code behind it.
    internal const string Version = "1.19.61";
}
