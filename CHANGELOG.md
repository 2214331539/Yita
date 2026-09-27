# Changelog

## 0.8.4 - 2026-09-27

- 根据译文内容自适应浮窗宽高，减少长文本被圆角边缘遮挡，并移除译文高亮背景与加粗样式。
- 设置页改为左侧导航，分为常规、AI 记录、翻译与外观、模型配置四个入口，统一使用微软雅黑。
- 托盘菜单同步使用 Yita 暖白色界面、字体和字号，保持与主界面一致。

## 0.8.3 - 2026-09-27

- Anchor new popups to the lower-left of each mouse-drag region, independent of drag direction. Keep user offsets in DIP, reset the move lock when an unpinned window gets a new anchor, and preserve placement during streaming and pinning.
- Run external UI Automation reads in a persistent, hidden helper process. A native provider crash or timeout terminates only the helper. Skip that target's UIA provider for 60 seconds and try native/guarded clipboard fallback, even when the general clipboard option is off. An ordinary empty UIA result does not override that preference.
- Exchange selected text only through redirected pipes; the helper does not load settings or credentials, initialize the UI, or acquire the app's single-instance lock. Start it ahead of the first selection and terminate it when the parent exits.
- Add regression coverage for process exit, timeouts, recovery, Unicode IPC, drag direction, relative offsets, pinning and screen-edge clamping. Reported native GetText crashes are addressed by isolation; the friend's exact environment still requires validation.

## 0.8.2 - 2026-09-27

- Reduce the primary UIA budget to 120 ms for enabled WPS PDF compatibility. WPS then tries Ctrl+C directly instead of waiting for WM_COPY; readable accessibility selections still avoid the clipboard entirely.
- Retry keyboard copy once only when the first attempt made no clipboard change. Cancelled gestures, changed clipboard content and foreign foreground windows stop retries.
- Resolve focus from the foreground GUI thread; recognize owned WPS windows and dedicated PDF renderer processes. Accept clipboard owners only from processes attached to the target window tree.
- Handle delayed clipboard text rendering and multiple format writes by re-reading a stable sequence from the same owner. Never reuse unchanged clipboard text or restore over a foreign/newer owner.
- Add privacy-safe WpsCopySucceeded/WpsCopyEmpty events with copy duration in milliseconds. These timings exclude the remote translation response.
- Pass 445 automated tests, including delayed rendering, owner changes, stale clipboard and shortened WPS timeouts. Real WPS success rate and end-to-end speed still need user acceptance testing.

## 0.8.1 - 2026-09-27

- Add a default-enabled WPS PDF compatibility option. When UIA/native reads return no text, try WM_COPY followed by a guarded Ctrl+C transaction. Other applications require the existing clipboard fallback option. Reject focus changes, held modifier keys, password-style fields and clipboard content from another process.
- Expand popup dragging to the full grab strip and empty header. Remember selection-relative offsets across restarts, scale them for DPI and clamp new popups to the current monitor. Streaming updates no longer reposition or resize a manually moved popup.
- Stop late callbacks after popup closure and isolate recoverable streaming presentation failures to the affected popup. Record bounded application method names and inner exception types without text, credentials, messages or source paths. The reported external crash has not yet been reproduced; this is defensive hardening, not a confirmed root-cause claim.
- Set the Yita SDK baseline to .NET 8.0.425. Rewrite the root MIT license for Yita modifications and retain the upstream MIT text in LICENSES/InstantTranslate-MIT.txt in both source and distribution.

## 0.8.0 - 2026-09-27

### Yita

- Add a per-user Windows x64 Setup with the Yita mascot, ivory surfaces, teal headings and Chinese/English installation flow.
- Include the .NET 8 desktop runtime, runtime licenses and SHA256 manifests. Support reinstall, uninstall and a newer-version guard; preserve user settings, credentials and records on uninstall.
- Add pinned portable Inno Setup acquisition, reproducible build instructions and an isolated installer lifecycle check. The installer is unsigned; building does not publish a GitHub Release.
- Rename the application, executable, credentials and configuration identity to Yita; add the otter mascot and application icons.
- Simplify the translation toolbar to source/translation switching, pin and close. Reading the source no longer interrupts incoming translated text.
- Unify interface typography, spacing and controls; add short motion transitions that respect system animation preferences.
- Introduce the Lakeside palette with ivory reading surfaces, dark warm text and teal controls. Upgrade legacy default fonts and colors while preserving other preferences.
- Add regression coverage for reading-view switching and appearance migration. The local automated suite contains 419 passing tests; static previews do not claim live API or external-reader validation.
- Document portable local builds, current limitations, data handling and upstream attribution for the public repository.

The entries below are inherited upstream history, not independent Yita releases.

## 0.7.3 - 2026-09-09

### Fixed

- Reject incomplete or unsuccessful AI stream endings instead of marking truncated text as a completed translation, explanation or answer. Bound individual SSE lines and aggregate response text to protect against malformed responses, without changing normal output budgets or prompts.
- Defer iterator disposal until an in-flight stream read finishes after cancellation or timeout; retain its physical concurrency slot until cleanup completes, and observe late faults without exposing their content.
- Preserve unsaved translation corrections, selection and undo state when appearance settings refresh. Reject oversized translation-memory writes before replacing the readable file, skip invalid null entries, and report unreadable-memory saves through the existing recoverable error path.
- Make daily-record tests deterministic around midnight; the application's local-date grouping behavior is unchanged.
- Clarify that translation targets the selected passage, not the translator's instructions. Detect clear instruction-echo responses for unrelated source passages, fail them before successful completion/cache insertion, and bypass matching polluted cache/memory results and prompt examples without deleting user data. This conservative guard does not reject legitimate translation-related source material and is not a general semantic validator.
- Add an opt-in live translation regression mode for the reported Scratch assignment; it checks Chinese task content without saving the response or credentials.

### Changed

- Code analysis now requests compact, clearly separated sections plus an item-by-item abbreviation/naming guide: English full forms, Chinese meanings, likely naming rationale, and common usage conventions. Ambiguous names and conventional symbols must not be given invented expansions. The code-only output budget is 3072 tokens; ordinary translation, explanation and chat budgets are unchanged.
- Both Bubble 3.0 glass styles now place translation, explanation/code analysis, and Q&A/DeepSeek chat text on opaque rounded reading cards. Soft off-white gradients pick up a faint gray-blue (Glass) or gray-lilac (Color Glass) tint instead of stark pure white. The outer glass, toolbar, tail and Color Glass tints remain translucent.
- Inset the text viewports and scrollbars from the card corners, account for card padding during automatic sizing, and remove the now-unnecessary glyph halo. Returning from an explanation preserves the original selection and size.
- Older popup styles keep their existing backgrounds and spacing. High-contrast mode uses system colors instead of off-white cards; settings previews describe the new glass-shell/reading-card appearance.

### Validation and limitations

- Includes 416 passing automated tests plus offline popup, settings and application checks. No new real-network DeepSeek or external-reader regression is claimed for this release.
- Includes the existing Edge PDF recovery improvements. Zotero's built-in PDF reader compatibility remains unresolved; its old companion plugin is unchanged and may be rejected by Zotero 9.0.6.
- Windows x64 self-contained package remains unsigned. Existing settings, Windows Credential Manager entries and user records are not included in release assets.

## 0.7.2 - 2026-09-05

### Added

- Added optional Bubble 3.0 / Liquid Glass and Bubble 3.0 / Color Glass popup styles alongside Minimal, Bubble, and Bubble 2.0. Both preserve the sculpted 2.0 shape with translucent surfaces, thin optical highlights, and layered edge reflections. Color Glass retains 2.0's pink, cyan, and lilac palette; the original 2.0 style is unchanged.
- Matched translation controls and auxiliary Q&A surfaces to the new material. Decorations are input-transparent and do not change selection, drag, resize, or copy behavior.
- Glass rendering uses real per-pixel surface transparency with roughly 20–30% base tint opacity, shared frozen vector brushes, and no desktop capture, backdrop sampling, or continuous animations. Text remains opaque with a fine light glyph edge; high-contrast mode uses system colors without glass decoration.

### Fixed

- Update an already-open Q&A window's rounded clip when changing popup styles, even if the window dimensions stay the same.
- Hide the original translation's drawing and mouse input while explaining, retaining its document and selection for return. Transparent explanations use a single material layer instead of a milky stacked cover, preventing the translation from showing through the explanation.
- Include preview images in the Windows ZIP so the packaged README can display them offline.

### Validation and limitations

- Added settings, palette, theme-reset and actual WPF rendered-alpha tests; the complete suite now contains 373 passing tests. Translation/explanation/Q&A snapshots cover both glass styles, text selection, return behavior, resizing and system-color fallback.
- Glass uses actual transparency, not desktop capture, real-time background blur or physical refraction. Busy or very dark backgrounds can reduce text contrast; existing opaque styles remain available.
- Includes the existing v0.7.1 Edge PDF recovery changes. This appearance release does not add another Edge or Zotero fix; Zotero's built-in PDF reader compatibility remains unresolved and its existing plugin is unchanged.

## 0.7.1 - 2026-09-05

### Fixed

- Improved Edge PDF selection recovery after scrolling, zooming, or renderer changes. Accessibility activation is now window-scoped, rate-limited, and refreshable after an empty read instead of being attempted only once per process.
- Continue through fresh focused/document candidates when a hit or ancestor node has expired, while still rejecting password-protected paths before reading any text.
- Search current visible document branches near the selection instead of limiting fallback to the first eight document nodes. Embedded renderer candidates must have verified ownership of the target browser window.
- Cancel the underlying selection read when its time budget expires so later gestures can recover when the external provider responds to cancellation. Physical reader concurrency remains bounded.

### Diagnostics

- Distinguish an empty selection from cancellation or translation failure, and include privacy-safe selection and Edge recovery counters in performance diagnostics.
- Added 21 regression cases for detached nodes, visible/deep document traversal, renderer ownership, refresh timing, cancellation, and selection outcomes.

### Known limitations

- Zotero's built-in PDF reader remains unresolved. The existing companion plugin is unchanged and can be rejected by Zotero 9.0.6; this release does not claim to fix or validate Zotero selection translation.
- Automated tests cover the recovery logic, not every Edge build or PDF. Manual Edge PDF verification of the newly packaged v0.7.1 executable is still pending.

## 0.7.0 - 2026-09-04

### Added

- Added a compact question bar below completed translations and explanations. Each translation popup owns an independent multi-turn AI conversation window with streamed answers, stop, retry, copy, resize, zoom, pin, and close controls.
- Added a bottom-right DeepSeek quick-chat launcher to every translation popup. It opens one reusable, compact, topmost conversation window, streams concise answers in the question's language, and sends no translation context or AI-history records.
- Added opt-in AI history. Completed explanations and answers for the same selection are atomically stored in one schema-marked Markdown file under a user-selected directory; disabled, cancelled, failed, and partial results write nothing.
- Added on-demand AI summaries for today, the last seven days, or all schema-marked Yita records. Summaries are classified, streamed through the existing DeepSeek configuration, and written as new non-overwriting Markdown files.
- Added a loopback-only Zotero selection bridge and companion Zotero 7–9 plugin. It reads only the current built-in PDF-reader selection and does not access the Zotero library or system clipboard.
- Added a compact top-left size preset panel to translation, explanation, contextual Q&A, and DeepSeek quick-chat windows. It provides one-click small, medium, large, wide, tall, and square layouts while preserving manual edge resizing.
- Added an always-visible, directly draggable text-size slider to every translation, explanation, contextual Q&A, and DeepSeek quick-chat window. Translation and explanation share a live size, while each conversation window controls its own transcript.
- Added explicit `Record` actions to completed explanations, contextual Q&A, and DeepSeek quick chat. A click appends the complete valuable content to one schema-marked Markdown file per local day in the user-selected folder, even when automatic AI history is disabled.
- Added a `Code / 代码` action to completed translation popups. It treats the original selection as code, streams a Chinese analysis of the likely language, common use, meaning, important logic, alternatives, and caveats, and reuses the explain overlay, cancellation, copy, follow-up, and Record flows.
- Added AI-selected inline emphasis for translations, explanations, code analyses, contextual Q&A, and DeepSeek quick chat. DeepSeek can mark up to three short key phrases; the client safely renders three emphasis levels while keeping copied, recorded, and follow-up text clean.
- Added independent AI emphasis palettes in Settings: Clarity, Morandi, Ocean, Warm, and High contrast. Palette changes update open popups immediately without changing the main accent color or popup style.

### Changed

- Added lighter opaque mist surfaces for explanation and Q&A while preserving actual window opacity at 1 and retaining system colors in high-contrast mode.
- Isolated Q&A and explanation work from the foreground translation path with independent low-priority concurrency, cancellation, request versions, and timeout protection.
- Increased UI Automation traversal depth for Chromium/Adobe PDF trees, added a bounded top-level `Document` fallback, and allowed a longer non-clipboard stabilization window for Edge PDF selections.
- Added a short, reversible Edge accessibility activation pulse before PDF selection reads. This enables Edge's otherwise dormant UI Automation document tree without modifying browser shortcuts, injecting keys, or using the clipboard.
- Added a dedicated, generously sized top drag handle with hover feedback and enlarged native edge/corner resize hit targets with explicit resize cursors, keeping the translation text area selectable.
- Kept preset resizing anchored at the window's current position, clamped the result to the active monitor, replaced the `S / M / L` letters with progressively sized window-outline icons, and aligned all six presets plus the direct font slider in one compact row.
- Removed the extra `Aa` expansion step, updated large-type line spacing in conversation transcripts, and reserved enough automatic popup height for the persistent control panel without hiding translated text.
- Pinned local builds to the .NET 8 SDK family so a newer incomplete SDK installation cannot silently take over the WPF toolchain.
- Kept malformed or partial AI emphasis transport markers out of the visible text during streaming, so unfinished metadata can never leak into a popup, selection, clipboard copy, or Markdown record.

### Privacy

- AI history remains off by default and clearly identifies its files as plain, readable Markdown. Manual daily records are written only after an explicit `Record` click; the app refuses to overwrite a same-named foreign file and sends automatic-history records for summarization only after the user clicks AI Summary.

## 0.6.0 - 2026-08-28

### Added

- Added AI Explain for a completed translation. It can explain either the original selection or a selected passage in the translated text, streams a concise Chinese explanation, and keeps the result in the same popup.
- Isolated explanation requests from translation requests, with per-window cancellation, a low-priority concurrency slot, timeout protection, retry, copy, and return controls.
- Added an optional Bubble popup style in Settings. It uses an opaque pastel surface, soft shadow, rounded controls, and a tail that points toward the selection side while preserving the existing Minimal style as the default.
- Added Bubble 2.0 as a separate popup style, with a fuller rounded silhouette, attached soft tail, white highlight, and opaque pastel blue, lavender, and pink surface.
- Added a smooth local reveal queue for streamed translations and explanations: the first content remains immediate, then newly received text fills in progressively without waiting for the whole response.

### Fixed

- Clipped Bubble popup content and explanation overlays to the same rounded surface geometry so the two lower corners cannot expose square edges.
- Retried safe UI Automation/native selection reads after mouse-up for Edge, Chromium, and Zotero document readers, and accepted same-process focused document nodes that do not expose a native window handle. This improves PDF selection pickup without enabling clipboard fallback.

### Privacy

- AI Explain content is never written to settings, translation memory, caches, or diagnostics.

## 0.5.4 - 2026-08-23

### Fixed

- Moved the top and upper-corner resize targets from the detached action toolbar to the visible top edge of the translation surface.

## 0.5.3 - 2026-08-23

### Changed

- Dismissed an unpinned popup immediately after a physical left click outside that popup while keeping every pinned result intact.
- Enabled four-edge and four-corner resizing before pinning, with a larger resize hit target.
- Added content-aware initial sizing that grows with mixed Chinese/English text, explicit line breaks, and the selected font size while staying within the active monitor and retaining vertical scrolling for long results.

## 0.5.2 - 2026-08-23

### Fixed

- Moved hit testing and subscriber work out of the low-level mouse callback, serialized hook lifecycle changes, added session and 15-minute rebind recovery, and made the global hotkey recover after an unexpected message-loop exit.
- Isolated UI Automation reads by target process, bounded physical reads, enabled best-effort COM call cancellation, and continued to safe native fallbacks after target-provider faults.
- Stopped ordinary external clicks from canceling a translation, bounded duplicate in-flight waits, and ensured joined or empty-result failures have a request window that can display an error.
- Retried DeepSeek connection timeouts that arrive as uncancelled `OperationCanceledException`, classified stream disconnects, and retried a stream failure only before any content has been emitted.
- Loaded Windows credentials off the startup/UI thread and prevented delayed credential recovery or a stale settings dialog from deleting a valid API key.
- Released the single-instance claim before lengthy shutdown cleanup, added activation acknowledgement, and extended graceful takeover so immediate relaunch no longer leaves both processes closed.
- Fixed canceling a pinned popup when another transient popup exists, preserved the last complete result after a failed retranslation, and kept language reversal disabled until streaming completes.
- Enabled explicit Per-Monitor V2 DPI awareness, bounded popup size to the current work area, and allowed manually widened text content to use the full window width.
- Prevented unreadable encrypted translation memory from being overwritten and restored safe non-clipboard Scintilla selection reading through system messages.

### Added

- Added a small rotating, privacy-safe lifecycle journal plus UIA and popup counts to copied diagnostics. These records never include selected text, translations, API keys, endpoints, or paths.
- Added regression coverage for credential startup, UIA slot poisoning, network/VPN timeout recovery, stream disconnects, shutdown takeover, malformed settings, and corrupt translation memory.

## 0.5.1 - 2026-08-23

### Fixed

- Made global mouse capture recover automatically when Windows is still completing login, when the native hook exits unexpectedly, or when the user explicitly chooses the tray repair command.
- Kept the app resident and retried input capture instead of closing the whole application when the first hook binding is temporarily unavailable.

### Added

- Added a tray command to repair input capture without exiting the app.
- Added privacy-safe input-capture status to diagnostics: hook state and the time of the most recent physical mouse-button event only. It never includes selected text, translations, API credentials, endpoints, or paths.

## 0.5.0 - 2026-08-19

### Added

- Added process-local performance diagnostics for selection reads, queueing, first content, total latency, cache hits, and coalesced requests; reports deliberately exclude source text, translations, credentials, endpoints, and paths.
- Added duplicate in-flight request coalescing so identical concurrent translations share one provider request while the owner continues to stream.
- Added explicit correction editing in the popup and a DPAPI-protected translation memory containing only corrections the user chooses to save.
- Added exact saved-correction hits plus up to three bounded relevant examples for future requests, with a clear-all control in Settings.
- Added automatic Windows high-contrast palette handling and popup keyboard commands for correction editing, pinning, copying, and text sizing.
- Added optional Authenticode signing and signature verification to the release script without changing the unsigned default.

### Changed

- Added bounded exponential-jitter retries and a short circuit-breaker cooldown for transient DeepSeek network, timeout, rate-limit, and server failures.
- Increased the configurable translation font maximum from 30 to 34 and reduced repeated allocation during relevant-example matching.
- Classified provider errors explicitly so configuration and authentication failures do not trigger transient-failure recovery.

### Security

- Kept ordinary translations ephemeral; persistent memory is created only after an explicit correction save and is encrypted for the current Windows account.
- Limited saved memory to 200 entries and relevant provider examples to three pairs within a 2,400-character budget.

## 0.4.1 - 2026-08-19

### Changed

- Reduced the in-memory translation cache to 128 entries, about one million characters, and a 20-minute TTL while preserving instant repeat-translation hits.
- Replaced retained source text in cache keys with SHA-256 fingerprints, reducing memory retention and strengthening in-process privacy.
- Bounded multi-range UI Automation context reads to 3,000 characters in total and reused prepared glossary/mode/style data across cache and provider stages.
- Tuned pooled HTTP/2 connections and active-request keep-alive behavior for faster recovery after network or VPN changes.

### Fixed

- Explicitly release popup HWND hooks, rich-text documents, animations, and delayed feedback work when a popup closes, preventing avoidable memory growth during long sessions.

## 0.4.0 - 2026-08-19

### Added

- Added opt-in surrounding-paragraph context from UI Automation without using the clipboard.
- Added fast, balanced, and precise translation modes plus natural, formal, concise, academic, and technical writing styles.
- Added a local personal glossary with `source => preferred translation` entries; only terms matching the current selection are sent.
- Added cache isolation for context, mode, style, matching terminology, and prompt-version changes.
- Added separate first-content and streaming-idle timeouts within the existing total request budget.

### Changed

- Reworked DeepSeek input as structured, untrusted translation data so context and terminology remain separate from instructions.
- Delayed the initial popup until translated content arrives, removing the animated waiting state.
- Refined the popup into a quieter content surface with an on-demand text-size control and a unified source/translation copy group.
- Refined the neutral palette, spacing, corner relationships, accessibility names, and Settings presentation around a content-first hierarchy.

### Security

- Kept surrounding-context sharing disabled by default and documented exactly when extra text is sent.
- Continued to keep source text, translations, and context out of persistent history; glossary preferences are stored locally.
- Explicitly reject UI Automation password elements and native Edit controls carrying the Windows password style.

## 0.3.1 - 2026-08-13

### Changed

- Changed automatic selection reading to prefer UI Automation and direct Win32/RichEdit/Scintilla selection messages, so ordinary selections no longer modify the system clipboard.
- Made the WM_COPY compatibility fallback opt-in from Settings and documented its clipboard side effects for custom-rendered applications.

### Fixed

- Fixed automatic dragging/selection potentially replacing or altering content that the user had already copied.

## 0.3.0 - 2026-08-13

### Added

- Added an English-first interface with a one-click English / 中文 switch in Settings; the saved language also applies to popup prompts, tray commands, notifications, and validation messages.
- Added separate English and Chinese translation-font settings. English options include Times New Roman, Arial, Source Sans Pro, Georgia, Calibri, and Cambria; Chinese options include SimHei, Microsoft YaHei UI, SimSun, and KaiTi.
- Bundled Source Sans Pro under the SIL Open Font License so non-translation interface text is consistent even when the font is not installed on Windows.
- Added migration and localization coverage for old settings, invalid font values, aliases, and provider errors.

### Changed

- Made Times New Roman and SimHei the respective default English and Chinese translation fonts.
- Standardized popup controls on a 24×24 icon coordinate system with consistent 34×32 hit areas.
- Replaced ambiguous copy badges with readable source/translation S and T markers, and redrew the pin icon for clearer visual weight.
- Applied saved interface-language and translation-font changes to open popup windows after Settings is saved.

## 0.2.0 - 2026-08-13

### Added

- Added single-instance activation so repeated launches reuse the running app.
- Added `Ctrl+Shift+T` and a tray command to translate text the user copied manually.
- Added a process-local LRU translation cache with TTL and memory limits.
- Added a close button, visible copy feedback, saved default font size, a scrollable settings window, API connection testing, API-key clearing, version status, and an About entry.
- Added complete tests for cache, streaming throttling, single-instance coordination, secure Endpoint validation, UIA process boundaries, and selection fallback policies.

### Changed

- Coalesced streaming UI updates and incrementally appended translation runs to keep long output responsive.
- Refined the popup into a compact, solid action strip plus an opaque, light-gray, borderless reading surface with clearer interaction states.
- Reorganized settings into three restrained cards with consistent controls, a fixed save bar, and an independently scrollable content area.
- Applied the configured accent theme to popup actions, selection, feedback, and pin state without adding glass, shadows, or decorative effects.
- Kept an in-progress pinned translation independent from the next transient selection.
- Increased the translation timeout to 60 seconds and made the coordinator the single owner of request timeouts.
- Required HTTPS for remote API Endpoints while continuing to allow local HTTP development servers.
- Made settings writes use unique same-directory temporary files before atomic replacement.

### Fixed

- Fixed a timeout race that could leave the loading popup visible indefinitely.
- Fixed provider failures and unexpected cancellation causing a popup to disappear without an explanation.
- Fixed repeated full FlowDocument rebuilding and repeated popup positioning for every streamed token.
- Fixed read-only translation text showing an unusable Paste command.
- Fixed theme choices being saved but not reflected in the popup.
- Fixed pinned translations being canceled when a new selection began.
- Fixed duplicate launches creating multiple tray icons, hooks, requests, and potential API charges.
- Fixed manual popup resizing clipping long or large text instead of providing a bounded, vertically scrollable reading area.
- Fixed the no-activate window style preventing normal text focus, drag selection, selection highlighting, and keyboard copy after an intentional click.
- Fixed a fully opaque selection overlay hiding the selected glyphs; the highlight now remains clear while the text stays readable.

### Security

- Removed every automatic keyboard-copy path; the app never synthesizes `Ctrl+C`.
- Serialized automatic clipboard transactions, stopped clearing the clipboard before copy, limited `WM_COPY` to the hit window and its same-process root, and avoided overwriting third-party clipboard updates.
- Restricted UI Automation focused-element reads to the same process as the pointer hit target.
- Rejected selection gestures whose start and end points belong to different processes.

## 0.1.0 - 2026-08-10

- Added global mouse-selection translation through Windows UI Automation.
- Added low-latency DeepSeek streaming translation and automatic language routing.
- Added no-focus translation popups with copy, language switch, pin, drag, resize, and font controls.
- Added application, window, and system-tray branding.
- Added request cancellation, secure API-key storage, and automated tests.
