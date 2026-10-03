# DevTools

A Windows desktop workbench for **API and payload work** — six tools that cover the loop you
actually run: compose a request, send it, profile it, format the response, model it in C#, and
diff it against what it used to be.

Built as a WinUI 3 packaged (MSIX) application on .NET 10.

---

## The tools

| Tool | What it does |
| --- | --- |
| **JSON Formatter** | Pretty-print, minify and validate JSON; query it with JSONPath; sort keys. Read the result as text or as a foldable tree, where every object and array collapses by key and the selected row hands you its JSONPath. Numbers keep their exact literal form — `1.0` stays `1.0`, `1e10` is not expanded, and a 30-digit integer keeps every digit. |
| **JSON Diff Checker** | Compares two documents by *structure*, so member order is never a difference. Reads them side by side, row for row, with differences coloured by kind and a navigator that steps through them one at a time. Three array-matching strategies, seven loosening options, and an RFC 6902 JSON Patch you can hand to anything else. |
| **JSON to C#** | Generates compilable models from a JSON sample. Records or classes, `init` / `set` / `required`, System.Text.Json or Newtonsoft attributes, nullable annotations, and date/GUID/URI detection. |
| **SVG to XAML** | Converts SVG shapes, paths, transforms and gradients to WPF or WinUI XAML, with a live preview that renders the output by actually loading it. |
| **API Builder** | Collections, folders, environments and `{{variables}}`; five auth schemes; JSON, form, multipart and binary bodies; a response viewer with timing, headers and cookies; cURL, OpenAPI 3 and Postman import. |
| **API Profiler** | Listens to the HTTP calls another application makes and lists them: method, host, status, size, timing, whole headers and whole bodies. Pick a process, press Start, read the exchange. |

They are one app because they are one workflow: every response can be formatted, modelled,
diffed or profiled in a click, and every request can be profiled without retyping it.

---

## What it promises about your data

Two of these tools exist to send HTTP requests, so "nothing leaves the machine" would be a lie.
The honest version, and how each part is checkable:

| Claim | How it is enforced |
| --- | --- |
| The JSON, SVG and code-generation tools have no network code **at all** | `DevTools.Core` has no package references and no `System.Net` usage. `build.ps1 -Task verify` greps for every network type and fails the build on a hit. |
| Everything outbound goes through one file | All socket work lives in `src/DevTools.Http/Execution/HttpExecutor.cs`. Nothing else constructs a handler. |
| No telemetry, analytics, crash reporting or update checks — ever | The same verify task greps the whole of `src` and fails on a hit. |
| Secrets are never written to a file | Passwords, tokens and client secrets go to the Windows credential vault; collections store a reference. Exporting a collection leaves them out by default. |

---

## Building

### Prerequisites

| Requirement | Version |
| --- | --- |
| Windows | 10 build 19041 or newer (developed on Windows 11 Pro 26200) |
| .NET SDK | **10.0.401** or newer — `global.json` pins the band |
| Windows SDK | 10.0.26100.0 |
| Windows App Runtime | 2.4.x — installed by the MSIX; needed manually only to run from `bin` |
| Visual Studio | **Optional.** Every task below works from the command line. |
| Developer Mode | On — only for `-Task run`, which registers an unsigned loose layout |

Internet access is needed once, for the NuGet restore.

### One entry point

```powershell
.\build\build.ps1 -Task all
```

| Task | What it does |
| --- | --- |
| `restore` | NuGet restore for the whole solution |
| `build` | Build Core, Http and the app (default) |
| `test` | Run both test suites |
| `verify` | Check the privacy boundary — fails if `Core` gained a network type or anything phones home |
| `package` | Produce an installable `.msix` under `src\DevTools.App\AppPackages` |
| `run` | Build, register for development, and launch |
| `clean` | Remove every `bin`, `obj` and `AppPackages` folder |
| `all` | restore → build → test → verify → package, in Release |

`-Configuration Debug|Release` and `-Platform x64|ARM64` both work; platform defaults to the
machine's architecture.

### Debugging in Visual Studio

Press F5 with `DevTools.App` as the startup project and the **DevTools (Package)** profile
selected. That profile lives in `src\DevTools.App\Properties\launchSettings.json`; without it
Visual Studio reports *"a profile with command name MsixPackage in launchSettings.json is
required"* and refuses to start, because a single-project MSIX app is deployed rather than
simply launched.

There is deliberately only one profile. The usual template also offers an *Unpackaged* profile
(`"commandName": "Project"`), which cannot work here: `WindowsPackageType` is `MSIX`, and the
app needs package identity for `ApplicationData.Current.LocalSettings`, the credential vault and
`devtools://` activation. Offering it would be a button that always crashes.

Debugging needs Developer Mode on, the same as `-Task run`.

### By hand

```bash
dotnet restore DevTools.sln -p:Platform=x64
dotnet build src/DevTools.Core/DevTools.Core.csproj -c Release
dotnet build src/DevTools.Http/DevTools.Http.csproj -c Release
dotnet build src/DevTools.App/DevTools.App.csproj -c Release -p:Platform=x64
```

xUnit v3 hosts its own runner, and the .NET 10 SDK removed the VSTest bridge `dotnet test`
used to go through — so run the built host directly:

```bash
dotnet build tests/DevTools.Core.Tests/DevTools.Core.Tests.csproj -c Release
./tests/DevTools.Core.Tests/bin/Release/net10.0/DevTools.Core.Tests.exe
```

---

## How it is put together

```
DevTools.Core.Tests ──▶ DevTools.Core ◀── DevTools.Http ◀── DevTools.App
                                     ◀────────────────────────────┘
                         DevTools.Http.Tests ──▶ DevTools.Http
```

| Project | Target | Rule |
| --- | --- | --- |
| `DevTools.Core` | `net10.0` | Pure engines. No WinUI, **no `System.Net`**, no package references. |
| `DevTools.Http` | `net10.0-windows` | The only assembly permitted a socket. Windows-targeted because the capture proxy reads the registry and the TCP table. |
| `DevTools.App` | `net10.0-windows10.0.26100.0` | All Windows concerns; single-project MSIX. |

Every engine is a static class of pure functions returning `OperationResult<T>`. Engines never
throw for bad *input* — they return a failure carrying a message with a line, column or offset,
so the UI can point at the exact character. That is why almost all of the logic is testable
without a UI thread.

The view models come in three shapes rather than one, because these six tools genuinely differ:
`TextToolViewModelBase` (one input, one output, live debounce), `DualTextToolViewModelBase`
(two inputs, for the differ) and `JobToolViewModelBase` (an explicit, cancellable job that keeps
partial results — the two API tools).

---

## Tests

527 automated tests, all passing.

| Suite | Count | Covers |
| --- | --- | --- |
| `DevTools.Core.Tests` | 390 | JSON reader/writer/formatter/JSONPath, the differ and its patch, the C# generator, the SVG converter, the syntax tokenizer |
| `DevTools.Http.Tests` | 137 | Request building and all five auth schemes, the executor and its timings, the capture proxy and its wire reader, process attribution, certificate issuance, cURL/OpenAPI/Postman import, variables, workspace persistence |

Two of them carry unusual weight:

- **The C# generator compiles its own output.** The test runs Roslyn over the generated source,
  deserialises the original sample into it, re-serialises, and asserts the result is the same
  document via the differ. "It generated code" is not the bar.
- **The differ's patch is applied, not eyeballed.** For every case, applying the emitted RFC 6902
  patch to the left document must produce the right one — including the array edits where
  indices shift underneath you.

The SVG converter is held against a 30-file corpus of real-world SVGs — Material and Feather
icons, compressed arc flags, designer exports — and every one must emit XAML that parses, in
both dialects and every output shape.

---

## Things worth knowing

**The SVG converter's supported subset is written down** (`Docs/01 §4.4`). Anything outside it —
`<text>`, filters, masks, patterns, animation, CSS blocks — produces a named warning rather than
being silently dropped, because the alternative is finding out from the rendered picture.

**WinUI has no `DrawingImage`, `DrawingGroup` or `DrawingBrush`**, and its `UIElement.Clip` is a
`RectangleGeometry` and nothing else. The converter offers only the output shapes each dialect
can actually express, and says so when a clip path cannot survive the trip.

**The profiler needs traffic pointed at it, and says how.** A Windows proxy setting is per user,
not per process: nothing can point one application at a proxy and leave the rest alone. So the
capture proxy sees everything, reads each connection's owning process from the TCP table, and the
process dropdown *filters* the list.

Pointing Windows at the proxy automatically is the part DevTools cannot do from inside its own
MSIX package — a packaged process has its registry writes redirected into a private hive, so the
change would appear to succeed and capture nothing. The tool checks for that before it tries,
says so in plain words, and gives you the address to paste into the app you want to watch (or to
set as `HTTP_PROXY` and `HTTPS_PROXY` when starting it). Where the setting *can* be changed, it is
written down first, put back on stop, and restored at the next launch if DevTools died holding it.

**https bodies need a certificate you have to agree to.** Without a DevTools root trusted for
your user account, an https call is recorded as a tunnel — host, size and timing, no content.
Installing the root is its own button with its own consent, removing it is offered beside it,
and a client that pins its certificate is listed as pinned rather than looking like a failure.

**Captured bodies are kept whole.** Nothing is truncated or sampled, because the response you
wanted to read is always the one that would have been cut. The capture bar shows the running
total of bytes held, so the cost of that choice is on screen rather than in Task Manager.

---

## Documents

| Document | What is in it |
| --- | --- |
| [`Docs/00-Development-Plan.md`](Docs/00-Development-Plan.md) | The phase-wise plan, the decisions and the risks |
| [`Docs/01-Product-Requirements.md`](Docs/01-Product-Requirements.md) | Every requirement, with its id and acceptance criteria |
| [`Docs/02-Architecture.md`](Docs/02-Architecture.md) | Projects, namespaces and the exact type contracts |
| [`Docs/09-Traceability-Matrix.md`](Docs/09-Traceability-Matrix.md) | Every requirement mapped to its implementation and its test |
