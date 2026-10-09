# Scratchpad — Requirements

**Version:** 1.0 · **Status:** Implemented (see §8) · **Date:** 2026-10-07 · **Tool id:** `scratchpad` · **Group:** Text Tools

---

## 1. Purpose

A scratchpad is the place between the clipboard and a real file. You paste a response there, keep
half a query, work out a sum or write a reminder you'll want for an hour. Then you move it somewhere
else or forget about it.

Two things make it useful:

1. **No ceremony.** You never name, save or choose a location.
2. **Nothing is lost.** Every keystroke is on disk, and older versions can be found again.

ForgeKitRk adds one more: whatever you jot down can go **straight into any of the other 20 tools**.
None of the standalone scratchpads below can do that.

---

## 2. Market scan

| Product | Platform / price | What it does well | What we take | What we leave |
| --- | --- | --- | --- | --- |
| **Heynote** | Win/Mac/Linux, free, open source | One persistent buffer split into **blocks**. Each block has its own language, highlighting and auto-format. **Math blocks** support variables, functions and units. Global show/hide hotkey. | Blocks with a language each; format a block; math with variables | Currency rates fetched from the internet (breaks P1); Emacs key bindings |
| **Windows Notepad** (11.2307+) | Windows, built in | Session restore: unsaved tabs come back after closing, with no prompt. Can be turned off. | Close without prompts; everything restored; a "start fresh" setting | Restore data held in an **opaque cache** you can't open in another editor |
| **Soulver / Numi** | Mac, paid | Notepad calculator: natural language, a running result per line, variables, units, percentages | A result column per line; variables; `x% of y`; unit conversion | Natural-language parsing beyond a small grammar; currency and crypto |
| **Antinote** | Mac, $5 | "Notes you're going to throw away". Typing `math` on the first line turns on contextual math. Change a value and every dependent line recalculates. | Recalculate on change; a note-level math mode | Online exchange rates |
| **Tot** | Mac/iOS, paid | **Seven** colour-coded notes. The fixed limit forces you to tidy up. | Optional colour tags | The hard limit (offered as an optional "tidy" nudge instead) |
| **Drafts** | Mac/iOS, subscription | Capture first, then **send the text somewhere** with "actions" | Send to a tool via the existing `IToolHandoffService` | Its scripting engine (out of scope, like API Builder scripts) |
| **Itsypad, DevPad, Quick Scratchpad** | Mac / web | Tabs, highlighting for many languages, clipboard history, timestamped capture | Multiple tabs, a timestamp on each note | Clipboard history (separate tool if wanted), iCloud sync |

**Pattern across the market:** apps ask for little, save locally and restore on open. The
developer-focused ones add per-block language and math. **Gap we fill:** none of them ships inside a
toolbox, none uses readable files on disk, and the math in all of them reaches the internet.

---

## 3. Principles applied

| Principle (PRD §1.1) | What it means for Scratchpad |
| --- | --- |
| P1 Confined network | No network code. Unit conversion uses fixed factors; there is no currency conversion. |
| P3 Instant | Opening the tool shows the last note at the caret within 200 ms. Saving never blocks typing. |
| P4 Never lose work | Every change reaches disk within 1 s and is written atomically. A crash, power cut or kill loses at most the last second. |
| P5 Explain failures | A math line that can't be evaluated shows *why* on hover, never `NaN`. A failed save shows a non-blocking banner and keeps retrying. |
| P7 Secrets stay secret | Notes are **plain text on disk**. Text that looks like a token, key or password gets a warning (§4.8). |
| P8 No machine changes | Everything stays in the app's own data folder. No global hotkey, no Jump List, no shell integration (decision 4). |

---

## 4. Functional requirements

### 4.1 Notes

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| **SP-01** | Multiple notes | A note list beside the editor (collapsible) shows every note: title, first-line preview, relative modified time and an optional colour tag. Newest first; **pinned notes on top**. |
| **SP-02** | Zero-ceremony creation | `Ctrl+N` (inside the tool) or the **+** button creates an empty note and focuses it. Nothing is ever asked: no name, no location. |
| **SP-03** | Automatic title | The title is the first non-empty line, trimmed to 60 characters, until you rename the note. An empty note shows "Untitled · 14:05". |
| **SP-04** | Rename, pin, colour, duplicate | From the note's context menu and the options band. Colours: none + 6 theme-safe colours, each with a name for screen readers (P6). |
| **SP-05** | Open on last state | Opening the tool restores the last active note, caret, selection and scroll position, plus the note-list collapsed state. |
| **SP-06** | Search | `Ctrl+Shift+F` searches all notes (titles and content, case-insensitive). Results show the line with the match highlighted, and Enter opens the note at that line. Results for 1,000 notes in under 150 ms. `Ctrl+F` stays the in-editor find (FR-T12). |
| **SP-07** | Import and export | Drop or open `.txt`, `.md`, `.json`, `.sql`, `.xml`, `.log` (≤ 10 MB) as a new note. Export the note with `Ctrl+S` (existing shortcut), using the extension that matches its language. **Export all** writes a `.zip` of plain files. |
| **SP-08** | Delete goes to Trash | Delete moves a note to **Trash** with an Undo strip for 8 s. Trash keeps notes for the **retention period set in Settings → Scratchpad: 20 days by default, 1 to 60** (decision 2), and they can be restored or deleted for good. Emptying Trash asks for confirmation. |
| **SP-09** | Archive | Archive hides a note from the main list without deleting it. "Show archived" filter. |

### 4.2 Editor

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| **SP-10** | Reuse `CodeEditor` | Same font size, word wrap, line numbers and find bar as the other tools, all following Settings. |
| **SP-11** | One language per note | *Decision 1: no blocks.* Each note has one language — Plain, Markdown, JSON, XML, SQL, C#, HTML or Math — chosen in the options band and shown in the note list. A new note is Plain. |
| **SP-12** | Auto-detect language | When text arrives in an empty Plain note, `SmartDetector` suggests a language in an info bar ("This looks like JSON — set the note's language?"). It never switches on its own. |
| **SP-13** | Colouring | Editing stays plain text, as in every other editable pane (FR-T11 colours read-only panes only). |
| **SP-14** | Format | `Shift+Alt+F` or **Format** formats the note with the existing engine for its language (JSON, SQL, XML). The text before formatting is kept in History. A failed format leaves the text unchanged and shows the engine's positioned error. |
| **SP-15** | Large text | Notes up to 10 MB stay editable. Above 2 MB, colouring and live math switch off with a notice. |
| **SP-16** | Timestamp insert | `Ctrl+Shift+;` inserts the local date and time (ISO 8601 by default; format in Settings). |
| **SP-17** | Undo | Undo history per note survives switching notes within a session. It doesn't survive a restart (history snapshots, §4.4, cover that). |

### 4.3 Math

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| **SP-20** | Results panel | A Math note shows a **Results** panel beside the editor: one row per line that evaluates, with the line number, the expression and the result. Clicking a row copies the result without digit grouping. Recalculation runs 120 ms after typing stops, off the UI thread. *(A results column aligned to the editor lines was dropped: with word wrap on, lines and rows cannot stay aligned.)* |
| **SP-21** | Grammar | `+ − × ÷ ^ %`, parentheses, `mod`, decimal arithmetic (`decimal`, so `0.1 + 0.2 = 0.3`). Functions: `sqrt round floor ceil abs min max log ln sin cos tan`, plus `pi` and `e`. |
| **SP-22** | Variables | `rate = 1500` defines a value, and later lines can use `rate`. Changing it updates every dependent line. `prev` refers to the line above and `sum` / `avg` to the lines above, back to the last blank line. |
| **SP-23** | Percentages | `20% of 1500`, `1500 + 18%`, `1500 − 10%`, `300 as % of 1500`. |
| **SP-24** | Developer units | Data sizes (`b`, `B`, `KB/KiB` … `TB/TiB`) and time (`ms s min h d wk`), so `3.5 GB in MiB` works. Bases: `0x1F`, `0b1010`, `0o17`, and `255 in hex/bin/oct`. Length, mass and temperature as a smaller second set. **No currency** (P1). |
| **SP-25** | Comments and labels | Text after `//` or `#` is ignored. A leading label is allowed (`Rent: 1200 × 12`). Lines that are plain prose show no result and no error. |
| **SP-26** | Errors | A line that looks like maths but fails (unknown variable, division by zero, unit mismatch) shows a subtle **!** with the reason on hover. |
| **SP-27** | Engine location | `DevTools.Core/Math/ScratchMath` is pure and has no packages. It is unit-tested like the other engines (≥ 120 cases, including precedence, units and every error path). |

### 4.4 Persistence and history (the "saved in app path" requirement)

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| **SP-30** | Location | `‹JsonStore.RootPath›\Scratchpad\`. The packaged path is `%LocalAppData%\Packages\‹family›\LocalState\Scratchpad`. Shown in Settings → Stored data with an **Open folder** button (existing `OpenDataFolder`). |
| **SP-31** | Readable files | Each note is a **plain UTF-8 file with CRLF line endings**, `notes\‹id›.txt`, that any editor can open, unlike Notepad's opaque cache. Metadata (title, pin, colour, language, created, modified, caret, archived, deleted) is held in `index.json`. If `index.json` is lost, it is rebuilt from the note files. |
| **SP-32** | Autosave | Saves 750 ms after typing stops, and also on note switch, tool switch, window deactivate and app suspend or close. Writes use the existing temp-then-move pattern, so a crash never leaves half a file. No save prompt, ever. |
| **SP-33** | Version history | A snapshot is written to `history\‹id›\‹yyyyMMdd-HHmmss›.md` when content has changed and either 60 s of idle has passed or 5 min since the last snapshot, and always before a destructive action (clear, paste-replace-all, format, import over). |
| **SP-34** | History view | The **History** button shows that note's snapshots (time, size, +/− lines). Selecting one previews it read-only. **Restore** replaces the note's content, first taking a snapshot of the current content. **Compare** opens the snapshot and the current text in **Text Compare** through `IToolHandoffService`. |
| **SP-35** | History retention | Keep snapshots for the **same retention period as Trash (Settings, default 20 days, max 60)**, and at most the newest **100 per note** (decision 3). Pruning runs on startup in the background. The whole `Scratchpad` folder is capped at **500 MB**; over the cap, the oldest snapshots go first and notes are never deleted. |
| **SP-36** | Session behaviour setting | Settings → Scratchpad → *When the tool opens*: **Continue where I left off** (default) / **Start with a new note**. Same idea as Notepad's setting, but nothing is ever discarded. |
| **SP-37** | Clear data | Settings → Stored data → **Clear scratchpad history** (snapshots only) and **Delete all notes** (confirmation required, then moved to Trash). "Clear saved tool state" does **not** touch notes; they are user documents, not tool state. |
| **SP-38** | Concurrency | Only one app instance writes the folder (single-instance app). *Not implemented:* reloading a note changed by another editor while open. The app's copy wins on its next save. |

### 4.5 Hand-off to other tools

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| **SP-40** | Send to tool | **Send to…** (menu and `Ctrl+Shift+K`) sends the selection, or the current block if nothing is selected, to any text-input tool: JSON Formatter, SQL, XML, JSON Diff (as left or right), Text Compare, Regex Validator, Markdown, HTML Viewer, Base64, URL, HTML encode, JSON to C#, JSON to Table, Character Counter. The list is ordered by the block's detected language. |
| **SP-41** | Send to Scratchpad | Every text tool's output pane gets **Send to Scratchpad**. It either appends to the active note as a new block in the matching language, or creates a note, with a timestamp header. |
| **SP-42** | Command palette | Scratchpad is in the palette as a tool. *Not implemented:* palette entries for individual notes. |

### 4.6 Quick capture — dropped (decision 4)

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| **SP-50** | Protocol | `forgekitrk://scratchpad` opens the tool, like every tool. Nothing else. |
| **SP-51** | Global hotkey | **Dropped.** Registering a system-wide hotkey is a machine-level change. |
| **SP-52** | Jump List | **Dropped**, for the same reason. |

### 4.7 Options band

New · Language (current block) · Format · Send to… · History · Pin · Colour · Note list toggle. The
same in-page band as every other tool. Nothing goes in the title bar.

### 4.8 Privacy and safety

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| **SP-60** | Secret hint | When a note contains something matching common secret shapes (private key, JWT, AWS key, GitHub or Slack token, `Bearer …`, `password=`, `api_key:`), a warning bar says it seems to contain one and that notes are stored unencrypted at ‹path›. Closing the bar dismisses it for that note. Detection is local and runs 1 s after typing stops. *Not implemented:* masking secrets in history. |
| **SP-61** | No telemetry | The same promise as every other tool. The privacy notes in Settings gain a line about where notes are stored. |
| **SP-62** | Uninstall | Notes live in package LocalState, so **uninstalling the app deletes them**. The first-run tip and Settings say so and point to *Export all*. |

---

## 5. Non-functional requirements

| ID | Requirement | Target |
| --- | --- | --- |
| NFR-SP1 | Tool open to caret ready | ≤ 200 ms with 500 notes |
| NFR-SP2 | Keystroke latency | Unchanged from the other editors (no work on the UI thread per keystroke beyond the editor's own) |
| NFR-SP3 | Durability | ≤ 1 s of typing lost on process kill; zero corrupt files across 1,000 forced kills in the soak test |
| NFR-SP4 | Scale | 5,000 notes, 10 MB each, 100k snapshots: list, search and pruning remain responsive (search ≤ 1 s) |
| NFR-SP5 | Accessibility | Note list, block chips, math results and history are all keyboard-reachable and named. Each math result is announced with its line ("Line 4, result 18,000"). |
| NFR-SP6 | Architecture | Math, block parsing, title derivation, secret detection and retention policy live in `DevTools.Core` (no packages, no System.Net, so the verify script still passes). File I/O and UI live in `DevTools.App/Services/ScratchpadStore`. |

---

## 6. Edge cases

1. Typing during a save: the save takes a snapshot of the text, so the next save carries the newer text and nothing is overwritten with old content.
2. Disk full or folder read-only: a banner says "Can't save — ‹reason›". The text stays in memory, the app retries every 10 s and blocks closing the window only in this case, offering Export.
3. Note file deleted outside the app while open: the note stays in the editor and is re-saved, with a notice.
4. Malformed or missing `index.json`: rebuilt from `notes\*.md`, with titles derived again.
5. Non-UTF-8 import: detect BOM, otherwise try UTF-8 and fall back to Windows-1252 with a notice.
6. Paste of 50 MB: refused above 10 MB with the size shown, and an offer to open it as a file in the editor read-only.
7. A math line in a Plain block is just text; math only runs in Math blocks.
8. Clock change or DST: snapshot names use UTC and are shown in local time.
9. A note made entirely of whitespace counts as empty: it is not snapshotted and is removed on switch-away if it was never typed in.
10. Two notes with the same derived title are allowed; the list shows time to tell them apart.

---

## 7. Out of scope

Cloud or cross-device sync · encryption at rest (use Windows BitLocker) · rich text and images ·
collaboration · AI features · clipboard history · live currency rates · scripting or "actions"
beyond Send to tool · Vim or Emacs key bindings.

---

## 8. Implementation status

| Area | Status | Where |
| --- | --- | --- |
| Notes, list, search, pin, colour, archive, Trash with Undo, duplicate (SP-01…09) | Done | `ViewModels/Tools/ScratchpadViewModel.cs`, `Views/Tools/ScratchpadPage.xaml` |
| Editor, one language per note, format, language hint, timestamp (SP-10…17) | Done | same, plus `CodeEditor.InsertAtCaret` / `CaretIndex` |
| Math (SP-20…27) | Done — 120+ unit tests | `DevTools.Core/Scratch/ScratchMath.cs` |
| Autosave, history, restore, compare, retention, size cap, startup choice, clear data (SP-30…37) | Done | `Services/ScratchpadStore.cs`, Settings → Scratchpad and Stored data |
| Send to a tool / Send to Scratchpad from every result pane (SP-40, 41) | Done | `CodeEditor` "Send to Scratchpad" button on read-only panes |
| Palette entries for notes (SP-42), external-change reload (SP-38), masking secrets in history (SP-60) | Not done | — |
| Quick capture: hotkey, Jump List (SP-50…52) | Dropped (decision 4) | — |

Keyboard: `Ctrl+N` new note · `Ctrl+Shift+F` search notes · `Ctrl+Shift+K` send to a tool ·
`Shift+Alt+F` format · `Ctrl+Shift+;` insert date and time · `Ctrl+S` export the note ·
`Ctrl+L` clear (the previous text goes to History) · `Ctrl+Enter` new note.

---

## 9. Decisions (2026-10-07)

1. **One language per note.** No blocks.
2. **Trash retention is a setting:** Settings → Scratchpad → *Keep Trash and history for*, default
   **20** days, maximum **60** (minimum 1).
3. **History:** at most 100 versions per note and 500 MB in all, and versions expire after the same
   number of days as Trash.
4. **No global hotkey**, and nothing else that changes the machine.
5. **Notes live in the package's LocalState** (`%LocalAppData%\Packages\‹family›\LocalState\Scratchpad`),
   so uninstalling removes them. Settings and the privacy statement say so and point to Export all.

## Sources

- Heynote — <https://github.com/heyman/heynote/>, <https://linuxlinks.com/heynote-dedicated-scratchpad-developers>
- Windows Notepad session state — <https://www.elevenforum.com/t/turn-on-or-off-automatically-save-session-state-for-notepad-in-windows-11.17595/>, <https://www.ghacks.net/?p=201520>
- Soulver — <https://documentation.soulver.app/llms-full.txt>, <https://makerstack.co/reviews/soulver-review/>; Numi — <https://www.popsci.com/diy/free-calculator-apps/>
- Antinote — <https://antinote.io/features>
- Tot and Drafts — <https://www.macworld.com/article/3531388/tot-pocket-review.html>, <https://forums.getdrafts.com/t/drafts-5-vs-tot-pocket/6915>
- Itsypad, DevPad, Quick Scratchpad — <https://alternativeto.net/software/hacker-pad>, <https://apps.puter.com/app/devpad>
