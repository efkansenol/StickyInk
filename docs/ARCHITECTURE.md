# How StickyInk works

This document explains the design and, more importantly, *why* it is the way it
is. Most of the "why" was learned from bugs; they are listed at the end so that
nobody has to rediscover them.

## The parts

```
                    ┌──────────────────────────── UI thread ───────────────────────────┐
 mouse ─► InputHook ─► InkController ──draws──► OverlayWindow (transparent, full screen)
 (own thread)          │  strokes, tools,
                       │  ink mask
                       │        ▲                       ▲
                       │        │ moves strokes         │ snaps strokes to anchors
                       │   StrokeFollower          AnchorRelocator
                       │        ▲                       ▲
                    └───────────┼───────────────────────┼──────────────────────────────┘
                                │ FrameMatch            │ cell hashes
 screen ──capture──► ScrollTracker (own thread) ────────┘

 ToolbarForm: the visible toolbar; creates and owns everything above.
```

| File | Responsibility |
| --- | --- |
| `Program.cs` | Entry point: single instance, DPI awareness. |
| `ToolbarForm.cs` | The toolbar UI, global hotkeys, and wiring the parts together. |
| `InkController.cs` | Strokes, tools, colors; turning mouse events into strokes; rendering; the ink mask. |
| `OverlayWindow.cs` | The transparent click-through window the ink is drawn on. |
| `InputHook.cs` | Low-level mouse hook: captures drawing input, lets everything else through. |
| `CursorShield.cs` | Invisible window that keeps the cursor a crosshair while drawing. |
| `ScrollTracker.cs` | Captures the screen, hashes it, matches consecutive frames. |
| `StrokeFollower.cs` | Per stroke: decides how far its content scrolled and moves it. |
| `AnchorRelocator.cs` | Per stroke: remembers surrounding content and snaps the stroke back onto it. |
| `Pane.cs` | Finds the window under a stroke and its bounds. |
| `Grid.cs` | Screen geometry: bands, cells, flags. |
| `Stroke.cs` | The `Stroke` and `StrokeAnchor` data classes. |
| `Native.cs` | All Win32 P/Invoke declarations. |
| `SelfTest.cs` | The end-to-end test (`test.cmd`). |

## Coordinates

The process is per-monitor DPI aware, so **every number is a physical pixel**.
"Overlay coordinates" are screen coordinates with the top-left corner of the
virtual screen (all monitors together) moved to (0, 0).

A stroke stores its points as drawn and never changes them. Scrolling only
changes `Stroke.OffsetY`; the stroke is rendered at `Points + OffsetY`.

## Bands and cells

The screen is divided into vertical **bands** 64 pixels wide. A **cell** is one
pixel row of one band: a 64 x 1 strip. Everything the tracker knows is stored per
cell in flat arrays (`Grid.Cell(band, row)`).

Why this shape?

- Vertical scrolling moves a strip to another *row* but keeps it in the same
  *band*, with exactly the same pixels. So "where did this strip go" is an exact
  hash lookup, not an expensive image comparison.
- Bands keep things local. A fixed sidebar, a second window, a video - each only
  affects the bands it covers.

Each cell gets flags (`CellFlags`): `Flat` (all one color - useless) and `Inked`
(covered by our own ink - must be ignored). A cell with no flags is "usable".

## The pipeline

### 1. Capture and hash (`ScrollTracker`, background thread)

Roughly every 15 ms while things are moving (45 ms when idle), the whole screen
is copied with `BitBlt` and every cell is hashed. With no strokes on screen the
tracker sleeps and costs nothing.

### 2. Match frames (`ScrollTracker.MatchFrames`)

The new frame ("current") is compared with the previous one ("reference"). For
each band, the reference rows are indexed by hash; then each current cell is
looked up. The result is a `FrameMatch`:

- unchanged cell → one candidate shift: `0`
- changed, found in the reference → up to six candidate shifts
- changed, not found → marked `ChangedNoMatch`
- flat, inked or too ambiguous → no information

If enough cells agree on one non-zero shift, a scroll has been detected and the
match is handed to the UI thread.

If the frame changed but could *not* be matched (a blurry mid-animation frame,
for instance), the old reference is kept for a few frames. A later, sharper
frame is then matched against it, and no movement is lost.

### 3. Move the strokes (`StrokeFollower`, UI thread)

There is no single "the screen scrolled by N". Each stroke asks its own
surroundings, in widening circles:

1. **Nearby** - the stroke's bands, ±250 rows. Unchanged cells count here: this is
   how a stroke on something that is not scrolling knows to stay put.
2. **Its column** - the same bands over the full height of its pane.
3. **Its pane** - the whole window it was drawn on.
4. **The whole screen** - only if the pane is unknown.

From step 2 on, only *changed* cells vote.

### 4. Correct drift with anchors (`AnchorRelocator`, UI thread)

Frame-by-frame following loses a pixel here and a frame there. Over a long
scroll that adds up. So shortly after a stroke is finished, an **anchor** is
captured: the hashes of the usable cells around it, stored relative to the
stroke.

Later (after each scroll, a few times per second for about a second) the anchor
is searched for in the current frame. Every anchor cell found votes for the
stroke offset it implies. A clear winner sets `OffsetY` *absolutely*, wiping out
all accumulated error. This also recovers strokes after jumps too large to
follow.

## Panes

The **pane** of a stroke is the window under it when it was drawn - for a
browser, the page area without the tabs and toolbar. It is used to:

- limit measurements to the stroke's own window;
- clip the ink, so a stroke scrolled out of the page does not appear on top of
  the browser toolbar.

## Threads

| Thread | Does | Rules |
| --- | --- | --- |
| UI | Toolbar, all stroke data, rendering, moving strokes | The only thread that may touch strokes. |
| Input hook | Low-level mouse hook, cursor shield | Must return within microseconds; it delays *all* mouse input on the system. |
| Scroll tracker | Capture, hashing, matching | Calls into the UI thread with `Invoke` and waits. |

Cross-thread state is limited to the `volatile` fields of `InkController`.

## Lessons learned (do not undo these)

**The ink must never measure itself.** The ink is on screen, so captures contain
it. If the tracker saw the ink move (because we moved it), it would conclude the
page scrolled *again* and move it again, forever. Hence `InkMask`: cells under
ink are excluded. The mask must be updated **before** the pixels are presented,
and the masks of the previous two frames stay in force, because ink that just
moved can still be on screen in its old place for a frame.

**Things that do not scroll are everywhere.** Title bars, window borders,
toolbars and sticky headers produce "unchanged" cells that vote for "no scroll".
A stroke in a blank margin has no content of its own nearby, so a handful of
those cells used to pin it in place. That is why: a "static" verdict needs at
least 40 cells; from the column level on only changed cells vote; pane bounds
exclude the title bar and borders; and only bands fully inside the pane are used.

**Something else is always moving.** A video, a spinner, a blinking cursor in
another window. Any logic of the form "when the screen is completely still" will
never run on a real desktop, and any "most changed cells agree" test fails when
a video is playing. Scroll detection therefore also accepts an absolute number
of agreeing cells, and the anchor step runs on a timer, not on stillness.

**Anchors can contain fixed things.** If an anchor includes cells of a fixed
sidebar, those cells will always be found at the original position and will drag
the stroke back there. So whenever a stroke moves, anchor cells that stayed put
are dropped (`DropStaticCells`).

**Never capture an anchor mid-scroll.** The stroke's position is only
trustworthy when the area around it is stable; an anchor captured during a
scroll animation would lock in a wrong position permanently.

**Do not move a window under the cursor.** The cursor shield once followed the
mouse. Each move exposed the application underneath for an instant, and the
cursor flickered between the crosshair and the app's cursor. It now covers the
whole screen and never moves.
