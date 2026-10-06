# DevTools — Product Requirements

**Version:** 1.0 · **Status:** Baselined · **Date:** 2026-09-17

---

## 1. Product statement

**DevTools** is a Windows 11 workbench for **API and payload work**, delivered as a single
WinUI 3 packaged (MSIX) desktop application. Six tools cover the loop a developer actually
runs: compose a request, send it, profile it, format the response, model it in C#, and diff
it against what it used to be.

### 1.1 Principles

| P | Principle | Consequence |
| --- | --- | --- |
| P1 | **Confined network** | Only `DevTools.Http` may open a socket. `DevTools.Core` has no network reference at all. No telemetry, no analytics, no update pings — ever. |
| P2 | **Never silently wrong** | A tool that cannot handle part of its input says so. Unsupported SVG features, ambiguous JSON types and unreliable timings are reported, never guessed. |
| P3 | **Instant** | Cold start under 2 s. No transform on the UI thread. |
| P4 | **Never lose work** | Input, options, collections and environments survive navigation and restart. |
| P5 | **Explain failures** | Precise, positioned, human-readable messages — never a stack trace, never a silent empty box. |
| P6 | **Accessible by default** | Full keyboard operation, screen-reader names, High Contrast, no colour-only status. |
| P7 | **Secrets stay secret** | Credentials go to the OS credential store, never into a document on disk. |
| P8 | **No machine changes** | The app changes nothing outside its own storage: no system settings, no proxy configuration, no certificates. (The API Profiler, the one part that did, was removed.) |

### 1.2 Out of scope (v1)

Cloud sync · plugin host · multi-window tear-off · auto-update · scripting engine in API
Builder (pre-request/test scripts, collection runner) · SVG text/filters/masks/animation ·
GraphQL and gRPC · localisation beyond an en-US resource pipeline ready for more.

---

## 2. Shell requirements

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| **FR-S01** | Navigation shell | `NavigationView` in Left mode listing the six tools flat (no category groups — six items do not need them). Opens **compact**, icons only; the pane toggle lives in the title bar and the chosen state is remembered. |
| **FR-S02** | Home dashboard | Landing page with a card per tool, with the recently opened and pinned ones above them. |
| **FR-S03** | Global search | Ranked search over tool name, description and keywords; Enter opens the top hit. Reached through the command palette (FR-S04), which searches the same index over the whole window. *The title-bar search box is withdrawn:* it was 320 px of permanent chrome doing a job `Ctrl+K` already did, and it held `Ctrl+F` hostage — that shortcut now belongs to the pane with the caret (FR-T12). `ToolCatalog.Search` is unchanged. |
| **FR-S04** | Command palette | `Ctrl+K` overlay over the same index plus app commands. `Esc` closes. |
| **FR-S05** | Favorites | Pin/unpin from the title bar or the palette. Persisted. |
| **FR-S06** | Recents | *Restored.* `RecentToolsService` records each tool as it is opened; the four most recent are shown as a Recent row on the Home dashboard, and an empty command palette lists recents first, newest first. Cleared from Settings → Stored data. |
| **FR-S07** | Theme | Light / Dark / System, applied live to every surface including caption buttons. |
| **FR-S08** | Backdrop | Mica / Mica Alt / Acrylic / None, applied live, degrading to a solid brush when unsupported. |
| **FR-S09** | Custom title bar | `ExtendsContentIntoTitleBar` with correct drag regions after resize and DPI change. Carries the pane toggle, `DevTools ▸ <tool>`, the active tool's options row (FR-T01), and the favourite, reset, palette and theme commands. Those four are 46 × 32 and the system caption buttons are set to `Tall`, so every button in the row shares one centre line. The tool's one-line description is the tooltip on its name rather than text in the bar — with the options row up there, there is not room for both. |
| **FR-S10** | Window persistence | Size, position and maximised state restored; an off-screen window is clamped back onto a visible monitor. |
| **FR-S11** | Per-tool state | Input and options saved (debounced) and restored across navigation and restart. Individually and globally clearable. |
| **FR-S12** | Settings page | Theme, backdrop, editor font size, word wrap, line numbers, syntax colouring toggle, HTTP defaults (timeout, redirects, proxy), clear-state, clear-history, and an About section with the **privacy statement** (§P1) and the on-disk data location. |
| **FR-S13** | Keyboard shortcuts | `Ctrl+K` palette · `Ctrl+F` search · `Ctrl+,` settings · `Ctrl+D` favourite · `Ctrl+Enter` run/send · `Ctrl+L` clear · `Alt+←/→` back/forward · `F1` about · `Esc` dismiss. |
| **FR-S14** | Navigation history | Back/forward via `Alt+←/→` and mouse buttons 4 and 5. No back button: it was never used and the title bar has better things to carry. |
| **FR-S15** | Accessibility | `AutomationProperties.Name` on every interactive element; logical tab order; visible focus; High Contrast dictionary; no colour-only signalling. |
| **FR-S16** | Localisation readiness | All user-facing strings resolve through `Strings/en-US/Resources.resw`. |
| **FR-S17** | MSIX packaging | Single-project MSIX, full logo asset set, `runFullTrust`, `devtools:` protocol activation. |
| **FR-S18** | Cross-tool hand-off | `IToolHandoffService` carries a typed payload between tools (see FR-A09). No tool references another tool's view model. |

## 3. Shared tool surface

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| **FR-T01** | Tool chrome | The tool's name, favourite and reset live in the 48 px title bar, not in a page header. Each page declares **one** options row — settings ordered by how often they are touched, rarer ones behind a named overflow button carrying a count — and the shell shows it **in the title bar** when there is room beside the name and the commands, or in a band directly above the page when there is not. The row carries no text labels: the controls say what they are and each has a tooltip, which is what makes it narrow enough to fit. Nothing wraps or clips at 1088 effective pixels. |
| **FR-T02** | Input affordances | Paste, Open file, Clear, live character and line counter on every text input. |
| **FR-T03** | Output affordances | Copy (with transient confirmation) and Save as. Output read-only but selectable. |
| **FR-T04** | Live transform | Runs automatically on input or option change, debounced 250 ms, cancelling any in-flight run. Tools whose work is expensive or outbound (API Builder) expose an explicit **Run/Send** button instead. |
| **FR-T05** | Error surface | `InfoBar` with severity, message and — where the engine supplies it — line/column/offset. The previous good output is **not** wiped mid-typing. |
| **FR-T06** | Empty state | Empty input shows a neutral placeholder, never an error. |
| **FR-T07** | Large input guard | Above 5 MB the UI prompts; processing stays responsive and cancellable. |
| **FR-T08** | Layout | Two-pane split reflowing to stacked below 900 px, with a draggable, remembered, keyboard-operable splitter. |
| **FR-T09** | Editor options | Word wrap, line numbers, font size and syntax colouring from Settings apply everywhere. |
| **FR-T10** | Drag and drop | Dropping a file on an input loads its text, or its bytes for binary-capable tools. |
| **FR-T11** | Syntax colouring | Read-only output panes and the diff view colourise JSON, XML and XAML. Editable inputs stay plain. Toggleable. |
| **FR-T12** | Find in pane | `Ctrl+F` with the caret in a pane opens a find strip on that pane: live match count, next/previous by button or `Enter`/`Shift+Enter`, match case, `Esc` to close. Every match is marked, not just the current one. Each pane searches itself, so input and output can be searched independently. |

## 4. Tool requirements

### 4.1 JSON Formatter — `json-formatter`

| ID | Requirement |
| --- | --- |
| **FR-J01** | Pretty, minify and validate-only modes. |
| **FR-J02** | Indent 2 spaces, 4 spaces or tab. |
| **FR-J03** | Recursive key sorting, ordinal. |
| **FR-J04** | Tolerant input options: allow trailing commas, allow `//` and `/* */` comments. |
| **FR-J05** | Number fidelity: `1.0`, `1e10`, `-0` and 30-digit integers round-trip byte-for-byte. |
| **FR-J06** | JSONPath query: `$`, `.`, `..`, `[n]`, `[a:b:c]`, `[*]`, `[?(@.x>1)]`. Result rendered as a JSON array with a match count. |
| **FR-J07** | Document stats: objects, arrays, keys, max depth, byte size. |
| **FR-J08** | Errors carry line, column and byte offset. |
| **FR-J09** | Duplicate keys reported as a warning, never silently merged. |
| **FR-J10** | Escape-non-ASCII option for output. |
| **FR-J11** | Result as a foldable tree: every object and array expandable by key, with a child count, the leaf values in JSON notation, expand-all and collapse-all, and the selected row's JSONPath shown and copyable. Branches fill on first expand, so a large document costs a parse and nothing more. |

### 4.2 JSON Diff Checker — `json-diff`

| ID | Requirement |
| --- | --- |
| **FR-J20** | Semantic diff: key order never counts as a difference. |
| **FR-J21** | Array strategies: Index, Key (by chosen id field), Best-match LCS (default). |
| **FR-J22** | Options: ignore array order · numeric tolerance · ignore case in values · ignore case in keys · null equals missing · ignore paths (glob list). |
| **FR-J23** | Side-by-side tree view with `+` / `−` / `~` marker glyphs **in addition to** colour. |
| **FR-J24** | Unified text view, and a textual line-diff fallback mode. |
| **FR-J25** | Counts of added, removed, changed and unchanged nodes. |
| **FR-J26** | **RFC 6902 JSON Patch** output for the computed difference. |
| **FR-J27** | Node cap of 50,000 with a clear message rather than a hang. |
| **FR-J28** | Side-by-side view, and the default: both documents pretty-printed against each other, a row at a time, each column carrying that document's own line numbers. Differences are coloured by kind — **missing property**, **incorrect type**, **unequal value** — counted per kind, and each kind can be hidden. A navigator steps through whatever is showing (`‹ 3 of 77 ›`), scrolling to each in turn and saying in a sentence what it is. A member on one side only leaves the other side of its row blank, and a blank does not advance that column's line numbers. |

### 4.3 JSON to C# Generator — `json-to-csharp`

| ID | Requirement |
| --- | --- |
| **FR-J40** | Infer a type graph by **merging every element** of an array, not just the first. |
| **FR-J41** | Widening `int → long → decimal → double`; conflicting types become `object` **with a warning naming the path**. |
| **FR-J42** | A member absent from some elements becomes nullable. |
| **FR-J43** | Detect `DateTimeOffset`, `Guid` and `Uri` from string shape (toggleable). |
| **FR-J44** | Options: record / class / readonly record struct · `get;set;` / `init` / `required` · STJ / Newtonsoft / no attributes · `List<T>` / `T[]` / `IReadOnlyList<T>` · nullable annotations · file-scoped / block / no namespace · nested / flat types. |
| **FR-J45** | PascalCase naming, acronym-aware; C# keywords escaped; leading digits prefixed; illegal characters stripped; type/member collisions renamed. |
| **FR-J46** | Structurally identical objects emit **one** type, not duplicates. |
| **FR-J47** | Generated code compiles under Roslyn and round-trips the source sample (verified by test). |

### 4.4 SVG to XAML Converter — `svg-to-xaml`

| ID | Requirement |
| --- | --- |
| **FR-V01** | Shapes: `path`, `rect` (incl. `rx`/`ry`), `circle`, `ellipse`, `line`, `polyline`, `polygon`. |
| **FR-V02** | Structure: `g`, `use`, `defs`, `symbol`, nested `svg`, `viewBox`, `width`/`height`, `preserveAspectRatio`. |
| **FR-V03** | Full SVG path grammar including implicit command repeats, relative forms and compressed arc flags. |
| **FR-V04** | Transforms: `matrix`, `translate`, `scale`, `rotate` (incl. 3-argument), `skewX`, `skewY`, nested and combined. |
| **FR-V05** | Paint: fill, stroke, widths, caps, joins, miterlimit, dasharray, dashoffset, fill-rule, all opacity variants; `#rgb`, `#rrggbb`, `rgb()`, `rgba()`, `hsl()`, named colours, `currentColor`. |
| **FR-V06** | Gradients: `linearGradient`, `radialGradient`, stops, `gradientUnits`, `xlink:href` inheritance. |
| **FR-V07** | `clipPath`. |
| **FR-V08** | Output flavours: **WPF** (`Canvas` · `DrawingImage` · merged `Path`) and **WinUI 3** (`Canvas` · merged `Path` · `PathIcon` only — WinUI has no `DrawingImage`). |
| **FR-V09** | Unsupported features (`text`, filters, masks, patterns, animation, raster, external refs, CSS blocks) produce a **named warning**, never a silent drop. |
| **FR-V10** | **Two** live previews side by side on a transparency checkerboard: the source SVG through the platform renderer (`SvgImageSource`) and the emitted XAML through `XamlReader.Load`. A load failure is surfaced in place, and the options row carries a standing "Output loads in XamlReader" chip. Deliberately two independent renderers: a source preview drawn by this converter would agree with the output by construction and could never show a difference. |
| **FR-V11** | A conversion report beside the previews: one row per feature with what happened and **why** — what converted, what was dropped and what the target dialect cannot express — each with the action that would fix it. Kind is carried by glyph as well as colour. |
| **FR-V12** | Optional geometry simplification: merge all paths into one `Path` with a single `Data` string. |

### 4.5 API Profiler — *removed*

The API Profiler (FR-A01…FR-A11) was removed from the product, together with its capture proxy,
certificate handling and in-process agent. Requirement ids FR-A01…FR-A11 are retired and are not
reused.

### 4.6 API Builder — `api-builder`

| ID | Requirement |
| --- | --- |
| **FR-A20** | Collections with folders; requests stored under `LocalFolder\workspace\`, JSON, atomic writes, **schema version field**. The rail is collapsible, filterable by name, and shows each request's verb as a coloured badge. |
| **FR-A21** | Verbs GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS and custom. |
| **FR-A22** | URL bar and query-parameter table kept in sync in both directions. |
| **FR-A23** | Header and parameter tables with per-row enable/disable, a description column, and a delete button on every row. Counts of the rows actually being sent appear on the tab that holds them. |
| **FR-A24** | Bodies: JSON (validated and pretty-printed by the FR-J01 engine) · form-urlencoded · multipart with file parts · raw · binary file · none. |
| **FR-A25** | Auth: None, Basic, Bearer, API key (header or query), OAuth 2.0 client credentials. |
| **FR-A26** | Environments and `{{variable}}` substitution with precedence **environment → collection → global**, plus a resolved-value preview before send. |
| **FR-A27** | Response viewer shaped like the request above it: a strip of status, time and size, then a left nav of Body · Headers · Cookies · Timings · History. The body has Pretty / Raw / Preview; Preview renders images and shows everything else as text, because rendering an arbitrary response in a web view would run its script inside the app. Binary bodies fall back to a hex dump. |
| **FR-A28** | Per-request history of the last N responses, replayable. |
| **FR-A29** | Import/export: cURL both directions · OpenAPI 3 import · Postman collection v2.1 import. |
| **FR-A30** | **Secrets** held in the OS credential store; documents store a reference. Export defaults to excluding secrets and warns when they are included. |
| **FR-A31** | Cross-tool hand-off: Format response · Generate C# model · Compare two responses · Profile this request (FR-S18). |

---

## 5. Non-functional requirements

| ID | Requirement | Target |
| --- | --- | --- |
| NFR-01 | Cold start | Under 2 s |
| NFR-02 | Navigation | Under 100 ms between tools |
| NFR-03 | UI responsiveness | Nothing blocks the UI thread longer than 50 ms |
| NFR-04 | Memory | Under 300 MB steady state with all six tools visited |
| NFR-05 | **Network confinement** | `grep` over `src/DevTools.Core` for network types returns no hits; all socket work lives in `DevTools.Http/Execution/HttpExecutor.cs` |
| NFR-06 | **No phoning home** | `grep` over `src` for telemetry/analytics types returns no hits |
| NFR-07 | Build | `build.ps1 -Task all` succeeds with 0 warnings, 0 errors |
| NFR-08 | Strictness | `TreatWarningsAsErrors=true`, nullable enabled across every project |
| NFR-09 | Test coverage | Every engine has unit tests covering its documented failure modes |
| NFR-10 | Minimum OS / architectures | Windows 10 build 19041; x64 and ARM64 |

---

## 6. Global edge cases

Each is handled and tested.

1. Empty input produces a placeholder, not an error.
2. Whitespace-only input is treated as empty.
3. Input over 5 MB prompts, then processes cancellably in the background.
4. Invalid UTF-8 and lone surrogates produce replacement characters and a warning, never a crash.
5. A BOM is stripped before parsing and preserved on save when it was present.
6. Mixed CRLF / LF / CR are normalised for processing; the original is preserved on round trip.
7. Non-ASCII, RTL, emoji and astral characters are counted by text element, not UTF-16 unit.
8. Null bytes and control characters are handled, not truncated.
9. An empty or non-text clipboard makes Paste a no-op.
10. A cancelled file dialog is a no-op.
11. A locked or unreadable file surfaces the OS reason.
12. Rapid typing is coalesced; a stale result never overwrites a newer one.
13. Navigating away mid-transform cancels cleanly with no `TaskCanceledException` leak.
14. A theme change re-themes every surface, including syntax colouring and the SVG preview.
15. A DPI change or monitor move keeps layout and drag regions correct.
16. JSON nested deeper than 256 is refused rather than overflowing the stack.
17. Numbers beyond `double` keep full precision through the source-text path.
18. An SVG with a feature outside the supported subset converts what it can and names what it could not.
19. A request to an unresolvable host, a refused connection, a TLS failure and a timeout each produce a distinct, actionable message.
20. A response with no `Content-Type`, or a body that is not valid UTF-8, renders as a hex dump rather than mojibake.

---

## Release 1.1 — fifteen new tools

Added in 1.1, each with its engine in `DevTools.Core` (pure, no network) and its tests in
`DevTools.Core.Tests`. Where a tool follows DevToys, the differences are deliberate and listed.

| Id | Tool | Engine | Notes against DevToys |
| --- | --- | --- | --- |
| FR-N01 | JSON to Table | `Json/JsonTable` | Exact numbers; arrays kept as JSON cells, not dropped; RFC 4180 CSV quoting; TSV and Markdown output |
| FR-N02 | SQL Formatter | `Sql/SqlFormatter` | Same ten dialects and layout (DevToys' test suite ported and passing); hand-written scanner instead of per-call regexes; keyword case Upper/Lower/Preserve; ASC/DESC recognised; contextual negative numbers |
| FR-N03 | XML Formatter | `Xml/XmlFormatter` | Streaming reader→writer instead of a DOM; declaration kept verbatim (DevToys rewrote every "utf-16" in the text); DTDs never processed |
| FR-N04 | Date & Unix Time | `Time/DateTimeConverter` | One input for numbers or dates; seconds to nanoseconds with size detection; decimal arithmetic; DST gaps reported |
| FR-N05 | Base64 Text | `Codecs/Base64Codec` | Seven encodings, URL-safe, MIME wrapping; forgiving decode; binary refused as text |
| FR-N06 | Base64 Image | `Codecs/Base64Image` | Format from the bytes, not the label; open, paste, drop; no ImageSharp |
| FR-N07 | URL Encoder | `Codecs/UrlCodec` | Component, whole-URL and form modes |
| FR-N08 | HTML Encoder | `Codecs/HtmlCodec` | Optional numeric references for non-ASCII |
| FR-N09 | UUID Generator | `Generators/UuidGenerator` | v7 monotonic within a batch; v1 node random with the multicast bit; four formats; inspector |
| FR-N10 | QR Code Generator | App `Services/QrCodeRenderer` (ZXing.Net encoder) | Drawn from the module matrix; PNG up to 4096 px and SVG; recovery level, quiet zone and colours |
| FR-N11 | Text Compare | `Text/TextDiff` (existing) | Side by side or inline, word-level highlights, unified diff export |
| FR-N12 | Character Counter | `Text/TextAnalysis` | Grapheme counts, limits for common targets |
| FR-N13 | Regex Validator | `Text/RegexTester` | Not a DevToys tool. Matches with line, column and groups; replacement preview; all .NET flags and ECMAScript; 2 s timeout against catastrophic backtracking |
| FR-N14 | Markdown Preview | Markdig in the app | WebView2 with script off, CSP, and every resource request refused |
| FR-N15 | HTML Viewer | App `Controls/SafeWebPreview` | Not a DevToys tool. Live preview of edited HTML; same no-network WebView2 as Markdown Preview; page scripts optional |
| FR-N16 | Diff export | `Text/DiffHtmlExporter` | JSON Diff and Text Compare save the side-by-side view as a self-contained HTML page |

Navigation groups in 1.1: Formatters (JSON, SQL, XML), Validators (JSON Diff, Text Compare, Regex), Converters (JSON to C#, JSON to Table, Date), Encoders & Decoders, Generators, Text Tools (Character Counter, Markdown Preview, HTML Viewer), Media Tools, API Tools. Tool options sit in a band at the top of the page; the title bar holds only the tool name and app commands, including a keyboard-shortcuts list.

### Performance work in the same release

| Area | Before | After (2 MB JSON) |
| --- | --- | --- |
| JSON parse (every JSON tool, Smart Detect) | 74,488 ms — line and column recounted from the start for every node | 80–100 ms |
| JSON Diff | 942 ms and an unbounded LCS table in the patch builder (12k × 12k ints) | ~340 ms; common prefix/suffix trimmed, hashes cached, table bounded |
| Editor keystroke cost | Whole text split into lines twice per keystroke | Allocation-free line count; gutter numbers reused |
