# DevTools — Architecture

**Version:** 1.0 · **Date:** 2026-09-17

---

## 1. Projects and dependency direction

```
DevTools.Core.Tests ──▶ DevTools.Core ◀── DevTools.Http ◀── DevTools.App
                                     ◀────────────────────────────┘
                         DevTools.Http.Tests ──▶ DevTools.Http
```

| Project | TFM | Rule |
| --- | --- | --- |
| `DevTools.Core` | `net10.0` | No WinUI. **No `System.Net`.** Pure functions. |
| `DevTools.Http` | `net10.0-windows` | No WinUI. The only assembly permitted a socket, and the only one that changes anything outside the process. |
| `DevTools.App` | `net10.0-windows10.0.26100.0` | All Windows concerns; single-project MSIX. |

---

## 2. Shared contracts (`DevTools.Core`)

### 2.1 `OperationResult<T>` / `ToolError`

Ported from DevKit verbatim. Engines never throw for bad **input**; they return
`Fail` carrying a message with optional `Line`, `Column` and `Offset`. Exceptions are
reserved for programmer error.

### 2.2 `Limits`

DevKit's values plus the HTTP caps:

```csharp
MaxInputBytes        = 5 MiB      MaxDiffNodes         = 50_000
MaxJsonDepth         = 256        MaxGeneratedTypes    = 500
MaxSvgElements       = 50_000     MaxResponseBytes     = 64 MiB
DefaultHttpTimeout   = 100 s      MaxHistoryPerRequest = 25
```

### 2.3 Text

`TextUtil` (BOM, newline normalisation, grapheme counting, offset→line/column),
`EncodingCatalog`, `CaseConverter` (acronym-aware), `TextDiff` — all ported.

---

## 3. `DevTools.Core.Json` — the shared model (D5)

```csharp
public enum JsonKind { Null, Bool, Number, String, Array, Object }

public sealed record JsonPosition(int Line, int Column, long Offset);

public abstract record JsonNode(JsonPosition Position)
{
    public abstract JsonKind Kind { get; }
}

public sealed record JsonNull(JsonPosition Position)                    : JsonNode(Position);
public sealed record JsonBool(bool Value, JsonPosition Position)        : JsonNode(Position);
public sealed record JsonString(string Value, JsonPosition Position)    : JsonNode(Position);

/// Raw is the ORIGINAL source text: "1.0", "1e10", a 30-digit integer.
/// Nothing in the pipeline ever converts a number through double.
public sealed record JsonNumber(string Raw, JsonPosition Position)      : JsonNode(Position);

public sealed record JsonArray(IReadOnlyList<JsonNode> Items, JsonPosition Position)
                                                                        : JsonNode(Position);

/// Members keeps SOURCE ORDER and permits duplicate names — both are legal JSON and
/// discarding either is how a formatter and a differ end up disagreeing.
public sealed record JsonMember(string Name, JsonNode Value, JsonPosition NamePosition);
public sealed record JsonObject(IReadOnlyList<JsonMember> Members, JsonPosition Position)
                                                                        : JsonNode(Position);
```

| Type | Responsibility |
| --- | --- |
| `JsonReader` | Hand-written recursive-descent parser producing the model. Options: allow trailing commas, allow comments, max depth. Reports line/column/offset on failure and collects duplicate-key warnings. |
| `JsonWriter` | Model → text. Options: indent style, sort keys, minify, escape non-ASCII. Numbers are emitted from `Raw`. |
| `JsonFormatter` | The FR-J01…J10 façade over reader + writer, plus `Analyze` for stats. |
| `JsonPathQuery` | `$`, `.name`, `..name`, `[n]`, `[a:b:c]`, `[*]`, `[?(@.x > 1)]`. |
| `JsonDiffer` | FR-J20…J27. Consumes the model, emits `JsonDiffNode` tree + `JsonPatch`. |

### 3.1 Diff model

```csharp
public enum JsonDiffKind { Unchanged, Added, Removed, Changed }
public enum ArrayStrategy { Index, Key, BestMatch }

public sealed record JsonDiffNode(
    string Path, JsonDiffKind Kind, string? Left, string? Right,
    IReadOnlyList<JsonDiffNode> Children);

public sealed record JsonDiffResult(
    JsonDiffNode Root, int Added, int Removed, int Changed, int Unchanged,
    bool AreEqual, string JsonPatch);
```

`BestMatch` runs an LCS over per-element structural hashes, so inserting one element does
not report every following element as changed.

---

## 4. `DevTools.Core.CodeGen`

```csharp
public sealed record CSharpGenOptions {
    TypeKind Kind; MemberStyle Members; AttributeStyle Attributes;
    CollectionKind Collections; bool NullableAnnotations; bool DetectDateGuidUri;
    NamespaceStyle Namespace; string NamespaceName; bool NestTypes; string RootTypeName; }

public static class CSharpFromJson {
    public static OperationResult<CodeGenResult> Generate(string json, CSharpGenOptions o);
}
public sealed record CodeGenResult(string Code, int TypeCount, IReadOnlyList<string> Warnings);
```

Pipeline: `JsonReader` → **shape inference** (`ShapeBuilder` merges array elements, widens
numerics, marks absent members nullable) → **type graph** (`TypeGraph` deduplicates
structurally identical shapes) → **naming** (`IdentifierFactory`: PascalCase, keyword escape,
collision rename) → **emit** (`CSharpEmitter`).

---

## 5. `DevTools.Core.Vector`

```csharp
public sealed record SvgConvertOptions {
    XamlFlavor Flavor;          // Wpf | WinUi
    XamlOutputShape Shape;      // Canvas | MergedPath | DrawingImage | PathIcon
    bool IncludeViewBoxTransform; int DecimalPlaces; string? ResourceKey; }

public static class SvgToXaml {
    public static OperationResult<SvgConvertResult> Convert(string svg, SvgConvertOptions o);
}
public sealed record SvgConvertResult(string Xaml, int ElementCount, IReadOnlyList<string> Warnings);
```

| Type | Responsibility |
| --- | --- |
| `SvgParser` | XML → `SvgElement` tree; resolves `use`/`symbol`/`defs`, inherits presentation attributes and inline `style`. |
| `SvgPathGrammar` | The full path mini-language → `PathFigure` list, including compressed arc flags. |
| `SvgArc` | Endpoint → centre arc parameterisation, with the spec's degenerate-radius corrections. |
| `SvgTransform` | `matrix`/`translate`/`scale`/`rotate`/`skewX`/`skewY` → a 2×3 matrix; composes nested transforms. |
| `SvgPaint` | Colour and gradient resolution (`#rgb`, `#rrggbb`, `rgb()`, `rgba()`, `hsl()`, named, `currentColor`). |
| `XamlEmitter` | Emits per flavour. **WinUI never receives `DrawingImage`/`DrawingGroup`/`DrawingBrush` — they do not exist there.** |

Unsupported features are collected into `Warnings` by name (FR-V09); the converter never
drops one silently.

---

## 6. `DevTools.Http`

```csharp
public sealed record RequestDefinition {
    string Id; string Name; string Method; string Url;
    IReadOnlyList<HeaderItem> Headers; IReadOnlyList<QueryItem> Query;
    BodySpec Body; AuthSpec Auth; RequestOptions Options; }

public sealed record ResponseRecord(
    int StatusCode, string ReasonPhrase, string HttpVersion,
    IReadOnlyList<HeaderItem> Headers, IReadOnlyList<CookieItem> Cookies,
    byte[] Body, string? MediaType, string? CharSet,
    long ContentLength, RequestTiming Timing, DateTimeOffset StartedAt);

public sealed record RequestTiming(
    TimeSpan Dns, TimeSpan Connect, TimeSpan Tls,
    TimeSpan TimeToFirstByte, TimeSpan Download, TimeSpan Total);
```

### 6.1 `HttpExecutor` — the only file that opens a socket

Timing comes from a `SocketsHttpHandler` whose `ConnectCallback` performs DNS and TCP by
hand (exact `Dns` and `Connect` figures) and whose `PlaintextStreamFilter` brackets the TLS
handshake, with stopwatches around send, first byte and body read for the rest.

### 6.2 Capture — removed

The capture proxy, certificate handling and process attribution were removed along with the API Profiler.

### 6.3 Workspace

`Workspace` → `Collection` → `Folder` → `RequestDefinition`; `Environment` → variables.
Persisted as versioned JSON under `LocalFolder\workspace\`, written atomically.
`VariableResolver` substitutes `{{name}}` with precedence environment → collection → global.
`ICredentialStore` (implemented in `DevTools.App` over `PasswordVault`) holds every secret;
documents carry only a reference (D8).

Importers/exporters: `CurlCodec` (both directions), `OpenApiImporter`, `PostmanImporter`.

---

## 7. `DevTools.App`

### 7.1 Composition

`App.OnLaunched` builds a `ServiceProvider` holding `ISettingsService`, `IToolStateService`,
`IFavoritesService`, `IRecentToolsService`, `IClipboardService`, `IFileDialogService`,
`IDialogService`, `IThemeService`, `INavigationService`, `IProtocolActivationService`,
`IToolHandoffService`, `ICredentialStore`, `IWorkspaceService`, `ToolCatalog`, and one
transient view model per tool.

### 7.2 View-model hierarchy (D4)

```
ToolViewModelBase              message banner · IsBusy · cancellation · generation guard
  ├── TextToolViewModelBase    Input/Output · 250 ms debounce · Paste/Open/Clear/Copy/Save
  │      └── JsonFormatterViewModel, JsonToCSharpViewModel, SvgToXamlViewModel
  ├── DualTextToolViewModelBase Left/Right inputs · swap
  │      └── JsonDiffViewModel
  └── JobToolViewModelBase     explicit Start/Stop · progress · partial results
         └── ApiBuilderViewModel
```

A stale run can never overwrite a newer one: each run captures a monotonically increasing
generation number and discards its result if the generation has moved on.

### 7.3 Controls

`ToolShell`, `CodeEditor` (+ `GutterBuilder`), `ToolSplitPanel` — ported. New:
`ColourisedTextPresenter` (read-only `RichTextBlock` driven by a token list; JSON, XML and
XAML tokenizers live in `DevTools.Core.Text.Tokenizers`), `KeyValueTable`, `DiffTreeView`,
`SparkChart` (`Canvas`-drawn line and histogram — no charting dependency).

### 7.4 Threading

Input mutates on the UI thread → debounce → `Task.Run` on the thread pool → results marshal
back through the captured `DispatcherQueue`. No engine touches a UI type, so this is always
safe.

### 7.5 Persistence

| Data | Location |
| --- | --- |
| Settings, window placement | `ApplicationData.LocalSettings` |
| Favorites, recents | `LocalFolder\favorites.json`, `recents.json` |
| Per-tool state | `LocalFolder\state\<tool-id>.json`, debounced 500 ms |
| Collections, environments | `LocalFolder\workspace\`, versioned, atomic |
| Secrets | Windows `PasswordVault` — never a file |

All writes are atomic (temp then replace); every read tolerates a missing or corrupt file by
falling back to defaults.

---

## 8. Error handling

| Layer | Policy |
| --- | --- |
| Engine | `OperationResult.Fail` with a precise message; never throws on bad input |
| `HttpExecutor` | Maps `SocketException`, `AuthenticationException`, timeout and DNS failure to distinct, actionable messages (edge case 19) |
| View model | Maps failure to the InfoBar; keeps the last good output visible |
| App | `UnhandledException` logs to `LocalFolder\logs\crash-<date>.log` and shows a dialog |

---

## 9. Testing

`DevTools.Core.Tests` mirrors the engine namespaces one-to-one. `DevTools.Http.Tests` uses a
loopback `HttpListener` only — never a real host. Two acceptance tests carry unusual weight:

- **C# generator:** compile the generated source with Roslyn, deserialise the sample into it,
  re-serialise, and assert semantic equality via `JsonDiffer`.
- **SVG converter:** a golden-file corpus whose emitted XAML must be accepted by an XML
  parser and, in the app, by `XamlReader.Load`.
