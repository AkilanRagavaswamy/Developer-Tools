# ForgeKitRk – Developer Toolkit

A Windows desktop workbench for **API and payload work** — twenty tools that cover the loop
you actually run: compose a request, send it, format the response, model it in C#, diff it
against what it used to be, and the dozen small conversions that happen in between.

Built as a WinUI 3 packaged (MSIX) application on .NET 10.

---

## The tools

| Tool | What it does |
| --- | --- |
| **JSON Formatter** | Pretty-print, minify and validate JSON; query it with JSONPath; sort keys. Read the result as text or as a foldable tree, where every object and array collapses by key and the selected row hands you its JSONPath. Numbers keep their exact literal form — `1.0` stays `1.0`, `1e10` is not expanded, and a 30-digit integer keeps every digit. |
| **JSON Diff Checker** | Compares two documents by *structure*, so member order is never a difference. Reads them side by side, row for row, with differences coloured by kind and a navigator that steps through them one at a time. Three array-matching strategies, seven loosening options, and an RFC 6902 JSON Patch you can hand to anything else. The side-by-side view exports as a self-contained HTML page. |
| **JSON to C#** | Generates compilable models from a JSON sample. Records or classes, `init` / `set` / `required`, System.Text.Json or Newtonsoft attributes, nullable annotations, and date/GUID/URI detection. |
| **SVG to XAML** | Converts SVG shapes, paths, transforms and gradients to WPF or WinUI XAML, with a live preview that renders the output by actually loading it. |
| **JSON to Table** | Lays a JSON array out as rows and columns — nested objects become dotted columns, arrays stay as JSON in their cell — and copies it as tab-separated values for Excel, RFC 4180 CSV or a Markdown table. |
| **SQL Formatter** | One clause per line, in ten dialects (Standard SQL, T-SQL, MySQL, MariaDB, PostgreSQL, PL/SQL, Db2, Redshift, Spark SQL, N1QL), with keyword case and leading-comma options. Only whitespace and keyword case ever change. |
| **XML Formatter** | Indents or minifies XML by streaming it, keeps the declaration exactly as written, and never processes a DTD or fetches an external entity. |
| **Date & Unix Time** | One box for a timestamp in seconds, milliseconds, microseconds or nanoseconds — the unit read from its size — or a date in any zone, shown fourteen ways including ISO 8601, RFC 1123, .NET ticks and FILETIME. |
| **Base64 Text** | Encodes and decodes in seven text encodings, URL-safe or standard, wrapped or not, line by line if asked. Decoding forgives form (padding, line breaks, either alphabet, a `data:` prefix) and refuses to show binary as text. |
| **Base64 Image** | Image to Base64 or a data URI and back, with a preview; recognises PNG, JPEG, GIF, BMP, WebP, ICO, TIFF and SVG by their bytes, not their label. |
| **URL Encoder** | Percent-encoding for a query value, a whole URL or form data, decoded as UTF-8. |
| **HTML Encoder** | Escapes and unescapes HTML entities, optionally writing every non-ASCII character as a numeric reference. |
| **UUID Generator** | Versions 4, 7 and 1, up to 10,000 at a time, in four formats. A batch of version 7 UUIDs sorts in the order it was made. Paste one to see its version and, for v7, when it was created. |
| **QR Code Generator** | Text or a URL to a QR code with a choice of recovery level, quiet zone and colours; saves as PNG up to 4096 px or as SVG, and copies either. |
| **Text Compare** | Side by side or inline, with the changed words inside a changed line highlighted, a navigator through the differences, and a unified diff to copy — or the side-by-side view saved as an HTML page. |
| **Character Counter** | Characters as people count them, words, sentences, paragraphs, lines, bytes and reading time, measured against the common limits — a post on X, an SMS, an SEO title. |
| **Regex Validator** | Tests a .NET regular expression against text: every match with its line, column and groups, an optional replacement preview, the usual flags and a JavaScript mode. A two-second limit stops a catastrophically backtracking pattern instead of freezing the app. |
| **HTML Viewer** | Open or write HTML and see the page update as you edit. The preview never goes online, and the page's own scripts run only if you allow them. |
| **Markdown Preview** | GitHub-flavoured Markdown rendered as you type, in light or dark, saved as a standalone HTML page. The preview runs no script and loads nothing from the network. |
| **API Builder** | Collections, folders, environments and `{{variables}}`; five auth schemes; JSON, form, multipart and binary bodies; a response viewer with timing, headers and cookies; cURL, OpenAPI 3 and Postman import. |

They are one app because they are one workflow: every response can be formatted, modelled or
diffed in a click. The tools you opened last are waiting on the Home dashboard and at the top of
the command palette.

---

## What it promises about your data

One of these tools exists to send HTTP requests, so "nothing leaves the machine" would be a lie.
The honest version, and how each part is checkable:

| Claim | How it is enforced |
| --- | --- |
| Every tool except API Builder has no network code **at all** | Their engines live in `DevTools.Core`, which has no package references and no `System.Net` usage. `build.ps1 -Task verify` greps for every network type and fails the build on a hit. |
| Everything outbound goes through one file | All socket work lives in `src/DevTools.Http/Execution/HttpExecutor.cs`. Nothing else constructs a handler. |
| No telemetry, analytics, crash reporting or update checks — ever | The same verify task greps the whole of `src` and fails on a hit. |
| Secrets are never written to a file | Passwords, tokens and client secrets go to the Windows credential vault; collections store a reference. Exporting a collection leaves them out by default. |
| Markdown Preview and HTML Viewer cannot reach the internet | Both previews are a WebView2 with a handler that refuses every request the page makes, behind a Content-Security-Policy that allows only inline style and `data:` images. Script is off — in HTML Viewer, unless you turn on the page's own scripts, which still cannot fetch anything. A link you click opens in your own browser. |

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
`forgekitrk://` activation. Offering it would be a button that always crashes.

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
| `DevTools.Http` | `net10.0-windows` | The only assembly permitted a socket: the request builder, executor and collection import/export. |
| `DevTools.App` | `net10.0-windows10.0.26100.0` | All Windows concerns; single-project MSIX. |

Every engine is a static class of pure functions returning `OperationResult<T>`. Engines never
throw for bad *input* — they return a failure carrying a message with a line, column or offset,
so the UI can point at the exact character. That is why almost all of the logic is testable
without a UI thread.

The view models come in three shapes rather than one, because the tools genuinely differ:
`TextToolViewModelBase` (one input, one output, live debounce), `DualTextToolViewModelBase`
(two inputs, for the differ) and `JobToolViewModelBase` (an explicit, cancellable job that keeps
partial results — the API Builder).

---

## Tests

579 automated tests, all passing.

| Suite | Count | Covers |
| --- | --- | --- |
| `DevTools.Core.Tests` | 469 | JSON reader/writer/formatter/JSONPath, the differ and its patch, the C# generator, the SVG converter, the SQL formatter in all ten dialects, the XML formatter, JSON to Table, the date converter, the codecs, the UUID generator, the regex tester, the HTML diff export, text statistics, Smart Detect, the syntax tokenizer |
| `DevTools.Http.Tests` | 110 | Request building and all five auth schemes, the executor and its timings, cURL/OpenAPI/Postman import, variables, workspace persistence |

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

---

## Documents

| Document | What is in it |
| --- | --- |
| [`Docs/00-Development-Plan.md`](Docs/00-Development-Plan.md) | The phase-wise plan, the decisions and the risks |
| [`Docs/01-Product-Requirements.md`](Docs/01-Product-Requirements.md) | Every requirement, with its id and acceptance criteria |
| [`Docs/02-Architecture.md`](Docs/02-Architecture.md) | Projects, namespaces and the exact type contracts |
| [`Docs/09-Traceability-Matrix.md`](Docs/09-Traceability-Matrix.md) | Every requirement mapped to its implementation and its test |
