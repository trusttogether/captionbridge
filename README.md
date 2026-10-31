# Caption Bridge

A single-window running transcript of **Windows Live Captions**. It follows the captions window
as fast as the system will report changes, and keeps everything that scrolls out of it, in a
large read-only box you can park anywhere on screen.

![Caption Bridge window](docs/window-preview.png)

That is the whole app. There is no browser, no ChatGPT integration and no licensing — just the
caption text, as quickly as it can be read, and all of it.

## Requirements

- Windows 10 version 1809 (build 17763) or later, 64-bit
- Windows Live Captions (Win + Ctrl + L), available on Windows 11 and recent Windows 10 builds
- .NET 8 SDK — only to build. The published exe is self-contained and needs no runtime.

## Getting the exe

```powershell
.\build\publish.ps1
```

This writes a single self-contained `publish\CaptionBridge.exe` (~74 MB) that runs on any
64-bit Windows 10/11 machine with no .NET install.

To build and test without publishing:

```powershell
dotnet build CaptionBridge.sln -c Release
dotnet test  CaptionBridge.sln -c Release
```

## Using it

Start `CaptionBridge.exe`. It turns Windows Live Captions on for you if it is not already
running, so there is nothing to set up first.

The window follows whatever Live Captions is showing and keeps itself scrolled to the newest
text. Live Captions only ever holds the last few lines and throws the rest away; this does
not. Words that scroll out of its window stay here, so the box grows into the whole of what
was said rather than only what is being said. Closing Live Captions stops the transcript
growing but does not take it away, and the title bar reads "waiting for Live Captions".

Scroll up to read back through it, or press **Clear** on the bottom bar to start again.
The transcript is held in memory only: it is not written to disk, and closing the window
ends it.

Starting Live Captions is skipped when it is already running, because the system shortcut that
turns it on is a toggle and pressing it then would turn the captions off. Pass
`--no-autostart` to leave it alone entirely.

| Control | What it does |
| --- | --- |
| Left slider | Caption font size (10–48 pt) |
| Right slider | Window opacity (20–100%) |
| Always on top | Keeps the window in front of everything else. On by default |
| Clear | Empties the transcript so you can start a new one |
| Ctrl + A / Ctrl + C | Select all / copy |
| Right-click | Copy, select all, clear, refresh rate, always on top, start Live Captions, exit |

The caption box is **read-only** but behaves like ordinary text: a left click places the caret
and drops whatever was selected, and a drag marks a new range from that point. The first
click stays put while captions keep arriving; only a new click starts a different range.
Copy uses that range, not an earlier one. Selecting never stops the mirror, and the mirror
never re-selects or widens what you picked — new words keep landing after it, so you can
copy something mid-sentence.

The view is pinned to the newest line except while you are actually dragging, when it holds
still — pulling the text down mid-drag would slide the words out from under the pointer and
make a range impossible to place. Letting go puts the view back on the newest line.

The window sits above other windows by default, which is the point of a caption box. The
checkbox in the bottom bar and **right-click → Always on top** are the same switch, and it is
asserted again a moment after startup, because Live Captions is itself an always-on-top window
and comes up in front of this one.

Window size, position, font size, opacity, always-on-top and refresh rate are remembered in
`%AppData%\CaptionBridge\settings.json`. Launching the exe a second time raises the window
that is already running instead of starting another copy.

## How it keeps up

The default refresh is 25 ms, and a measured end-to-end round trip from "caption text changes"
to "text visible in the window" lands around 40 ms. Three things buy that:

- The Live Captions window is found by enumerating top-level window handles, which is far
  cheaper than searching the UI Automation tree, and the handle is cached until it goes away.
- Each poll pulls the caption text in a **single** cross-process call using a UI Automation
  `CacheRequest` rather than walking the element tree property by property.
- The poll loop owns a dedicated thread and raises the system timer resolution, so a 25 ms
  sleep is actually 25 ms rather than the default ~16 ms granularity.

A refresh costs two edits at most. Comparing the old text with the new one says how much
scrolled off the front and which words at the end were rewritten; the box is brought up to
date by deleting the first and replacing the second, so it repaints the affected lines instead
of the whole transcript. The transcript itself grows on the reader's own thread, so no reading
of the captions window is ever skipped, however busy the window is.

Joining one reading of the captions window to the next is the only part of this that has to
guess. Live Captions revises what it is showing as it grows more confident — a word changes, a
comma appears, a sentence is capitalised — so two readings of the same speech can have nothing
in common at the front. They are compared as characters first, which is exact and cheap and
covers almost every refresh; failing that they are lined up word by word, ignoring punctuation
and case, and the alignment that puts the most words in the same places wins. Only what the
alignment leaves behind the newest reading is treated as history. Get this wrong in one
direction and a sentence is in the transcript twice; wrong in the other and it is lost. The
transcript holds up to 500,000 characters, upwards of ten hours of speech, after which the
oldest words come off the front.

Those edits go through the caption box's **text object** rather than its selection. That is the
detail the rest of the behaviour rests on. Every ordinary way to change an edit control's text
works through the selection and therefore destroys it, which is why a mirror built on a plain
text box has to choose between following the captions and letting you select — the old build
wrote the text and then put the selection back on afterwards, which fought the drag it was
trying to preserve. Editing through a range moves neither the caret nor the selection, so
there is nothing to put back.

If your machine is busy, drop the rate with **right-click → Refresh rate**.

## Repository layout

```
src/CaptionBridge/           the application
  Program.cs                 entry point and single-instance handling
  CaptionBridgeForm.cs       the window
  CaptionTextBox.cs          the caption box, edited without disturbing the selection
  Services/
    LiveCaptionReader.cs     the polling loop that mirrors Live Captions
    LiveCaptionsWindowFinder.cs
    CaptionTextCleaner.cs    strips window chrome from the raw text
    CaptionTranscript.cs     joins each reading of the window onto the running transcript
    CaptionTextEdit.cs       how the text moved between two polls
    RichEditDocument.cs      the text object the caption box is edited through
    CaptionSettings.cs       settings and their JSON store
tests/CaptionBridge.Tests/   unit tests for the text cleaning and the caption box
build/publish.ps1            produces the single-file exe
tools/generate-icon.ps1      regenerates the app icon
tools/verify-mirror.ps1      end-to-end check against a stand-in captions window
tools/capture-window.ps1     screenshots the window for this README
```

## Verifying a change

`tools/verify-mirror.ps1` launches the published exe against a stand-in window titled
"Live captions", feeds it phrases, and reports how long each one took to appear. It also
checks that captions keep arriving while text is selected, that the selection stays on its
words as the text is appended to and scrolls off the front, that nothing said earlier is
dropped from the transcript, that the view returns to the bottom, that the transcript survives
captions disappearing and coming back, and that a second launch does not start a second
process.

Close Windows Live Captions before running it. The app finds its source window by title, and
a real Live Captions window wins that match over the stand-in, which makes every measurement
below it meaningless. The script warns when it sees one, and launches the exe with
`--no-autostart` so the app does not start one itself.
