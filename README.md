# StickyInk

**Draw on your screen. The ink scrolls with the page.**

StickyInk is a tiny screen-annotation pen for Windows. Like other screen pens it
lets you draw over anything - a web page, a PDF, a document, your code. Unlike
other screen pens, your drawing is not glued to the glass: scroll the page and
the ink moves with the content, staying exactly on the words you marked.

Circle a sentence, scroll down, underline another one, scroll back up - the
circle is still around the same sentence.

## Highlights

- **Ink that follows the content.** Scroll with the mouse wheel, the keyboard,
  the scrollbar or a touchpad; strokes travel with the page and come back when
  you scroll back.
- **Works in any application.** Browsers, PDF readers, editors, chat apps. There
  is no plugin or per-app integration: StickyInk watches the screen itself to see
  how far the content moved.
- **Self-correcting.** Each stroke remembers the content around it. When that
  content is on screen again the stroke snaps to the exact right pixel, so long
  scrolls do not leave drawings slightly off.
- **Each stroke follows its own window.** Two windows side by side scroll
  independently, and so does the ink on each of them.
- **Keep scrolling while you draw.** In pen mode the mouse wheel still reaches
  the page underneath.
- **Pen, highlighter and eraser**, six colors, three sizes, undo, clear.
- **Never steals focus.** The toolbar and the ink layer do not take keyboard
  focus from the app you are annotating.
- **Shows up in screenshots.** The ink is really on screen, so screenshots and
  screen recordings include it.
- **One small file, nothing to install.** A single ~40 KB `.exe` that runs on a
  stock Windows 10 or 11. No installer, no runtime download, no background
  service, no network access.

## Getting it

**Download:** get `StickyInk.exe` from the
[latest release](https://github.com/efkansenol/StickyInk/releases/latest) and
double-click it. That is all.

Windows may show a "Windows protected your PC" warning the first time, because
the file is not code-signed. Choose *More info → Run anyway*, or build it
yourself from the source instead.

**Or build it yourself** - it takes a few seconds:

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) (any recent version).
2. Clone this repository.
3. Run `build.cmd`.
4. Start `bin\StickyInk.exe`.

Requirements to *run* it: Windows 10 (version 2004 or newer) or Windows 11.
Nothing else.

## Using it

A small dark toolbar appears at the top of the screen. Drag it by its left edge
to move it.

| Action | How |
| --- | --- |
| Start / stop drawing | **F9**, or click a tool on the toolbar |
| Stop drawing | **Esc** |
| Draw | Hold the left mouse button |
| Scroll while drawing | Mouse wheel, as usual |
| Undo the last stroke | **Ctrl+Z** (while drawing) |
| Clear everything | **Shift+F9** |
| Erase | Pick the eraser and touch a stroke |
| Quit | The ✕ button on the toolbar |

While a drawing tool is active the cursor is a crosshair and left clicks draw
instead of clicking. Press F9 or Esc to get your mouse back.

## How it works

StickyInk never talks to the application you are drawing on. Instead it looks at
the screen, a few dozen times per second, and works out how far the pixels
moved:

1. The screen is cut into thin horizontal strips (64 x 1 pixels) and each strip
   is reduced to a hash.
2. Each strip is looked up in the previous capture. "This strip of text used to
   be 37 pixels lower" is one vote for "the content scrolled up by 37 pixels".
3. Every stroke collects the votes around itself and moves by the winning amount.
4. Afterwards each stroke checks its *anchor* - a fingerprint of the content it
   was drawn on - and corrects any leftover error.

The full story, including the traps this design has to avoid, is in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Limitations

- **Vertical scrolling only.** Horizontal scrolling is not followed.
- **Windows only.**
- **Big instant jumps can be missed.** If a page jumps so far that the old and
  the new view share no content (Ctrl+End, clicking a link), there is nothing to
  measure. The stroke is put back in place when its content returns to the screen.
- **Changed content cannot be recognised.** If the page reloads, reflows or is
  zoomed, anchors stop matching and strokes rely on frame-by-frame following.
- **Fixed elements inside a page.** A stroke passing a sticky header is drawn
  over it; StickyInk cannot tell the header from the page.
- **F9, Esc and Ctrl+Z** are taken over while drawing is on (Esc and Ctrl+Z only
  then).
- **Strokes are not saved.** They live until you clear them or quit.

## Contributing

Contributions are welcome - including ones written with an AI coding assistant.
The code base is deliberately small, split into one-purpose files and commented
for that way of working.

- [CONTRIBUTING.md](CONTRIBUTING.md) - how to build, test and send a change, plus
  a list of ideas to pick from.
- [AGENTS.md](AGENTS.md) - the briefing for AI assistants (Claude Code, Cursor,
  Copilot, ...). Point your assistant at it before you ask for a change.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) - how the scroll-following works.

## License

[MIT](LICENSE)
