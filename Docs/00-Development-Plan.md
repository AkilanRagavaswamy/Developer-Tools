# DevTools — Phase-wise Development Plan

**Version:** 1.0 · **Date:** 2026-09-17 · **Workspace:** `D:\Projects\DevTools`
**Owner:** akilan.rk@zohocorp.com

> **Post-release changes (October 2026).** The product shipped as **ForgeKitRk – Developer
> Toolkit**. The **API Profiler** described in Phases 7 and 12 — its capture proxy, certificate
> handling and in-process agent — was **removed**; the sections below are kept as the historical
> plan. **Recents (FR-S06)** were restored: a Recent row on Home and recents-first in the command
> palette.

---

## 1. What is being built

**DevTools** is a Windows desktop workbench for **API and payload work**, delivered as a
WinUI 3 packaged (MSIX) application. It is a new, independent product — not a DevKit
release, not a DevKit plug-in.

| # | Tool | Tool id | What it does |
| --- | --- | --- | --- |
| 1 | **JSON Formatter** | `json-formatter` | Pretty / minify / validate, JSONPath query, sort keys, fidelity-preserving numbers |
| 2 | **JSON Diff Checker** | `json-diff` | Semantic (key-order-insensitive) diff of two documents, with RFC 6902 JSON Patch output |
| 3 | **JSON to C# Generator** | `json-to-csharp` | Compilable C# models from a JSON sample: records or classes, STJ or Newtonsoft attributes |
| 4 | **SVG to XAML Converter** | `svg-to-xaml` | SVG shapes, paths, transforms and gradients to WPF or WinUI XAML, with a live preview |
| 5 | **API Profiler** | `api-profiler` | Watches the HTTP calls an application makes: a capture proxy, process attribution, whole request and response bodies |
| 6 | **API Builder** | `api-builder` | Postman-lite: collections, environments, variables, auth, body editors, response viewer |

### 1.1 Why these six belong in one app

They are one workflow, not six utilities. You build a request, send it, profile it, format
the response, generate a C# model from it, and diff today's response against yesterday's.
The plan therefore treats **cross-tool hand-off as a first-class requirement** (§6.9), not a
nice-to-have — it is the reason API Builder is built last, with every other engine already
available to it.

### 1.2 How DevTools differs from DevKit

| | DevKit | DevTools |
| --- | --- | --- |
| Shape | 32 light, stateless, single-input utilities | 6 heavy, stateful, multi-input tools |
| Network | **Zero** — grep-verifiable | Two tools exist to make HTTP requests |
| Per-tool state | One text box + options in a JSON blob | Collections, environments, saved profiles, baselines |
| Editor needs | Plain text + line numbers | Syntax colouring, diff gutters, tree views, charts |

The first two rows are why this is a new solution rather than six more DevKit tools.

---

## 2. Decisions locked

| # | Decision | Rationale |
| --- | --- | --- |
| **D1** | .NET 10 (SDK 10.0.401), Windows App SDK **2.4.0**, `net10.0-windows10.0.26100.0`, min `10.0.19041.0`, single-project MSIX, x64 + ARM64 | Identical to DevKit's validated toolchain. Re-confirmed on this machine in Phase 0, so the stack is a known quantity rather than a risk. |
| **D2** | **Four projects**, with network confined to one (§3) | The privacy claim changes shape; confinement is what keeps it checkable. |
| **D3** | Shell **copied and adapted** from DevKit, not shared | Both products stay independently releasable. DevKit is complete and frozen; a shared library would couple their release cycles and force a DevKit rework. |
| **D4** | `ToolViewModelBase` **split in two** (§4.2) | DevKit's base assumes one text input → one text output with a live debounce. Four of the six DevTools tools do not fit that shape. |
| **D5** | One **shared JSON document model** behind the three JSON tools | Three tools parsing the same document three ways would give three verdicts on it. |
| **D6** | MVVM via `CommunityToolkit.Mvvm` 8.4.2 source generators; central package management; `TreatWarningsAsErrors=true`; nullable enabled | Same quality gates that kept DevKit at zero warnings. |
| **D7** | No telemetry, no analytics, no update pings, ever | The one DevKit promise that survives unchanged. |
| **D8** | Secrets never stored in plaintext (§6.9) | A credential written to a collection file in clear text is on disk forever. |

### Assumption A1 — stated explicitly

No requirement documents for DevTools were found on the machine, and the referenced chat
session (`claude.ai/share/da4719f3…`) could not be read — it redirects to a sign-in wall and
no Chrome browser is connected to this session. The scope above is therefore authored from
the six tool names given, plus the DevKit conventions in `D:\Projects\DevKit\Docs`. If the
intended scope of any tool differs, `01-Product-Requirements.md` is the only document that
needs to change; the architecture is scope-agnostic.

---

## 3. Solution layout

```
D:\Projects\DevTools\
├── DevTools.sln
├── Directory.Build.props           # shared MSBuild settings, strictness
├── Directory.Packages.props        # central package management
├── global.json                     # pins .NET 10 SDK
├── build\build.ps1                 # restore / build / test / package / run
├── Docs\
├── src\
│   ├── DevTools.Core\              # net10.0 — pure engines, ZERO network
│   ├── DevTools.Http\              # net10.0-windows — the ONLY network-capable assembly
│   └── DevTools.App\               # net10.0-windows10.0.26100.0 — WinUI 3, MSIX
└── tests\
    ├── DevTools.Core.Tests\        # net10.0 — xUnit v3
    └── DevTools.Http.Tests\        # net10.0-windows — xUnit v3, loopback server only
```

### 3.1 Dependency direction

```
DevTools.Core.Tests ──▶ DevTools.Core ◀── DevTools.Http ◀── DevTools.App
                                    ◀───────────────────────────┘
                         DevTools.Http.Tests ──▶ DevTools.Http
```

`DevTools.Core` references nothing from WinUI, nothing from `System.Net`.
`DevTools.Http` references `DevTools.Core` (it reuses the JSON engines to pretty-print
response bodies) and is the single place any byte leaves the machine.

### 3.2 The privacy boundary — the one decision that matters most

DevKit could claim *nothing leaves the machine* and prove it with one grep. DevTools cannot:
two of its tools exist to send HTTP requests. The claim therefore changes from **absent** to
**confined and auditable**:

| Claim | How it is enforced |
| --- | --- |
| `DevTools.Core` never touches the network | No `System.Net` usage; CI gate greps `src/DevTools.Core` for `HttpClient\|WebRequest\|Socket\|Dns\.\|TcpClient\|UdpClient` and fails on any hit |
| Every outbound byte goes through one file | All socket work lives in `DevTools.Http/Execution/HttpExecutor.cs`; no other file constructs a handler |
| Nothing is ever sent that the user did not compose | No telemetry, no analytics, no update check, no crash reporting. Gate greps the whole of `src/` for those too. |
| Secrets are not written to disk in clear text | `ICredentialStore` backed by `PasswordVault`; collection files store a reference, never a value |

This is stated as a product feature on the Settings page, the way DevKit stated its own.

---

## 4. Shared foundation

### 4.1 Ported from DevKit unchanged (namespace rename only)

Copying these is ~3,000 lines of already-debugged code and is the single largest schedule
saving in the plan.

| From DevKit | To DevTools | Notes |
| --- | --- | --- |
| `OperationResult.cs`, `Limits.cs` | `DevTools.Core` | `Limits` gains HTTP caps (§6.8) |
| `Text/TextUtil.cs`, `Text/EncodingCatalog.cs` | `DevTools.Core.Text` | Solves 8 of the global edge cases once |
| `TextTools/CaseConverter.cs` | `DevTools.Core.Text` | Acronym-aware — needed by the C# generator |
| `TextTools/TextDiff.cs` | `DevTools.Core.Text` | The textual fallback mode of JSON Diff |
| `Formatters/JsonFormatter.cs` | `DevTools.Core.Json` | Re-based onto the shared JSON model (§4.3) |
| Services: Clipboard, Dialog, FileDialog, JsonStore, Navigation, Settings, Theme, ToolState, ShellContext, Favorites | `DevTools.App.Services` | As-is |
| Controls: `CodeEditor` + `GutterBuilder`, `ToolShell`, `ToolSplitPanel` | `DevTools.App.Controls` | `CodeEditor` gains colouring (§4.4) |
| `Styles/Theme.xaml`, converters, `build.ps1`, the three MSBuild files | as-is | |

### 4.2 D4 — splitting `ToolViewModelBase`

DevKit's base class bakes in *one input string, one output string, 250 ms live debounce*.
That fits JSON Formatter exactly and fits nothing else here: JSON Diff has two inputs,
SVG→XAML has a file input and a rendered preview, API Profiler is a long-running cancellable
job with progress, and API Builder's "input" is an object graph with an explicit Send.

```
ToolViewModelBase              ← message banner, IsBusy, cancellation, generation guard,
  (abstract)                     state persistence, favorite, reset, ComputeAsync
    │
    ├── TextToolViewModelBase  ← Input/Output strings, 250 ms debounce, Paste/Open/Clear/
    │     (abstract)             Copy/Save, counters.  JSON Formatter, JSON→C#, SVG→XAML
    │
    ├── DualTextToolViewModelBase ← Left/Right inputs, both toolbars, swap.  JSON Diff
    │
    └── JobToolViewModelBase   ← explicit Start/Stop, progress %, iteration log, partial
          (abstract)             results.  API Profiler, API Builder
```

Everything above the split is DevKit's proven machinery; everything below is new. This
refactor lands in Phase 2 and is the load-bearing piece of the whole UI.

### 4.3 D5 — the shared JSON document model

`DevTools.Core.Json.JsonModel` is an immutable tree that preserves what
`System.Text.Json`'s own DOM discards:

| Preserved | Why it matters |
| --- | --- |
| **Number source text** | `1.0` must stay `1.0`, `1e10` must not become `10000000000`, a 30-digit integer must keep every digit. DevKit's formatter already does this; the differ and the generator need the same fidelity or they will disagree with the formatter. |
| **Key order** | The formatter round-trips it; the differ must be able to ignore it *by choice*, not by accident. |
| **Source position** (line, column, offset) per node | Every error message points at a character, and the diff view can scroll to a node. |
| **Duplicate keys** | Legal in JSON, and silently dropping one is how a differ reports a false "equal". |

All three JSON tools parse **once**, through this model. This is why they ship as one block
(Phases 3–5) rather than being spread across the schedule.

### 4.4 `CodeEditor` colouring

DevKit's editor is a `TextBox` plus a line-number gutter, with no colouring. DevTools needs
JSON colouring in five places and diff markers in one. The approach that avoids rewriting
the editor: keep the editable `TextBox` plain, and add a **read-only colourised presenter**
(`RichTextBlock` driven by a token list) used for every *output* pane and for the diff view.
Editing stays plain; everything the user only reads gets colour. Risk and fallback in §8.

---

## 5. The phases

Sizes are **S / M / L / XL** relative to each other. The indicative day ranges assume one
engineer; DevKit's own 32 tools were completed in a single agent-driven session, so these map
to phase boundaries far better than they map to a calendar.

| Phase | Name | Size | Days | Depends on |
| --- | --- | --- | --- | --- |
| 0 | Toolchain confirmation | XS | 0.5 | — |
| 1 | Baseline documents | S | 1 | 0 |
| 2 | Foundation: solution, contracts, shell port, VM split | L | 3–4 | 1 |
| 3 | JSON model + **JSON Formatter** | M | 2–3 | 2 |
| 4 | **JSON Diff Checker** | L | 3 | 3 |
| 5 | **JSON to C# Generator** | L | 3 | 3 |
| 6 | **SVG to XAML Converter** | XL | 4–5 | 2 |
| 7 | HTTP foundation + **API Profiler** | XL | 4–5 | 2 |
| 8 | **API Builder** | XL | 6–8 | 3, 7 |
| 9 | Integration, cross-tool hand-off, accessibility | M | 2–3 | 3–8 |
| 10 | Verification loop | M | 2 | 9 |
| 11 | Packaging, README, release | S | 1 | 10 |
| 12 | UI redesign + capture profiler | XL | 4–5 | 11 |

**Total: 36–46 engineer-days.** Phases 6 and 7 are independent of 3–5 and of each other,
so with more than one engineer the critical path is 2 → 3 → 4/5 → 8 → 9 → 10 → 11.

---

## 6. Phase detail

### Phase 0 — Toolchain confirmation · **COMPLETE**

Verified on this machine before any design work, as DevKit did:

| Check | Result |
| --- | --- |
| .NET SDK | **10.0.401** ✓ |
| `Microsoft.NETCore.App` runtime | 10.0.12 ✓ |
| Windows SDK | 10.0.26100.0 ✓ |
| `Microsoft.WindowsAppRuntime.2` | **2.4.0.0** installed ✓ |
| OS | Windows 11 Pro 10.0.26200 ✓ |

Identical to the environment DevKit was built and packaged on, so WinUI 3 compilation,
single-project MSIX and the CommunityToolkit controls under Windows App SDK 2.x are all
already-proven rather than assumed.

**Exit:** all five green. ✓

---

### Phase 1 — Baseline documents · **COMPLETE**

**Deliverables:** `Docs/00-Development-Plan.md` (this file), `01-Product-Requirements.md`
(FR ids everything traces to), `02-Architecture.md` (exact namespaces and type contracts),
`09-Traceability-Matrix.md` (every id mapped to its implementation and its test).

A separate `03-Feature-Specifications.md` was planned and deliberately not written: `Docs/01 §4`
already carries per-tool behaviour and acceptance criteria at that level of detail, and
`Docs/02` carries the type contracts. A third document restating both would have to be kept in
step with them, and a spec that drifts is worse than one that does not exist.

**Exit:** every tool has an FR id; every FR id has acceptance criteria. ✓

---

### Phase 2 — Foundation

**Goal:** a solution that builds, packages and runs an empty shell with six navigable, empty
tool pages. Nothing after this phase touches infrastructure.

**Deliverables**

1. `DevTools.sln`, five projects, `Directory.Build.props`, `Directory.Packages.props`,
   `global.json`, `build\build.ps1` — all ported from DevKit.
2. `DevTools.Core`: `OperationResult`, `Limits`, `Text/TextUtil`, `Text/EncodingCatalog`,
   `Text/CaseConverter`, `Text/TextDiff`.
3. `DevTools.App`: the ten services, the four controls, `Styles/Theme.xaml`, `ShellPage`,
   `HomePage`, `SettingsPage`, `ToolCatalog` with six descriptors, `devtools:` protocol.
4. **D4 — the view-model hierarchy** (§4.2): `ToolViewModelBase`,
   `TextToolViewModelBase`, `DualTextToolViewModelBase`, `JobToolViewModelBase`.
5. Six empty tool pages wired into navigation.
6. `Package.appxmanifest` with the full logo asset set.

**Exit:** `build.ps1 -Task all` is green with **0 warnings**; the MSIX installs; all six
pages open; theme, backdrop, window persistence and `Ctrl+K` all work.

---

### Phase 3 — JSON model + JSON Formatter

**Engine:** `DevTools.Core.Json` — `JsonModel`, `JsonReader`, `JsonWriter`, `JsonFormatter`,
`JsonPath`.

| Capability | Detail |
| --- | --- |
| Modes | Pretty · Minify · Validate-only |
| Indent | 2 / 4 spaces, tab |
| Options | Sort keys recursively · allow trailing commas · allow `//` and `/* */` comments · escape non-ASCII |
| Query | JSONPath — `$`, `.`, `..`, `[n]`, `[a:b:c]`, `[*]`, `[?(@.x > 1)]` |
| Stats | Objects, arrays, keys, max depth, size |
| Errors | Message with **line, column and byte offset** |

**Traps that are tested explicitly:** `1.0` stays `1.0`; `1e10` is not expanded; a 30-digit
integer keeps every digit; `-0` survives; duplicate keys are reported, not silently merged;
lone surrogates become U+FFFD with a warning; a BOM is stripped before parsing; nesting
deeper than 256 is refused rather than overflowing the stack.

**Exit:** engine tests green including every trap above; page live-formats with debounce,
reports position on failure, and keeps the last good output when input goes briefly invalid.

---

### Phase 4 — JSON Diff Checker

**Engine:** `DevTools.Core.Json.JsonDiffer`.

The hard part is arrays — it is where every JSON differ is judged. Three strategies, chosen
by the user:

| Strategy | Behaviour |
| --- | --- |
| **Index** | Compare element *i* with element *i*. Cheap, correct for ordered data. |
| **Key** | Match elements by a chosen id field (`id`, `name`, …). Correct for record sets that reorder. |
| **Best match (LCS)** | Longest-common-subsequence over element hashes, so an insertion shifts nothing after it. Default. |

| Option | Effect |
| --- | --- |
| Ignore array order | Treats arrays as multisets |
| Numeric tolerance | `1` equals `1.0` equals `1.00`; optional epsilon for floats |
| Ignore case | In string *values*, and separately in *keys* |
| Null equals missing | `{"a":null}` equals `{}` |
| Ignore paths | Glob list, e.g. `$..timestamp`, `$.meta.*` — essential for diffing API responses |

**Outputs:** a side-by-side tree with `+` / `−` / `~` **marker glyphs as well as colour**
(DevKit UX rule: never signal status by colour alone); a unified text view; a summary of
added / removed / changed / unchanged counts; and an **RFC 6902 JSON Patch** document — the
differentiator, and directly useful to anyone scripting against the same API.

**Exit:** tests cover all three array strategies, every option, nested combinations,
documents that differ only in key order (must report *equal* in semantic mode), and the
50,000-node cap. Textual mode falls back to the ported `TextDiff`.

---

### Phase 5 — JSON to C# Generator

**Engine:** `DevTools.Core.CodeGen.CSharpFromJson`.

**Inference:** merge the shapes of every element of an array rather than trusting the first;
widen `int → long → decimal → double`; a property present in some elements and absent in
others becomes nullable; conflicting types become `object` **with a warning naming the
path**, never silently; `[]` becomes `List<object>`; `null`-only becomes `object?`.
Strings are probed for `DateTimeOffset`, `Guid` and `Uri` shapes.

| Option | Values |
| --- | --- |
| Type kind | `record` · `class` · `readonly record struct` |
| Members | `{ get; set; }` · `{ get; init; }` · `required` |
| Attributes | `System.Text.Json` `[JsonPropertyName]` · Newtonsoft `[JsonProperty]` · none |
| Collections | `List<T>` · `T[]` · `IReadOnlyList<T>` |
| Nullable | Annotate reference types · leave un-annotated |
| Namespace | File-scoped · block · none |
| Layout | Nested types · flat sibling types |
| Naming | PascalCase via the ported acronym-aware `CaseConverter` |

**Correctness traps:** C# keywords escaped (`@class`); identifiers starting with a digit
prefixed; characters illegal in identifiers stripped; a property whose name collides with its
containing type renamed; two structurally identical objects emitted as **one** type, not two.

**The acceptance test is unusually strong and worth stating:** the test project compiles the
generated source **with Roslyn**, deserialises the original sample into it, re-serialises,
and asserts the result is semantically equal to the input via `JsonDiffer` from Phase 4.
"It generates code" is not the bar; "it generates code that round-trips the sample" is.

**Exit:** that round-trip test passes across a corpus of ~20 real-world payloads; every
option permutation that changes output has a test.

---

### Phase 6 — SVG to XAML Converter

The highest-risk pure engine, because "SVG support" is unbounded unless the subset is
written down. It is written down here.

**Engine:** `DevTools.Core.Vector` — `SvgParser`, `SvgPathGrammar`, `TransformParser`,
`StyleResolver`, `XamlEmitter`.

**In scope (v1)**

| Area | Supported |
| --- | --- |
| Shapes | `path`, `rect` (incl. `rx`/`ry`), `circle`, `ellipse`, `line`, `polyline`, `polygon` |
| Structure | `g`, `use`, `defs`, `symbol`, `svg` nesting, `viewBox`, `width`/`height`, `preserveAspectRatio` |
| Path data | Every command — `M m L l H h V v C c S s Q q T t A a Z z` — including implicit repeats, relative forms, and compressed arc flags (`a1 1 0 011 1`) |
| Transforms | `matrix`, `translate`, `scale`, `rotate` (incl. the 3-argument form), `skewX`, `skewY`, nested and combined |
| Paint | `fill`, `stroke`, `stroke-width`, `stroke-linecap`, `stroke-linejoin`, `stroke-miterlimit`, `stroke-dasharray`, `stroke-dashoffset`, `fill-rule`, `opacity`, `fill-opacity`, `stroke-opacity`, named colours, `#rgb`, `#rrggbb`, `rgb()`, `rgba()`, `hsl()`, `currentColor` |
| Gradients | `linearGradient`, `radialGradient`, `stop`, `offset`, `stop-color`, `stop-opacity`, `gradientUnits`, `xlink:href` inheritance |
| Clipping | `clipPath` |
| Styling | Presentation attributes and inline `style=""` |

**Out of scope in v1 — reported as a warning, never silently dropped**

`<text>` (needs font metrics to convert to geometry) · filters · masks · patterns ·
animation (`<animate>`, SMIL) · embedded raster images · external file references ·
`<style>` blocks with CSS selectors · `foreignObject`.

**Output flavours** — and one correctness detail that is easy to get wrong:

| Target | Emits |
| --- | --- |
| **WPF** | `Canvas` of shapes · `DrawingImage`/`DrawingGroup` resource · single merged `Path` |
| **WinUI 3 / UWP** | `Canvas` of shapes · single merged `Path` · `PathIcon` |

**WinUI has no `DrawingImage`, `DrawingGroup` or `DrawingBrush`.** Emitting them for a WinUI
target produces XAML that silently fails to load. The WinUI flavour is therefore restricted
to `Path`, `PathIcon` and `Canvas`, and the option is disabled rather than offered-and-broken.

**Arc conversion.** SVG's endpoint arc parameterisation (`rx ry x-rotation large-arc sweep
x y`) maps to XAML `ArcSegment` (`Size`, `RotationAngle`, `IsLargeArc`, `SweepDirection`),
but the degenerate cases must be handled per the SVG spec: zero radii collapse to a line,
radii too small are scaled up, negative radii are made absolute.

**The preview doubles as a self-test.** The page renders the emitted XAML with
`XamlReader.Load`. If the output will not load, the tool says so immediately — which means
the class of bug where a converter emits plausible-looking but invalid XAML cannot ship.

**Exit:** a golden-file corpus (≥ 25 SVGs, including Material and Feather icons and two
complex illustrations) converts to XAML that `XamlReader.Load` accepts; every path command
has a unit test; every unsupported feature raises its warning.

---

### Phase 7 — HTTP foundation + API Profiler

**`DevTools.Http` foundation** (shared with Phase 8): `RequestDefinition`, `HttpExecutor`,
`ResponseRecord`, `ICredentialStore`, TLS options, redirect and proxy policy, cancellation.

**Engine:** `DevTools.Http.Profiling.RequestProfiler`.

**Timing breakdown — and the honest version of how it is obtained.** `HttpClient` does not
hand you a DNS/TCP/TLS split. The breakdown is assembled from a `SocketsHttpHandler` whose
`ConnectCallback` resolves and connects by hand (giving exact DNS and TCP times) and whose
`PlaintextStreamFilter` wraps the TLS handshake, plus stopwatches around send, first byte and
body download.

> **Risk, and the fallback, stated up front.** If any segment proves unreliable in practice,
> the tool ships **DNS · TCP connect · TLS handshake · TTFB · download · total**, all of
> which are obtainable with certainty from the callbacks above. It does **not** ship a number
> it cannot stand behind. A profiler that reports a plausible-but-wrong breakdown is worse
> than one that reports fewer segments.

| Run control | Detail |
| --- | --- |
| Warmup | N requests excluded from statistics |
| Iterations | Capped (§6.8) |
| Concurrency | 1–N, capped |
| Think time | Fixed delay between iterations |
| Connection mode | **Pooled** (reuse, the realistic case) vs **fresh per iteration** (measures full handshake). This changes results by an order of magnitude, so it is an explicit option with a documented default of pooled. |

**Statistics:** min, max, mean, stddev, p50, p75, p90, p95, p99, requests/sec, error rate,
status-code histogram, response-size stats. **Comparison:** two configurations side by side,
or a saved baseline versus now, with a verdict.

**Charts:** latency-over-iterations and a distribution histogram, drawn by a small custom
`Canvas`-based control. No charting dependency is added for two chart types.

**This tool generates load, so it has guard rails** — hard caps on iterations and
concurrency, a confirmation dialog above a threshold, no unlimited mode, and "ignore TLS
certificate errors" available only as an explicit per-request opt-in with a visible warning,
never a global default.

**Exit:** profiling a local `HttpListener` produces timings that track injected delays;
percentile maths is unit-tested against known distributions; cancellation mid-run leaves
partial results intact; caps cannot be exceeded from the UI.

---

### Phase 8 — API Builder (Postman-lite)

**Model:** `Workspace` → `Collection` → `Folder` → `RequestDefinition`; `Environment` →
variables. Stored under `LocalFolder\workspace\` as JSON with a **schema version field from
day one**, written atomically.

| Area | Scope |
| --- | --- |
| Verbs | GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS, and custom |
| URL | Path + query-parameter table, kept in sync both ways |
| Headers | Table with enable/disable per row, plus common-header autocomplete |
| Body | JSON (validated and pretty-printed by the Phase 3 engine) · form-urlencoded · multipart with file parts · raw text · binary file · none |
| Auth | None · Basic · Bearer · API key (header or query) · OAuth 2.0 client credentials |
| Variables | `{{name}}` with precedence **environment → collection → global**, and a resolved-value preview before send |
| Response | Status, timing, size, headers, cookies; body pretty-printed by content type — JSON via the Phase 3 engine, XML, HTML, image preview, hex dump for binary |
| History | Last N responses per request, replayable |
| Import / export | cURL **both directions** · OpenAPI 3 → collection · Postman collection v2.1 → collection |

**Secrets (D8).** Auth values are held in `ICredentialStore` (Windows `PasswordVault`); the
collection file stores a *reference*, never the value. Export offers "include secrets"
defaulting to **off**, with a warning. Getting this wrong once means credentials sit in a
JSON file forever.

**Exit:** a collection survives restart; variables resolve with correct precedence; all five
auth schemes produce correct headers (unit-tested without a network); cURL round-trips; an
OpenAPI 3 document imports; no secret appears in any file on disk.

---

### Phase 9 — Integration and cross-tool hand-off

This is the phase that turns six tools into one product (§1.1).

| From | Action | To |
| --- | --- | --- |
| API Builder response | *Format* | JSON Formatter |
| API Builder response | *Generate C# model* | JSON to C# Generator |
| Two API Builder responses | *Compare* | JSON Diff Checker |
| API Builder request | *Profile this* | API Profiler |
| API Profiler result | *Open request* | API Builder |

Implemented as a `IToolHandoffService` carrying a typed payload through `NavigationService`,
so no tool references another tool's view model.

Also in this phase: accessibility sweep (`AutomationProperties.Name` on every interactive
element, High Contrast dictionary, no colour-only status, tab order), keyboard shortcut
table, Settings page including the privacy statement (§3.2), and the responsive reflow pass.

**Exit:** every hand-off works in both directions where listed; screen-reader pass on all six
pages; High Contrast legible throughout.

---

### Phase 10 — Verification loop

The DevKit protocol, which worked: implementation audited by a model that did not write it,
against `09-Traceability-Matrix.md`. Anything marked incomplete goes back; repeat until the
audit returns a clean pass on every functional, UI, edge-case and accessibility requirement.
Rounds recorded in `10-Verification-Log.md`.

Plus the gates that are mechanical:

```bash
# Core must never touch the network
grep -rniE "HttpClient|WebRequest|WebClient|Socket|Dns\.|TcpClient|UdpClient" src/DevTools.Core --include=*.cs

# Nothing anywhere phones home
grep -rniE "telemetry|analytics|AppCenter|ApplicationInsights" src --include=*.cs
```

Both must return **no hits**.

---

### Phase 11 — Packaging and release

`build.ps1 -Task all` green at 0 warnings · MSIX produced for x64 and ARM64 · installs and
launches · `devtools://tool/api-builder` activates · `README.md` written from what was built.

---

### Phase 12 — UI redesign and the capture profiler

Not in the original plan. It follows a design review of the shipped v1 that raised six points
about the shared chrome and three about individual tools, and one change that is not a UI
change at all: the profiler should listen to an application rather than measure a URL.

**Decisions taken in this phase**

| # | Decision | Rationale |
| --- | --- | --- |
| **D9** | The measure-a-URL profiler is **removed**, not kept as a second tab | Two modes would mean two answers to "what does this tool do". `RequestProfiler` and its tests were deleted with it rather than left as dead code. |
| **D10** | The root certificate and proxy port are installed **once** and recorded in `capture.json` | Reinstalling a trusted root every session is both a nuisance and a bad habit to teach. A thumbprint that no longer resolves means "no certificate", so removing it by hand works. |
| **D11** | Captured response bodies are retained **whole** | A truncated body is reliably the one you needed. The cost is memory, so the running total is shown in the capture bar. |
| **D12** | `DevTools.Http` retargets to `net10.0-windows` | The capture proxy reads the registry and the TCP table. Both are Win32, and the app is Windows-only; a shim package would have been ceremony around the same dependency. |
| **D13** | Preview never renders a response in a web view | Rendering an arbitrary API response in a browser control runs that response's script inside the app. Images render; everything else is text, and the UI says why. |
| **D14** | A packaged DevTools does **not** attempt the proxy change | An MSIX process has its registry writes redirected into a private hive — through `HKEY_CURRENT_USER` and `HKEY_USERS` alike, and `unvirtualizedResources` did not lift it. The write reports success, the read-back agrees, and the capture listens to silence. Detecting package identity and saying so is the only honest option; the proxy still captures whatever is pointed at it, and the UI gives the address. |

**What changed**

- **Shared chrome.** The 90 px page header is gone; the tool's name, description, favourite and
  reset moved into the 48 px title bar, reached through `IToolChrome` — the only coupling
  between the shell and a page. The back button went (`Alt+←` and mouse button 4 remain), the
  pane toggle came up into the title bar, the navigation pane opens compact and remembers its
  state, and the search box is a fixed 320 px that widens to 460 px only while focused.
- **One options row per tool**, ordered by how often each setting is touched, with the rare ones
  behind a named button carrying a count of how many are on. One spacing scale in
  `Styles/Theme.xaml` drives all six pages, and the editor placeholder now sits exactly where
  the first character will, in the same font.
- **SVG to XAML** gained a second preview — the source rendered by the platform, beside the
  output rendered by `XamlReader.Load`, both on a transparency checkerboard — and a conversion
  report that says what survived, what did not, and what to do about it. `SvgNote` replaced the
  flat warning strings in the engine; `SvgConvertResult.Warnings` still projects them, so every
  existing test kept passing unchanged.
- **API Builder** took the Postman shape: a collapsible, filterable collections rail with
  coloured verb badges, a method/URL/Send row with the resolved URL beneath it, request tabs
  carrying counts, per-row delete buttons and a description column, and a response half that
  mirrors the request half — a status strip, then Body · Headers · Cookies · Timings · History.
- **API Profiler** was rewritten around `DevTools.Http/Capture`: a loopback proxy, a root
  certificate authority, the Windows proxy switch, and process attribution from the TCP table.

**The constraint that shaped the profiler**

A Windows proxy setting is per user, not per process. Nothing can point one application at a
proxy and leave the rest of the machine alone. Rather than implying per-app capture, the tool
captures everything, reads each connection's owning process from `GetExtendedTcpTable` while
the connection is still open, and the process dropdown filters the list. The banner says so in
those words.

Everything the capture changes outside the process is reversible and is reversed: the proxy
setting is written to `capture.json` before it is changed, restored on stop, restored again at
the next launch if the process died holding it, and `build.ps1 -Task verify` now fails the
build if `X509Store` or the Internet Settings key appears anywhere outside
`src\DevTools.Http\Capture`.

---

### Phase 13 — Review of the redesign

Also not planned. A second review, this time of the redesign itself, raised eight defects and
asked for two features. The defects were mostly one bug each, but two of them had a cause worth
writing down, and one of them was not in the app at all.

**Decisions taken in this phase**

| # | Decision | Rationale |
| --- | --- | --- |
| **D15** | Syntax brushes are **dependency properties set from markup**, not resource lookups in code | `ThemeService` applies the theme by setting `RequestedTheme` on the window root. `Application.Current.Resources[...]` and every `IValueConverter` therefore resolve against the *application* theme, which is not the one on screen. Only `{ThemeResource}` on an element re-resolves. This is why light mode had dark-mode syntax colours, and it is why the JSON tree uses four mutually exclusive labels instead of one label with a converted brush. |
| **D16** | The JSON tree is built from `TreeViewNode`s **by hand**, not bound to the projection | Hand-built nodes can report `HasUnrealizedChildren` and fill on expand, so a large document costs a parse. The price is that the item template is handed the node rather than the row, so it reads `Content` through a cast — and that `Expanding` is raised only for expansions the control drives, so opening the root has to fill it explicitly. |
| **D17** | `-Task run` deletes the staged `AppX` layout, removes a registration that points elsewhere, and then **verifies** `InstallLocation` | See below. |

**What changed**

- **Alignment.** The title-bar commands overlapped the caption buttons at any scaling other
  than 100%: `AppWindow.TitleBar.RightInset` is in physical pixels and the margin is in
  effective ones, so it is divided by `XamlRoot.RasterizationScale` and reapplied whenever the
  window changes.
- **The result pane scrolled sideways instead of wrapping.** A `ScrollViewer` that can scroll
  horizontally measures its child with infinite width, and `TextWrapping` can never take
  effect. Horizontal scrolling is now switched off whenever word wrap is on.
- **Overlapping text** was the line-number gutter painting over the pane footer. Neither
  `Canvas` nor `Border` clips its children, and `UIElement.Clip` accepts only a
  `RectangleGeometry`, so the gutter host clips itself on `SizeChanged`.
- **The command palette** was translucent because its card used a layer fill; it is now an
  opaque solid. **Recents** left the dashboard — six tools fit whole, so the strip was a second
  copy of the same list; `RecentToolsService` still records them. **JSON to C#** no longer
  shares the formatter's braces glyph.
- **Find in a pane (FR-T12)** and **the foldable result tree (FR-J11)**, both described in
  `01-Product-Requirements.md`.

**The defect that was not in the app**

Three fixes appeared to do nothing. The running app was two weeks old: a packaging run had left
a staged copy of the whole app in `bin\...\AppX`, the development registration pointed at *that*
copy, and nothing ever updated it. Every rebuild reported success and launched binaries from
18 September. `-Task run` now deletes the staged layout, removes any registration whose
`InstallLocation` is not the folder just built — registering at the same version does not
reliably move one, and registering over a folder that has been deleted fails outright — and
then throws unless the registration really does point at the new build. It also closes a
running copy first, which is what was locking `DevTools.exe`.

The lesson is narrower than "verify your deployment": a build that reports success is evidence
about the build, not about what is on screen. Anything that can run stale should say where it
loaded from.

---

### Phase 14 — The title bar carries the tool

A second round on the shell: the title bar looked unsettled, the search box was not earning its
width, and the tool's options row could move up into the bar beside the tool's name.

**Decisions taken in this phase**

| # | Decision | Rationale |
| --- | --- | --- |
| **D18** | The title-bar **search box is removed** | `Ctrl+K` already searched the same index, over the whole window and with better results. The box cost 320 px of permanent chrome for a second way in — and it owned `Ctrl+F`, which a text tool wants for finding text in a pane (FR-T12). `ToolCatalog.Search` is untouched; only the second front end is gone. |
| **D19** | The options row is **declared by the page and parented by the shell** | A `UIElement` has one parent, so the row cannot be in two hosts at once. The shell owns both the title-bar host and the band and moves the row between them; the page only declares it. `IToolChrome` carries it across, as it already carried the tool's identity. |
| **D20** | How wide a row needs is **declared, not measured** | A row that has never been in a visual tree has no templates applied and measures to almost nothing — and nothing fits anywhere, so every row went to the title bar whatever its real size. Measuring it after a layout pass means measuring it somewhere, which is the decision being made. `ToolShell.OptionsWidth` is one number per page, right beside the row; guessing high only costs a row of height. |
| **D21** | The option rows **lose their text labels** | "Action", "Indent", "JSONPath", "Result" and the rest cost about 280 px — the difference between a row that fits in the title bar and one that never does. The controls say what they are (`Format \| Minify \| Validate`, `2 spaces`, a JSONPath placeholder), each kept its automation name and gained a tooltip, and the labels inside the overflow flyouts stay, where there is room. |
| **D22** | Caption buttons are set to **`TitleBarHeightOption.Tall`** | The shell's row is 48 px and the system draws minimise, maximise and close at 32, so they sat in the top two thirds while our buttons were centred in all of it. Tall makes the system band 48 as well, and `DevToolsCaptionButtonStyle` sizes ours to the same 46 × 32, so the whole right-hand end shares one centre line. |

**What changed**

- Every tool page gives back a second row of height: with the row in the title bar there is no
  options band at all, and the panes start directly under the chrome.
- The shell re-runs the placement on every resize, so dragging a window edge moves the row
  between the title bar and the band.
- Verified across all six tools at 1550 and 1088 effective pixels. At the wider size the JSON
  Formatter, Diff, JSON to C# and SVG rows ride in the title bar; the API Profiler's row is
  wide enough that it always uses the band; API Builder has no options row at all.

**Two days of the week spent on the wrong window**

Most of the cost of this phase was not the feature. The screenshot harness picked the largest
top-level window the process owned, and opening a tool brought up a larger surface than the
app's own frame — so every verification shot was of something else, cropped to the width I had
asked for. On top of that the window was maximised, which silently ignores `MoveWindow`, and
the resize requests were being DPI-virtualised, so a request for 1360 produced 1700. Three
separate reasons for the same symptom: the screenshots did not change when the code did.

The fix was to pin the window handle once and photograph that, un-maximise before resizing, and
compose from the captured bitmap's width rather than the requested one. The lesson is the same
one as the stale build in Phase 12, one level up: when evidence stops responding to changes,
stop reasoning about the change and go and check the instrument.

**Three defects the phase surfaced**

| Symptom | Cause | Fix |
| --- | --- | --- |
| **The app would not start at all** — "DevTools could not finish starting", no tools | `FavoritesService.LoadAsync` awaits the file with `ConfigureAwait(false)` and then raises `Changed`, so the view model raised a property change on a thread-pool thread. The compiled binding updated there, and the first one needing a converter asked `Application.Current.Resources` for it — a UI-thread-marshalled call that fails off-thread with `RPC_E_WRONGTHREAD`, during start-up, before any tool had loaded. A latent race the rest of the phase happened to expose. | `Services/UiDispatcher.cs`: the UI thread captured once at launch, and every view model that subscribes to a service event marshals through it. Fixed in the shell, Home and API Builder — anywhere a disk load can raise an event. |
| Line numbers stood still while a result scrolled | The gutter was driven by the `TextBox`'s own `ScrollViewer` only. A read-only pane — which is every tool's output — scrolls `ColourHost` instead, and nothing was listening. | `SyncGutterOffset` reads whichever of the two scrollers is on screen, and both now raise it. |
| **JSON Diff** showed no differences and would not scroll | The inputs sat in an `Auto` row, which gives a pane as much height as its content asks for. Two long documents made the editors grow instead of scrolling, and pushed the differences list off the bottom of the window. | Both rows are `*`. SVG to XAML already did this (`3*` / `2*`); the diff page was the one that did not. |

---

### Phase 15 — The diff reads side by side

The diff tool had three views — a tree, a patch, and unified text — and none of them answered
the first question a diff is asked: what do these two documents look like next to each other.
Modelled on [jsondiff.com](https://jsondiff.com), as a native view rather than a web one.

**Decisions taken in this phase**

| # | Decision | Rationale |
| --- | --- | --- |
| **D23** | The layout is built **from the diff tree**, not by pretty-printing each document and matching the two texts afterwards | The tree has already decided which member on the left is which member on the right — including across a reordered or key-matched array, where the same element sits at different indices on the two sides. Walking it emits both columns in step, so a row is aligned by construction instead of by a second guess at the question the differ just answered. |
| **D24** | A row is **padded**, and padding does not advance that column's line numbers | The reference site lets its two columns drift apart after the first size difference, which makes the halves progressively harder to read against each other. Padding keeps them level; numbering only real lines keeps each column's numbers true to its own document. |
| **D25** | Differences are categorised as **missing property / incorrect type / unequal value** | The differ's own vocabulary — added, removed, changed — describes an edit. Reading two documents side by side the question is a different one, and these three are what a reader is actually sorting by, which is why each can be hidden on its own. Telling a changed type from a changed value needed the node to carry what each side *was*: `"1"` and `1` print almost the same. |
| **D26** | Leaf values are kept **whole** | `JsonWriter.WriteInline` ellipsizes at 120 characters, which is right for a tree row and wrong here: a diff that trims the part that differs is worse than no diff. The tree view trims its own rows for display instead. |

**What it cost to find two of these**

The page would not load at all, and the error — `Failed to assign to property
ToggleButton.IsChecked` — named the wrong thing. A checkbox that starts ticked raises `Checked`
while the XAML is still being read, so the handler ran before the named fields it touches had
been assigned; the null reference inside it surfaced as a failure to set `IsChecked`. Controls
that rebuild themselves from markup state now wait for `Loaded`.

Finding it took longer than it should have because the bisection was wrong: commenting the
control out left an **unbalanced XAML comment** that swallowed it, so three runs that appeared
to clear the control had simply not included it. `NavigationService` now logs a failed
navigation, which is what finally produced the real message — a page that will not load should
say why rather than looking like a click that did nothing.

---

## 7. Traceability

Every requirement carries an id (`FR-S**` shell, `FR-T**` shared tool surface, `FR-J**` JSON
tools, `FR-V**` vector, `FR-A**` API tools, `NFR-**`). `09-Traceability-Matrix.md` maps each
id to its implementing file and its test. A requirement with no test row is not done.

---

## 8. Risks

| Risk | Likelihood | Handling |
| --- | --- | --- |
| **HTTP timing breakdown is not reliably obtainable** | Medium | Fallback scope stated in Phase 7 up front; ship fewer segments rather than wrong ones |
| **SVG scope creep** — "it didn't convert my file" | High | The supported subset is written down (Phase 6) and everything outside it produces a named warning, so the tool is never silently wrong |
| **Emitted XAML looks right but will not load** | Medium | The preview pane *is* `XamlReader.Load`; invalid output cannot reach the user unnoticed |
| **WinUI/WPF XAML dialect differences** | Medium | Flavour switch; WinUI restricted to `Path`/`PathIcon`/`Canvas` because it has no `DrawingImage` |
| **Syntax colouring rewrite swallows the schedule** | Medium | Colour only the read-only presenter (§4.4). Fallback: ship monochrome; every tool still works |
| **Generated C# does not compile** | Medium | Roslyn compile + deserialise round-trip is the acceptance test, not a manual eyeball |
| **Secrets land in plaintext** | Medium | `ICredentialStore` from Phase 8 day one; a grep gate for likely secret keys in `workspace\` fixtures |
| **Profiler used as a load generator against third parties** | Low | Hard caps, confirmation above threshold, no unlimited mode |
| **DevKit shell drift** (D3) | Accepted | DevKit is complete and frozen; divergence costs nothing while it stays that way |
| **Six heavy tools do not fit DevKit's one-input UI** | High | D4 splits the base class in Phase 2, before any tool page is written |

---

## 9. What actually happened

Recorded against the plan, including where the plan was wrong.

### Decisions that changed during implementation

| Planned | What was built | Why |
| --- | --- | --- |
| Smart Detect dropped as DevKit-only | **Kept**, with a DevTools-specific detector | The shell already carried it, and it suits these six tools well — JSON offers three of them, an SVG offers the converter, a pasted cURL command offers API Builder. Removing it was more work than porting a 100-line detector. Added as FR-S19. |
| Syntax colouring listed as the risk most likely to be dropped | **Shipped** | The read-only-presenter approach in §4.4 turned out to be about 250 lines plus a tokenizer, not a rewrite of the editor. The fallback was never needed. |
| Nested types in the C# generator as a straightforward layout option | Needed **reference counting and a rename rule** | A nested type may not share its name with a member of its parent (CS0102), and by construction it always would. A type used by two parents cannot be nested inside either. Both are silent compile failures if missed; the Roslyn acceptance test caught them immediately. |
| Ignore-paths as a filter applied during the diff walk | **Pruned from both trees first** | Skipping during the walk is not enough: array elements are paired by hashing them, so two elements differing only in an ignored member hash differently and fail to pair — reporting an add and a remove for a change the user explicitly asked to ignore. |
| Protocol activation handled by subscribing to `AppInstance.Activated` | Needed a **hand-written `Main` with single-instance redirection** | That event only fires for activations redirected to the process, and nothing redirects them by default. Every `devtools://` URI was starting another copy of the app — each with its own settings, favourites and workspace, last writer winning. |
| API Profiler measures a URL you type | **Rewritten to listen** to an application's calls (Phase 12) | The question people arrive with is "what is this app calling, and why is it slow". A URL you already knew about cannot answer it, and after a design review the measuring mode was removed rather than kept alongside. |
| A page header carrying the tool's identity | **Folded into the title bar** (Phase 12) | 90 px repeated on six pages, showing what the navigation pane already said. Handing it back to the tool needed one service to carry the page's commands up to the shell. |
| One `DevTools.Http` targeting `net10.0` | **`net10.0-windows`** (Phase 12) | The capture proxy reads the registry and the TCP table. Both are Win32 and the product is Windows-only. |

### What the plan got right

- **Splitting `ToolViewModelBase` in three (D4) before writing any tool page.** It was the
  load-bearing call. Retrofitting a two-input tool and two long-running network jobs onto a
  single-input live-debounce base afterwards would have meant rewriting all six pages.
- **The two unusual acceptance tests.** Compiling the generated C# and applying the emitted
  JSON Patch both caught real defects on their first run — nested-type collisions and array
  index drift — that no amount of reading the output would have found.
- **Writing the SVG subset down first.** It turned every "it didn't convert my file" into a
  named warning instead of a bug report.
- **Copying DevKit's shell rather than sharing it (D3).** The shell needed a new view-model
  hierarchy, a flat navigation pane, a different privacy statement and a new editor mode. Every
  one of those would have been a breaking change to a shared library.

### Numbers

| | |
| --- | --- |
| Tests | **527** — 390 in Core, 137 in Http |
| Build | 0 warnings, 0 errors across five projects |
| Package | `DevTools.App_1.0.0.0_x64.msixbundle`, 29 MB |
| Memory with all six tools visited | 241 MB against a 300 MB target |
| Privacy gate | Passing — no network type in `Core`, nothing phones home, machine changes confined to `Http\Capture` |

---

## 10. Definition of done

- Every FR id in `01-Product-Requirements.md` has an implementation **and** a test row in the
  traceability matrix.
- `build.ps1 -Task all` completes with **0 warnings, 0 errors**.
- Every `DevTools.Core` and `DevTools.Http` engine has unit tests covering its documented
  failure modes.
- The JSON→C# round-trip test passes across the payload corpus.
- The SVG golden-file corpus emits XAML that `XamlReader.Load` accepts.
- Both privacy greps return no hits.
- The packaged MSIX installs, launches, and all six tools have been exercised with real input.
- The independent audit returns a clean pass.
- `README.md` documents every feature and the build.
