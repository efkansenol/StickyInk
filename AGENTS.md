# Briefing for AI coding assistants

You are working on **StickyInk**, a Windows screen-annotation pen whose ink
scrolls with the page underneath. Read this file fully before changing code.
For the design and its rationale, read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Commands

| Task | Command | Notes |
| --- | --- | --- |
| Build | `build.cmd` | Produces `bin\StickyInk.exe`. |
| Test | `test.cmd` | Builds, runs the end-to-end self-test (~7 s), prints `PASS ...` or `FAIL ...`. Exit code 0 = pass. |
| Run | `bin\StickyInk.exe` | Only one instance runs at a time. |

- The build fails with "file is in use" if StickyInk is running. `test.cmd` stops
  it for you; before `build.cmd`, run `taskkill /im StickyInk.exe /f`.
- The self-test opens a real window on the real desktop and reads the real
  screen. It needs an interactive desktop session, and the test window must not
  be covered while it runs. It does not touch the mouse or keyboard.
- **Run `test.cmd` after every change** to `ScrollTracker`, `StrokeFollower`,
  `AnchorRelocator`, `InkController` or `Pane`. Run it two or three times: timing
  bugs in this code show up as occasional failures.

## Tech constraints

- C# (latest language version) on **.NET Framework 4.8**, WinForms + GDI+ +
  Win32 P/Invoke. Keep it that way: net48 is preinstalled on every Windows, which
  is what makes the app a single small exe with nothing to install.
- **No NuGet dependencies.** If you think you need one, ask the user first.
- Modern APIs that do not exist on net48 (`Span<T>` helpers, `System.Text.Json`,
  etc.) are not available.
- All Win32 declarations go in `src/Native.cs`.
- Nullable reference types are off.

## Where things are

| I want to... | Look at |
| --- | --- |
| add a toolbar button, color, size or hotkey | `ToolbarForm.cs` (see the 4 steps in its class comment), `Palette` in `InkController.cs` |
| add a drawing tool or change how strokes look | `Tool` in `Stroke.cs`, `InkController.cs` (`BeginStroke`, `DrawStroke`) |
| change what mouse input does | `InputHook.cs` |
| change the cursor while drawing | `CursorShield.cs` |
| improve how well ink follows scrolling | `StrokeFollower.cs` (who moves, by how much), `ScrollTracker.cs` (measurement) |
| fix ink ending up in the wrong place after long scrolls | `AnchorRelocator.cs` |
| save / load drawings | new file; `Stroke` in `Stroke.cs` is the data to persist |

## Rules that must not be broken

Each of these exists because breaking it caused a real bug. The stories are in
the "Lessons learned" section of `docs/ARCHITECTURE.md`.

1. **Only the UI thread touches strokes.** From another thread use
   `InkController.PostMouse` or `Control.Invoke`. The only cross-thread state is
   the `volatile` fields on `InkController`.
2. **Update the ink mask before presenting pixels.** After changing stroke
   positions call `InkController.RedrawEverything()`; do not call `Present()` on
   your own with a stale mask. Otherwise the tracker measures our own ink and
   strokes run away.
3. **The hook callback must stay trivial.** `InputHook.OnMouseEvent` runs for
   every mouse event on the whole system. No allocation-heavy work, no I/O, no
   locks, no calls that can block.
4. **Everything is in physical pixels.** Do not introduce DPI-scaled or
   "logical" coordinates. Only the toolbar's own layout multiplies by `scale`.
5. **Never assume the screen is still.** Something unrelated is always
   animating. Do not write logic that waits for "no change on screen".
6. **Unchanged cells are weak evidence.** Fixed UI (title bars, borders, sticky
   headers) is everywhere. Read the comments in `StrokeFollower.Apply` before
   changing any threshold there.
7. **Never move or resize the cursor shield while it is visible.**
8. **The overlay stays click-through and never takes focus.** Keep
   `WS_EX_TRANSPARENT` and `WS_EX_NOACTIVATE`.

## Working style

- Keep files single-purpose and small. If a file grows a second
  responsibility, split it.
- Comments explain *why*, especially for thresholds and ordering. Constants that
  tune behaviour live in a `// ---- tuning ----` block at the top of the class
  with a comment each.
- A change to the tracking logic is not done until `test.cmd` passes. If you
  fix a tracking bug, try to extend `SelfTest.cs` so it would have caught it.
- The self-test cannot check things that need a real mouse (the cursor, the
  toolbar, drawing by hand). Say so plainly to the user and tell them exactly
  what to try by hand; do not claim those parts are verified.
- All code, comments, UI text and docs are in English.

## Good first tasks

See the idea list in [CONTRIBUTING.md](CONTRIBUTING.md).
