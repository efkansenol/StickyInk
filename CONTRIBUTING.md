# Contributing to StickyInk

Thanks for wanting to help. This project is small on purpose, and it is set up
so that you can contribute with an AI coding assistant ("vibe coding") as easily
as by hand.

## Setup

1. Install the [.NET SDK](https://dotnet.microsoft.com/download).
2. Fork and clone the repository.
3. Run `build.cmd`, then start `bin\StickyInk.exe`.
4. Run `test.cmd`. It should print a line starting with `PASS`.

That is the whole toolchain. There are no other dependencies.

## Contributing with an AI assistant

1. Open the repository folder in your assistant (Claude Code, Cursor, Copilot, ...).
2. Make sure it reads [AGENTS.md](AGENTS.md). Claude Code picks it up
   automatically; for other tools, start with *"Read AGENTS.md first."*
3. Describe what you want in plain words. For example:
   - *"Add a purple color to the toolbar."*
   - *"Add an arrow tool: drag to draw a straight line with an arrowhead."*
   - *"Make F8 toggle the highlighter directly."*
4. Ask the assistant to run `test.cmd` when it is done, and try the change
   yourself. The automatic test checks that ink follows scrolling; it cannot
   check how drawing *feels* or how the toolbar *looks*. That part is yours.

A tip: when something does not work, tell the assistant exactly what you did and
what you saw ("in Chrome, after scrolling down three screens and back, the line
is about 20 pixels too low"). Precise symptoms get precise fixes.

## Sending a change

- One change per pull request.
- `test.cmd` must pass. Run it a few times; tracking bugs can be intermittent.
- Say in the pull request what you tested by hand, and in which applications.
- Keep everything in English.

## Ideas

Easy:

- More colors, or a custom color picker.
- More pen sizes, or changing the size with the mouse wheel while holding a key.
- Configurable hotkeys.
- Remember the toolbar position between runs.
- A tray icon with a quit entry.
- Start with Windows (optional).

Medium:

- Shapes: straight line, arrow, rectangle, ellipse.
- A text tool.
- Redo.
- Save and reload strokes for a page.
- Pressure sensitivity for pen tablets.
- Per-monitor toolbar placement.

Hard:

- Follow horizontal scrolling too.
- Follow zooming.
- Keep strokes attached when a window is moved.
- Recognise sticky headers and hide ink behind them.
- Lower CPU use by capturing only the windows that have ink on them.

## Reporting bugs

Please include:

- which application you were drawing on (and its zoom level, if it has one);
- your display scaling (Settings → Display → Scale) and number of monitors;
- what you did, what you expected, and what happened.
