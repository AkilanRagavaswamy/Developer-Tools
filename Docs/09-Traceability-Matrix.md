# DevTools — Traceability Matrix

**Version:** 1.0 · **Date:** 2026-09-17

Every requirement in [`01-Product-Requirements.md`](01-Product-Requirements.md) mapped to what
implements it and what proves it. A requirement with no evidence column is not done.

**Legend** — *Automated*: an xUnit test asserts it. *Manual*: verified by running the packaged
app. *Gate*: enforced by the build.

---

## 1. Shell

| ID | Requirement | Implementation | Evidence |
| --- | --- | --- | --- |
| FR-S01 | Flat navigation, compact on launch | `Views/ShellPage.xaml.cs` · `BuildNavigation`, `OnPaneToggleClick` | Manual — six icons at 48 px, names on hover, state remembered |
| FR-S02 | Home dashboard with cards and pins | `ViewModels/HomeViewModel.cs` · `Views/HomePage.xaml` | **Verified** — six cards, pinned ones above; no recent strip |
| FR-S03 | Ranked search (through the palette) | `Services/ToolCatalog.cs` · `Search`/`Score` · `ShellViewModel.OnPaletteQueryChanged` | **Verified** — the title-bar box is gone; `Ctrl+K` searches the same index |
| FR-S04 | `Ctrl+K` command palette | `ViewModels/ShellViewModel.cs` · `OpenPalette` | Manual |
| FR-S05 | Favorites, persisted | `Services/FavoritesService.cs` | Manual — `favorites.json` written |
| FR-S06 | Recents, de-duplicated (recorded, not displayed) | `Services/RecentToolsService.cs` · `Services/NavigationService.cs` | **Verified** — `recents.json` holds all six after use |
| FR-S07 | Theme applied live | `Services/ThemeService.cs` | Manual |
| FR-S08 | Backdrop with fallback | `Services/ThemeService.cs` · `MainWindow.xaml.cs` | Manual |
| FR-S09 | Custom title bar | `MainWindow.xaml.cs` · `ShellPage.xaml` · `Styles/Controls.xaml` · `DevToolsCaptionButtonStyle` | **Verified** — identity, options row and four 46 × 32 commands, all sharing a centre line with the Tall caption buttons, in both themes |
| FR-S10 | Window placement restored and clamped | `Services/SettingsService.cs` · `MainWindow.xaml.cs` | Manual |
| FR-S11 | Per-tool state, debounced | `Services/ToolStateService.cs` · `ToolViewModelBase.PersistState` | **Verified** — `state/*.json` written per tool |
| FR-S12 | Settings page incl. privacy statement | `Views/SettingsPage.xaml` · `SettingsViewModel.PrivacyNotes` | Manual |
| FR-S13 | Keyboard shortcuts | `Views/ToolPageBase.cs` · `ShellPage.xaml.cs` | Manual |
| FR-S14 | Navigation history, no back button | `Services/NavigationService.cs` · `OnPointerPressed` | Manual — Alt+← and mouse button 4 |
| FR-S15 | Accessibility names, High Contrast | Every page; `Styles/Theme.xaml` | Manual |
| FR-S16 | Localisation-ready strings | `Strings/en-US/Resources.resw` | Manual |
| FR-S17 | Single-project MSIX, `devtools:` protocol | `Package.appxmanifest` · `Services/ProtocolActivationService.cs` · `Program.cs` | **Verified** — six URIs opened six tools in **one** instance |
| FR-S18 | Cross-tool hand-off | `Services/ToolHandoffService.cs` | Manual — see FR-A31 |
| FR-S19 | Smart Detect on the clipboard | `Core/Detection/SmartDetector.cs` | Manual |

---

## 2. Shared tool surface

| ID | Requirement | Implementation | Evidence |
| --- | --- | --- | --- |
| FR-T01 | Title-bar identity, one options row | `Services/ToolChromeService.cs` · `ShellPage.PlaceOptions` · `Controls/ToolShell.xaml` | **Verified** — all six pages at 1550 and 1088 effective px: four rows ride in the title bar at the wider size, the profiler is always too wide and uses the band, API Builder has no row. Nothing clipped at either size |
| FR-T02 | Paste / Open / Clear + counters | `TextToolViewModelBase` · `Controls/CodeEditor.xaml` | Manual |
| FR-T03 | Copy with confirmation, Save as | `TextToolViewModelBase.CopyOutput` / `SaveOutputAsync` | Manual |
| FR-T04 | Live transform, 250 ms debounce | `TextToolViewModelBase.ScheduleRun` | Manual |
| FR-T05 | Error surface keeps last good output | `TextToolViewModelBase.Apply` | Manual |
| FR-T06 | Empty input is a placeholder | `HandleEmptyInput` | Automated — every engine's empty-input test |
| FR-T07 | Large-input guard | `ExceedsInputGuardAsync` | Manual |
| FR-T08 | Two-pane responsive split | `Controls/ToolSplitPanel.xaml` | Manual |
| FR-T09 | Editor options from Settings | `Controls/CodeEditor.xaml.cs` | Manual |
| FR-T10 | Drag and drop | `Controls/CodeEditor.xaml.cs` | Manual |
| FR-T11 | Syntax colouring | `Core/Text/SyntaxTokenizer.cs` · `Controls/ColourisedTextPresenter.cs` · `CodeEditor.Syntax` | Automated — 36 tests incl. the tiling invariant on malformed input |
| FR-T12 | `Ctrl+F` find in pane | `Controls/CodeEditor.xaml` · `CodeEditor.Find` · `ColourisedTextPresenter.Highlight` | **Verified** — `id` in the JSON pane reads `1 of 3` and selects; `Enter` → `2 of 3`, `Shift+Enter` → back; match case narrows `A` from any-case to `1 of 2`; in the coloured Result pane all three matches are marked at once; `Esc` closes. Both panes search independently |

---

## 3. JSON Formatter

| ID | Requirement | Implementation | Evidence (all automated) |
| --- | --- | --- | --- |
| FR-J01 | Pretty / minify / validate | `Json/JsonFormatter.cs` | `Pretty_indents_two_spaces_by_default`, `Minify_removes_every_insignificant_byte`, `Validate_only_reports_a_verdict…` |
| FR-J02 | Indent 2 / 4 / tab | `Json/JsonWriter.cs` | `Pretty_honours_four_spaces_and_tab` |
| FR-J03 | Recursive key sorting | `JsonWriter.WriteObject` | `Sort_keys_is_recursive_and_ordinal` |
| FR-J04 | Trailing commas, comments | `Json/JsonReader.cs` | `Trailing_commas_are_refused_by_default…`, `Comments_are_refused_by_default…` |
| FR-J05 | **Number fidelity** | `JsonNumber.Raw` · `JsonWriter` | `Numbers_round_trip_byte_for_byte` — 9 cases incl. `1.0`, `1e10`, `-0`, 30 digits |
| FR-J06 | JSONPath | `Json/JsonPathQuery.cs` | 16 tests: index, negative index, slices, wildcards, unions, filters, recursive descent |
| FR-J07 | Document stats | `JsonFormatter.Analyze` | `Stats_count_objects_arrays_keys_and_depth` |
| FR-J08 | Line / column / offset on failure | `JsonReader` · `ToolError` | `Errors_carry_line_column_and_offset` |
| FR-J09 | Duplicate keys reported | `JsonReader.ReadObject` | `Duplicate_keys_are_kept_and_warned_about` |
| FR-J10 | Escape non-ASCII | `JsonWriter.WriteString` | `Escape_non_ascii_emits_surrogate_safe_escapes` |
| FR-J11 | Foldable result tree | `Core/Json/JsonOutline.cs` · `Controls/JsonTreeView.xaml` | 20 tests on the projection (paths, counts, leaf notation, number fidelity, bracketed names) · **Verified** — root opens filled, expand-all reaches `$.orders[0].items[0].sku`, collapse-all leaves `$`, selecting `lng` shows and copies `$.address.geo.lng` |

---

## 4. JSON Diff Checker

| ID | Requirement | Implementation | Evidence (all automated) |
| --- | --- | --- | --- |
| FR-J20 | Member order is never a difference | `JsonObject.HashInto` (sorted) | `Member_order_is_never_a_difference` |
| FR-J21 | Index / Key / Best-match strategies | `JsonDiffer.PairBy*` | `Best_match_reports_one_insertion_not_a_cascade`, `Index_strategy_reports_the_cascade…`, `Key_strategy_pairs_records_that_reordered` |
| FR-J22 | Seven loosening options | `JsonDiffOptions` · `JsonDiffer.Prune` | 8 tests incl. `Ignore_paths_excludes_matching_nodes_at_any_depth` |
| FR-J23 | Marker glyph as well as colour | `JsonDiffNode.Marker` · `JsonDiffPage.xaml` | `Diff_nodes_carry_a_marker_glyph_as_well_as_a_kind` |
| FR-J24 | Unified text / textual fallback | `JsonDiffer.CompareAsText` | `Textual_mode_falls_back_to_the_line_differ` |
| FR-J25 | Added / removed / changed counts | `JsonDiffResult` | `Added_removed_and_changed_members_are_counted` |
| FR-J26 | **RFC 6902 patch** | `Json/JsonPatch.cs` | **20 cases**: applying the patch must reproduce the right document |
| FR-J27 | Node cap | `Limits.MaxDiffNodes` · `DiffContext.Budget` | `PairByBestMatch` fallback warning |
| FR-J28 | Side-by-side view | `Core/Json/JsonDiffLayout.cs` · `Controls/JsonDiffView.xaml` | 23 tests on the projection: categories, blank padding, per-column line numbers, commas per side, reordered key-matched pairing, long values kept whole · **Verified** — two documents give `Found 6 differences`, `3 missing properties / 2 incorrect types / 1 unequal value`, the navigator walks them and scrolls to each, in both themes |

---

## 5. JSON to C#

| ID | Requirement | Implementation | Evidence (all automated) |
| --- | --- | --- | --- |
| FR-J40 | Merge every array element | `CodeGen/JsonShape.cs` · `BuildArray` | `Every_array_element_contributes_to_the_inferred_shape` |
| FR-J41 | Widening; conflicts become `object` + warning | `ShapeBuilder.MergeScalar` | `Numbers_widen_across_the_sample`, `Conflicting_types_become_object_and_name_the_path` |
| FR-J42 | Absent member becomes nullable | `ObjectShape.IsNullable` | `A_member_missing_from_some_samples_is_nullable` |
| FR-J43 | Date / GUID / URI detection | `ShapeBuilder.StringKind` | `Dates_guids_and_uris_are_detected…`, `Short_strings_are_not_mistaken_for_dates` |
| FR-J44 | Eight option groups | `CSharpGenOptions` | One test per value of every option |
| FR-J45 | Naming, keywords, collisions | `CodeGen/IdentifierFactory.cs` | `Member_names_become_pascal_case` (6), `Names_starting_with_a_digit…`, `Colliding_member_names…` |
| FR-J46 | One type per distinct shape | `TypeGraph.Signature` | `Structurally_identical_objects_emit_one_type` |
| FR-J47 | **Compiles and round-trips** | whole pipeline | **22 cases** compiled with Roslyn, deserialised, re-serialised and diffed |

---

## 6. SVG to XAML

| ID | Requirement | Implementation | Evidence (all automated) |
| --- | --- | --- | --- |
| FR-V01 | Seven shape elements | `Vector/SvgGeometry.cs` · `ShapeBuilder2D` | 6 tests incl. radius clamping and `ry` mirroring |
| FR-V02 | Structure, `viewBox`, `preserveAspectRatio` | `Vector/SvgParser.cs` | 6 tests incl. `meet`, `slice`, `none`, non-zero origin, CSS units |
| FR-V03 | Full path grammar | `Vector/SvgPathGrammar.cs` | 14 tests incl. implicit repeats, run-together numbers, compressed arc flags, arc→cubic, degenerate radii |
| FR-V04 | Every transform, composed | `Matrix2D` · `SvgParser.ReadTransform` | 5 tests incl. SVG composition order and stroke-width scaling |
| FR-V05 | Full paint model | `Vector/SvgPaint.cs` · `SvgStyle.cs` | 10 colour syntaxes + opacity nesting, dash conversion, fill rule |
| FR-V06 | Gradients incl. `href` inheritance | `SvgParser.ReadGradient` | 6 tests |
| FR-V07 | `clipPath` | `SvgParser.CollectDefinitions` | `Clip_paths_are_emitted_for_wpf_and_reported_for_winui` |
| FR-V08 | Two dialects, correct shapes each | `SvgConvertOptions.ShapesFor` · `XamlEmitter` | `WinUi_refuses_drawing_image…`, `Wpf_refuses_path_icon…` |
| FR-V09 | Unsupported features named | `SvgParser._unsupported` | 7 element types + `<style>` blocks |
| FR-V10 | **Two previews: source and output** | `SvgToXamlPage.xaml.cs` · `SvgImageSource` + `XamlReader.Load` · `Controls/CheckerboardPanel.cs` | **30-file corpus** × 2 dialects × every shape must parse |
| FR-V11 | Conversion report with reasons | `Vector/SvgNote.cs` · `SvgParser.UnsupportedDetail` · `SvgToXaml.Summarise` | Automated — every warning test asserts the sentence the report shows |
| FR-V12 | Merged-path output | `XamlEmitter.MergeGeometry` | `Merged_path_combines_every_geometry_and_warns…` |

---

## 7. API Profiler

| ID | Requirement | Implementation | Evidence |
| --- | --- | --- | --- |
| FR-A01 | Capture proxy records whole exchanges | `Capture/CaptureProxy.cs` · `Capture/HttpWire.cs` | Automated — `Forwards_a_request_and_records_the_whole_exchange`, `Records_a_posted_body` |
| FR-A02 | Proxy change attempted only where it can work | `Capture/SystemProxy.cs` · `CanChangeSystemSetting` · `CaptureSession.RoutesAutomatically` | Automated — `Reads_the_current_setting_without_changing_it`, `An_unpackaged_process_can_change_the_setting`; restore path manual (it alters the machine) |
| FR-A03 | Per user, not per process — said plainly | `ApiProfilerViewModel.ProxyNotice` · `ApiProfilerPage.xaml` banner | Manual |
| FR-A04 | Process attribution from the TCP table | `Capture/ProcessResolver.cs` | Automated — `Attributes_a_loopback_connection_to_the_process_that_opened_it` |
| FR-A05 | Certificate is its own consented step | `Capture/CaptureCertificates.cs` · `InstallCertificateCommand` | Automated — `Creating_a_root_does_not_trust_it`, `Issues_a_host_certificate_signed_by_its_root` |
| FR-A06 | Tunnel and pinned are distinct outcomes | `CaptureOutcome` · `CaptureProxy.HandleConnectAsync` | Automated — `A_tunnel_counts_its_bytes_without_keeping_them`, `A_pinned_client_says_so…` |
| FR-A07 | Port and thumbprint remembered once | `Services/CaptureConfigService.cs` | Manual — survives a restart |
| FR-A08 | Whole bodies retained | `CaptureProxy.ForwardAsync` · no cap | Automated — `Keeps_a_large_response_body_in_full` |
| FR-A09 | Crash recovery of the proxy setting | `CaptureConfig.Restore` · `App.RecoverCaptureProxyAsync` | Manual — kill the process mid-capture |
| FR-A10 | Whole exchange, hand-off, cURL | `ApiProfilerPage.xaml` detail pane · `SendToBuilderCommand` | Manual |
| FR-A11 | Credential values covered in the detail | `ApiProfilerViewModel.Redact` | Manual + code |

---

## 8. API Builder

| ID | Requirement | Implementation | Evidence |
| --- | --- | --- | --- |
| FR-A20 | Collections, folders, atomic writes, schema version | `Workspace/WorkspaceStore.cs` | Automated — 11 tests incl. corrupt file, newer schema, no temp left behind |
| FR-A21 | Every verb | `RequestFactory.NormalizeMethod` | Automated — `Every_verb_reaches_the_server` |
| FR-A22 | URL ↔ query table both ways | `BuildUrl` / `SplitQuery` / `StripQuery` | Automated — 5 tests |
| FR-A23 | Header table with enable/disable | `ApiBuilderViewModel.Headers` | Automated — `Headers_are_applied_and_disabled_rows_skipped` |
| FR-A24 | Five body kinds | `RequestFactory.BuildBody` | Automated — JSON, form, multipart, binary, missing-file failure |
| FR-A25 | Five auth schemes | `RequestFactory.ApplyAuthAsync` | Automated — 7 tests, all without a socket |
| FR-A26 | Variables with precedence + preview | `Workspace/VariableResolver.cs` | Automated — 11 tests incl. cycles and nesting |
| FR-A27 | Response viewer, hex dump for binary | `ApiBuilderViewModel.Present` · `HexDump` | Automated — `A_body_that_is_not_valid_text_reports_itself_as_binary` |
| FR-A28 | Per-request history | `ApiBuilderViewModel.History` | Manual |
| FR-A29 | cURL both ways, OpenAPI, Postman | `Interop/*.cs` | Automated — 45 tests |
| FR-A30 | **Secrets never in a file** | `CredentialVaultStore` · `AuthSpec.SecretRef` | Automated — `No_secret_from_the_export_is_carried_into_the_collection`, `Export_never_writes_out_a_secret` |
| FR-A31 | Hand-off to the other four tools | `IToolHandoffService` | Manual |

---

## 9. Non-functional

| ID | Target | Evidence |
| --- | --- | --- |
| NFR-01 | Cold start under 2 s | Manual — launches immediately |
| NFR-02 | Navigation under 100 ms | Manual |
| NFR-03 | Nothing blocks the UI thread | Every engine call goes through `ComputeAsync` onto the thread pool |
| NFR-04 | Under 300 MB with all six visited | **Verified — 241 MB** after opening all six |
| NFR-05 | Core has no network type | **Gate** — `build.ps1 -Task verify`, passing |
| NFR-06 | Nothing phones home | **Gate** — same task, passing |
| NFR-07 | 0 warnings, 0 errors | **Verified** — full solution build |
| NFR-08 | `TreatWarningsAsErrors`, nullable | `Directory.Build.props` |
| NFR-09 | Every engine has failure-mode tests | **527 tests, all passing** |
| NFR-10 | Windows 10 19041; x64 and ARM64 | `TargetPlatformMinVersion`; both platforms configured |

---

## 10. Global edge cases

| # | Edge case | Where it is asserted |
| --- | --- | --- |
| 1, 2 | Empty and whitespace input | `Empty_and_whitespace_input_produce_an_empty_result_not_an_error`; every engine |
| 3 | Over 5 MB prompts | `ExceedsInputGuardAsync` (both text bases) |
| 4 | Lone surrogates → U+FFFD + warning | `Unpaired_surrogate_becomes_replacement_char_with_a_warning` |
| 5 | BOM stripped | `Bom_is_stripped_before_parsing`, `A_bom_is_stripped_before_parsing` (SVG) |
| 6 | Mixed newlines normalised | `TextUtil` (ported, DevKit-tested) |
| 7 | Grapheme counting | `TextUtil.GraphemeCount`; `Astral_characters_survive_a_round_trip` |
| 8 | Control characters | `Raw_control_characters_inside_a_string_are_rejected` |
| 9–11 | Clipboard, cancelled dialog, locked file | Service-level; manual |
| 12 | Stale result never overwrites | Generation token in `ComputeAsync` |
| 13 | Cancel on navigate-away | `ToolViewModelBase.Deactivate` |
| 14 | Theme change re-themes everything | Manual |
| 15 | DPI / monitor change | Manual |
| 16 | Depth limit refused, not overflowed | `Nesting_beyond_the_depth_limit_is_refused_not_crashed` |
| 17 | Precision beyond `double` | `Numbers_round_trip_byte_for_byte` |
| 18 | SVG outside the subset | `Unsupported_elements_are_named_in_a_warning` |
| 19 | Distinct transport failures | `Each_transport_failure_gets_its_own_message` (5 cases) |
| 20 | Non-text body → hex dump | `A_body_that_is_not_valid_text_reports_itself_as_binary` |

---

## 11. Known gaps

Recorded rather than hidden.

Every functional requirement in `Docs/01` is implemented. What remains is a deliberate limit,
not an unfinished edge.

| Limit | Why | Impact |
| --- | --- | --- |
| Syntax colouring applies to **read-only** panes only | Colouring an editable surface in WinUI means a `RichEditBox`, which fights every keystroke and mangles pasted text. Colouring what the user reads and leaving what they type alone gets the whole benefit for none of that cost. | Input panes are monospace and plain. The four output panes and the response viewer are coloured. |
| Colouring is skipped above 200 KB | Building a `Run` per token for a very large document costs more than the colour is worth. | The document still displays, uncoloured, and scrolls normally. |
| The SVG converter's subset is bounded (`Docs/01 §4.4`) | "Support SVG" is unbounded; text needs font metrics, filters need a render graph. | Everything outside the subset is named in a warning rather than silently dropped. |
| Manual rows above are verified by exercising the packaged app, not by a UI test suite | A standing UI suite would cost more than it catches for a six-page app — the reasoning DevKit's test plan used, which holds here. The rows marked **Verified** were driven through UI Automation and screenshots during review, but that scaffolding is a scratch harness, not a committed suite. | The engines, where defects actually live, carry 527 tests. |

### Verified by running the packaged app

| Check | Result |
| --- | --- |
| `build.ps1 -Task all` | 390 + 137 tests pass · privacy gate passes · MSIX produced · **0 warnings, 0 errors** |
| All six tools opened via `devtools://tool/<id>` | Six tools, **one** process — single-instance redirection works |
| Per-tool state | A `state/<tool-id>.json` written for all six |
| Recents | All six recorded, newest first, de-duplicated |
| `Ctrl+F` in each pane | Editable pane selects and scrolls to the current match; coloured pane marks every match. Count, `Enter`/`Shift+Enter`, match case and `Esc` all correct in both themes |
| JSON result tree | Root opens filled; expand-all reaches the leaves; collapse-all returns to `$`; the selected row's JSONPath reaches the footer and the clipboard, brackets and all |
| Both themes | Light and dark checked by sampling the rendered window, not by eye — syntax colours, tree rows and the find strip legible in each |
| Options row placement | All six tools at 1550 and 1088 effective px. Wide: Formatter, Diff, JSON to C# and SVG ride in the title bar, no options band at all; Profiler uses the band; API Builder has no row. Narrow: every row in the band, nothing clipped bar the Profiler's status text |
| Title-bar commands | Favourite, reset, palette and theme at 46 × 32, on one centre line with the Tall caption buttons, in both themes |
| Launched build is the built build | `-Task run` verifies `InstallLocation` matches the output folder, closes a running copy first, and throws otherwise |
| Screenshot evidence is of the window under test | The harness pins the window handle, un-maximises before resizing, and composes from the captured width — three separate reasons it was previously photographing something else |
| Start-up with saved favourites | Shell reaches Home with all six tools; no `RPC_E_WRONGTHREAD` and no crash log. The favourites load finishes on a thread-pool thread and is marshalled back through `UiDispatcher` |
| Line numbers follow the text | Both panes: the editable one through the `TextBox`'s own scroller, the read-only coloured one through `ColourHost`. Scrolling a formatted result to line 34 shows 34 in the gutter |
| JSON Diff with two long documents | Inputs bounded and scrolling independently; the differences list renders (`9 added, 9 removed`) and scrolls |
| Side-by-side diff | Both columns level, each with its own line numbers; a member on one side only leaves the other half blank and does not advance its numbering; filters hide a kind and drop it from the count; `‹ n of m ›` scrolls to each difference and names it |
| A page that fails to load says why | `NavigationService` logs the exception instead of the click appearing to do nothing |
| Crash log | Empty after exercising every page |
| Memory | 241 MB with all six visited (target: under 300 MB) |
