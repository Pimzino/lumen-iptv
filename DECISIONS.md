# Decisions

Running log of choices made where the spec is silent or the environment forced a deviation.
Newest entries at the bottom of each phase section.

## Phase 0 — Scaffolding

- **SDK 9, TFM net8.0.** The machine has only the .NET 9 SDK installed (9.0.314) with .NET 8
  runtimes present. All projects target `net8.0` / `net8.0-windows` (LTS) per spec; the newer SDK
  merely compiles them. `global.json` pins `9.0.100 rollForward latestFeature`.
- **FluentAssertions pinned to 7.2.2.** The 8.x line moved to a commercial license; 7.x is the last
  Apache-2.0 line and is functionally sufficient.
- **Central Package Management.** One `Directory.Packages.props` owns every package version;
  project files list references without versions.
- **Analyzer posture.** `AnalysisLevel=latest` with `CodeAnalysisTreatWarningsAsErrors=true` plus
  `TreatWarningsAsErrors=true` everywhere. `CA2007` (explicit `ConfigureAwait`) is promoted to
  error in the three library projects via per-directory `.editorconfig`; it is intentionally not
  enforced in the WPF app project, where continuing on the UI context is the point. `CA2016`
  (forward CancellationToken) promoted solution-wide.
- **Data-layer tests live in `Lumen.Core.Tests`.** The spec fixes exactly two test projects;
  Data tests sit in a `Data/` folder inside Core.Tests rather than adding a third project.
- **Timestamps are unix seconds (INTEGER).** Fast range queries for EPG, no timezone ambiguity in
  storage; domain models expose `DateTimeOffset` accessors.
- **Active profile pointer lives in `settings`** (global key), not an `is_active` flag column, so
  switching profiles is a single-row write.
- **XML docs:** documentation files are generated for the three libraries with CS1591 (missing
  doc) suppressed — public service interfaces get real docs by review rather than boilerplate on
  every model property.
- **Snake_case schema with Dapper's `MatchNamesWithUnderscores`** enabled once via module
  initializer in Lumen.Data.

## Phase 1 — Design system

- **Motion tokens are markup extensions, not resources.** WPF parses each merged dictionary in
  isolation, so a swap-in "animations off" dictionary cannot reliably reach storyboards compiled
  into other dictionaries. `{theming:Dur Fast|Slow|Shimmer}` and `{theming:Loop}` read a static
  flag captured from `SystemParameters.ClientAreaAnimation` in the App static constructor —
  when the OS disables animation, every duration bakes to zero and looping shimmers run once.
- **Dictionary set**: spec names Colors/Typography/Controls; shipped as Colors, Metrics (shape,
  spacing, sizes), Typography, Icons, and Controls (aggregator over Core/Lists/Surfaces parts).
  Token dictionaries are re-merged by each part because parse-time StaticResource requires it;
  the duplicated token instances are a few KB.
- **Iconography** is Segoe Fluent Icons glyph text (fallback Segoe MDL2 on Win10) — native,
  crisp at any DPI, no vector assets to maintain.
- **SkeletonBlock is a custom control**: a Border style cannot give each instance its own
  animated gradient brush (style setter values are shared), so the shimmer lives in a control
  template.
- **"Zero literals" interpretation**: theme dictionaries are the single home of literal values;
  view XAML consumes tokens only. Control-structural constants (toggle 40×22, caption button
  width, dialog shadow margin) live inside the theme files next to their templates.
- **Implicit styles** cover Button (→Secondary), ComboBox, ProgressBar, ScrollBar/ScrollViewer
  (6px overlay bars), ToolTip, and SkeletonBlock so nothing can render with default WPF chrome.
- **Screenshot harness**: `--gallery-shot [dir]` renders every gallery section plus the full
  window chrome to PNGs, fully offscreen. Used as the visual review gate for this and later
  phases. `--gallery` opens the interactive gallery.

## Phase 2 — Providers & data

- **Xtream ids are strings.** stream_id/category_id arrive as numbers or strings depending on
  the panel; flexible converters normalize everything to strings (URLs consume strings anyway).
- **Defensive JSON strategy**: source-generated context with `AllowReadingFromString` globally,
  purpose-built converters on the notoriously dirty fields (exp_date, ratings with `7,5`
  decimals, backdrop_path as string-or-array, episodes as object-or-array-of-arrays), and
  per-element array parsing that logs and skips malformed items instead of failing the call.
- **Short-EPG base64 heuristic**: titles are decoded only when the payload has valid base64
  alphabet + padding *and* decodes to printable UTF-8 — plain titles that merely look base64-ish
  ("NEWS") survive untouched.
- **`vod_items` cache table added** (migration 0002) beyond the spec's table list: Home's
  "recently added", search-under-150ms, and offline browsing all need a local VOD catalog.
- **Snapshot sync keeps row ids stable**: channels match on provider_stream_id → stream_url →
  name; categories on provider_category_id. Favorites and EPG mappings survive refreshes.
- **SQLite parameter limit**: IN-clause queries chunk at 400 ids.
- **Perf gate recorded**: 1M-programme synthetic XMLTV imports in ~39s end-to-end (including
  generating the ~180MB fixture) with peak working set < 300MB. Batched transactions of 5,000
  rows via prepared commands.
- **Coverage gate recorded**: Lumen.Providers at 93.2% line / 82.1% branch (155 tests).

## Phase 3 — Shell, onboarding, profiles

- **Provider interfaces live in Lumen.Providers** (IXtreamClient, IM3uPlaylistParser, IXmltvParser):
  the spec's dependency rules let App reference Providers, and Core stays dependency-free. The one
  shared contract is `IEpgImportSink` (Providers produce, Data consumes) — it lives in Core.
- **Converters are a theme dictionary** (`Lumen.Converters.xaml`), not App.xaml inline resources:
  StaticResource inside a compiled ControlTemplate resolves against the owning dictionary's
  parse-time chain, not the runtime tree, so shell templates must merge the converters themselves.
- **Navigation is ViewModel-first** with per-page CancellationToken: NavigationService cancels the
  previous page's token on every navigation; page VMs implement `INavigationAware`.
- **Page view models are transient; shell/services are singletons.** Profile switches re-navigate
  Home for a clean reload.
- **E2E gate is automated**: `tools/Lumen.DevServer` (dev-only, deliberately outside Lumen.sln)
  emulates an Xtream portal + M3U playlist + XMLTV feed; `Lumen.exe --db <tmp> --e2e <server>`
  drives the real add-profile → catalog sync → EPG import path headlessly (DPAPI included) and
  `--e2e-verify` proves restart persistence. Phase-3 results: E2E PASS (5 channels/2 movies/1
  series per profile kind, 240 programmes each, 5/5 tvg-id mappings, now/next live on all 5),
  VERIFY PASS (2 profiles, 10 channels, 480 programmes, credentials decrypt after restart).
- **`--shot-shell <dir>`** captures Home/Settings/placeholder/onboarding screens off-screen for
  design review — reused as the visual gate in later phases.

## Phase 4 — Live TV + Player

- **One LibVLC + one MediaPlayer + one VideoView for the process.** Preview pane, full player,
  and mini player are `VideoSurface` Decorator slots; `PlaybackService` reparents the single
  VideoView between them (`Decorator.Child`), so preview → full → mini never recreates the
  player — seamless handoff per spec §6.
- **Overlay lives in `VideoView.Content`.** The LibVLC WPF VideoView renders in a separate
  airspace; its `Content` is the only WPF layer that composites above the video, so the player
  overlay is installed there once by the shell (`PlaybackService.SetOverlay`).
- **RenderTargetBitmap can't capture the player.** Airspace video + overlay don't appear in
  window snapshots — so the player is gated by the automated `--e2e-play` runner (real
  PlaybackService against fixture streams) rather than a screenshot.
- **Reconnect bug found and fixed by instrumented E2E, not by inspection.** First cut: the
  success path cancels *and disposes* the reconnect CTS from the Playing handler, after which the
  loop touched `cts.Token` → ObjectDisposedException swallowed the recovery. Fix: snapshot the
  token before the loop. Also added an explicit `player.Stop()` before each `Play()` because
  LibVLC ignores Play() from the Ended state. Backoff is 1/2/4/8/8s, max 5 attempts (spec §4.4).
- **libVLC events marshal to the dispatcher; commands run off it.** Native callbacks never touch
  WPF state directly (`OnUi`); `Play`/`Stop`/`SetAudioTrack` run on the thread pool so the UI
  thread never blocks on native calls.
- **Gate results**: `--e2e-play` PASS (play→Playing, zap to next channel, forced drop →
  Reconnecting attempt 1 → recovered, clean stop). `--scroll-bench` PASS — **10,000 channels,
  12 realized containers** (recycling), median 9.0ms / p95 12.6ms frames (60fps budget 16.7ms),
  2/200 frames over budget (initial realization). Live TV three-pane screenshot reviewed.
- **.ts / .m3u8 containers**: `XtreamUrls.Live` builds either from the profile's PreferHls flag
  (unit-tested); `PlaybackService.ResolveStreamUrl` honors it. M3U channels play their raw URL
  with per-channel user-agent/referrer options applied to the libVLC Media.
- **Immersive chrome**: the full player collapses the custom title bar via
  `LumenChrome.IsImmersive` (caption height → 0) so video is truly edge-to-edge; fullscreen (F)
  maximizes the window and restores the prior state on exit.

## Phase 5 — EPG Guide

- **`EpgGuidePanel` draws, it doesn't realize.** The spec forbids an ItemsControl-in-ItemsControl.
  Instead the panel is a single `FrameworkElement : IScrollInfo` that renders only the programmes
  intersecting the viewport directly with `DrawingContext` — zero child visuals, so panning cost
  is O(visible programmes) regardless of catalog size. Programmes per row are binary-searched by
  stop time to find the first visible block.
- **Ruler and gutter are pinned inside the same panel.** Rather than sync three scrollers, the one
  panel draws the scrolling grid first (clipped), then paints the time ruler (top) and channel
  gutter (left) over it at fixed positions — they can never desync because they share one render.
- **Both axes virtualize.** `IScrollInfo` reports the full extent (7 days wide × N channels tall);
  `OnRender` offsets by the scroll position and clips to the grid rect. Wrapped in a plain
  `ScrollViewer CanContentScroll=true`.
- **Gate results**: `--guide-bench` PASS — **500 channels × 7 days = 96,000 programmes**, panned
  both axes at median 8.3ms / p95 12.6ms (60fps budget 16.7ms, 1/300 frames over). Timezone gate:
  `20260704013000 +0530` and `20260703120000 -0800` both resolve to unix 1783108800, identical to
  the `+0000` reference — offsets are applied correctly. Live guide screenshot reviewed.
- **Background refresh** is a `BackgroundService` (`EpgRefreshScheduler`) that checks every 5 min
  and refreshes when the configured interval has elapsed since the last per-profile refresh,
  sharing the `IEpgSyncService` lock so it never overlaps a manual refresh.
- **Channel mapping UI** in Settings lists unmatched channels with a guide-channel ComboBox;
  selections write manual mappings (which survive automatic remapping).
- **`PlaybackServiceNavigator`** is the seam that lets Guide/Search/Home start playback without
  depending on the Live TV view model.

## Phase 6 — VOD: Movies & Series

- **`VirtualizingWrapPanel` must be hosted by a ListBox, not a bare ScrollViewer+ItemsControl.**
  `IScrollInfo` on a custom panel is only wired up when the hosting control's template contains a
  `ScrollViewer CanContentScroll=true` that binds to the panel — ListBox does, a raw ItemsControl
  does not. First cut used ItemsControl and rendered blank; switched to ListBox with a chrome-less
  `Lumen.PosterContainer` item style. (Found via screenshot review, not compile errors.)
- **VOD playback extends PlaybackService, doesn't fork it.** `PlayVodAsync` sets `IsVod`, seeks to
  the resume position on the first Playing event, and a 1s DispatcherTimer tracks position →
  `watch_history`. Position is saved on pause, stop, and navigation. >95% watched is stored as
  position 0 so a finished movie doesn't nag to resume its last seconds.
- **Movies and Series share `VodLibraryViewModel`** (category sidebar, sort, infinite-scroll
  paging at 120/page, favorites) with a two-line subclass fixing the content kind; the detail page
  (`VodDetailViewModel`) branches movie vs series (seasons/episodes) at load.
- **Navigation gained a back stack** for detail pages (`GoBack`); rail navigation clears it so Back
  stays page-local.
- **"Style can't set Style".** WPF forbids a `Setter Property="Style"` inside a Style; the
  play/start-over button became two visibility-toggled buttons instead. (Found via runtime XAML
  exception in the crash log.)
- **Gate result**: `--e2e-resume` PASS — play movie → detect 1200s duration → seek to 600s → stop
  (position 600s written to watch_history) → relaunch with resume → stream seeks back to 600s.
  Movies grid + detail page screenshots reviewed.

## Phase 7 — Search, Favorites, Home

- **Search performance: bound the EPG scan, don't index-hunt.** First cut of programme-title search
  ran **22 seconds** on a 200k-programme catalog (unindexed `channel_epg_map.xmltv_id` join + full
  `title LIKE '%…%'` + GROUP BY/ORDER BY that couldn't stop at LIMIT). Fixes: migration 0003 indexes
  `channel_epg_map(xmltv_id)`, and the programme search is bounded to a 48-hour window
  (`start_utc < now+48h`) so the title scan touches ~thousands of rows, not the whole table. Result:
  **22s → 39ms median** (`--search-bench` gate: 150ms). Channel/VOD searches use the existing
  name indexes.
- **Dapper + positional records + enums + NULL columns don't mix.** Materializing `SearchHit`
  (a record with a `ContentKind` first param and untyped `NULL AS Subtitle` columns) failed
  constructor matching. Query into a mutable `SearchHitRow` DTO (property-name mapping, which
  converts int→enum and NULL→null) and map to the record.
- **Search is 300ms-debounced**, min 2 chars, cancels superseded queries; Ctrl+K (wired in Phase 3)
  navigates here and focuses the box.
- **Home rows each own their query**: Continue watching (watch_history with 2–95% progress),
  Favorite channels (favorites ∩ channels, batch now/next), Recently added movies/series. Every row
  hides itself when empty; the whole page shows a designed empty state until the library fills.
- **Gate result**: `--search-bench` PASS (39ms median / 41ms max over 10k channels + 20k VOD + 200k
  programmes). Home / Search / Favorites screenshots reviewed — all render real data.

## Phase 8 — Polish & hardening

- **Ambient glow = sampled dominant color, not a static accent.** `AmbientColor` downsamples to
  16×16, averages saturation-weighted pixels (so black bars don't dominate), and normalizes to a
  consistent lightness band so every glow reads the same weight. `AmbientGlow` attached behavior
  turns it into a 12%-opacity radial wash that fades in off-thread. Applied to the player's
  now-playing bar and zap banner, bound to the current channel logo. `--glow-probe` PASS
  (crimson→hue 357, teal→hue 184, near-black→graceful fallback, both normalized to L=0.45).
- **Crash handling never shows a raw .NET window.** `DispatcherUnhandledException` is handled
  (app keeps running) and surfaces a styled `CrashDialog` (LumenDialogWindow) with the message and
  an "Open logs" button; a re-entrancy guard prevents stacked dialogs if faults cascade. Startup
  failures still fall back to a MessageBox (no themed window exists yet at that point).
- **`DispatcherHangMonitor`** (`[Conditional("DEBUG")]`) pings the dispatcher every 50ms and logs
  stalls over the 50ms budget — enforcing the "no UI stall >50ms" quality bar in dev without any
  Release cost.
- **Motion respects the OS.** `SystemParameters.ClientAreaAnimation` is captured before any XAML
  parses; when animation is disabled every `{theming:Dur}` bakes to zero and looping shimmers run
  once.
- **Empty + error states everywhere**: every list (Live TV, Guide, Movies, Series, Favorites,
  Search, Home) has a designed empty state; the player shows an inline error with Retry (never a
  modal over video); toasts cover transient success/failure.
- **Soak gate**: `--soak` zaps channels every 1.5s, sampling managed heap + working set. The full
  **30-minute run PASSED**: 1,192 zaps, managed heap net growth **0.0MB** (healthy GC sawtooth
  2.4↔8.4MB), working set 117.8MB → ~127MB plateau (**8.6MB growth**, flat over the final 10
  minutes). No leak under aggressive media churn. (soak-30min.txt)
- **Serilog review**: a full navigation of every page produces zero warnings/errors in the log.

## Phase 9 — Packaging

- **Self-contained single-file win-x64.** Managed assemblies fold into `Lumen.exe` (72.6MB, R2R);
  LibVLC's native tree stays on disk beside it (`IncludeNativeLibrariesForSelfExtract=false`)
  because its plugins are a large flat DLL set that self-extract would bloat and slow.
- **Trim foreign LibVLC RIDs.** `VideoLAN.LibVLC.Windows` ships x64 + x86 + arm64 (~240MB); a
  post-publish MSBuild target deletes the non-matching RID folders, cutting the payload from
  **362MB → 183MB**. Verified the trimmed exe still runs (`--scroll-bench` PASS).
- **Installer: Inno Setup, not MSIX.** Justified inline in `build/Lumen.iss` — a self-contained
  .NET + native-LibVLC payload is a flat file set that Inno packages into one offline,
  signing-optional installer without MSIX's packaging graph / virtualized FS / store-signing
  overhead. Compiles to **`Lumen-0.1.0-setup.exe`, 97.8MB** (LZMA2 over the 183MB payload).
- **Icon** is generated (`tools/make-icon.ps1`) into `lumen.ico` — a rounded accent-gradient mark
  with the signature glow highlight and a play triangle — and wired via `ApplicationIcon` +
  `SetupIconFile`.
- **`build.ps1 -Package`** runs publish + installer end to end.

## Post-review hardening

An adversarial correctness review of the concurrency-heavy code (`PlaybackService`, `EpgGuidePanel`,
`VirtualizingWrapPanel`) surfaced four real bugs, all fixed and re-validated (e2e-play, e2e-resume,
Movies-grid render, clean log):

1. **`VirtualizingWrapPanel.OnItemsChanged` blanked the grid on item Remove/Replace.** It removed
   *all* realized containers without telling the generator, which still considered them realized —
   so the next measure regenerated nothing and the grid went blank (and `Move` fell through
   entirely). Fixed to the canonical pattern: remove only the affected UI containers via
   `args.Position`/`args.ItemUICount` for Remove/Replace/Move; full clear only on Reset (where the
   generator resets its own map).
2. **`PlayChannelAsync`/`PlayVodAsync` re-checked the request sequence only *before* the one real
   suspension point.** Fast zapping or Stop-during-open could interleave `Stop`/`Play` on the shared
   player, land on the wrong channel, or fire a stale `ChannelChangedMessage`. Fixed by serializing
   all native `Play`/`Stop` through a `SemaphoreSlim`, re-checking the sequence *inside* the gate
   before `Play`, and only sending the channel-changed message if still current.
3. **`EnsureInitializedAsync` had a check-then-act race** that could build two `LibVLC`/`MediaPlayer`
   instances (double audio + leaked native resources) on concurrent first plays. Fixed by caching a
   single init `Task` under a lock.
4. **VOD end/error was swallowed** — both routed to `HandleStreamFailure`, which early-returns for
   VOD (no `_currentChannel`), so a failed movie hung at Buffering forever and a live `EndReached`
   was wrongly treated as a drop. Split into `HandleVodEnded` (save "completed", go idle) and
   `HandleVodError` (inline error state), with `HandleStreamFailure` now guarding `IsVod`.

## Post-release hotfix — full-player chrome crash

The first real end-user session (real Xtream data) crashed the moment the user entered full-player
mode on a channel, with `InvalidOperationException: Cannot set a property on object
'System.Windows.Shell.WindowChrome' because it is in a read-only state`.

**Root cause.** The window's `WindowChrome` is declared as a `Setter.Value` in the `Lumen.Window`
style (`Lumen.Controls.Surfaces.xaml`). WPF shares and **freezes** style setter values, so the
chrome instance applied to the window is read-only. `LumenChrome.OnIsImmersiveChanged` then mutated
`chrome.CaptionHeight` in place to collapse the caption drag zone for edge-to-edge video — throwing
on the frozen object. Both directions were affected: entering full player (`IsFullPlayerActive` →
trigger → `IsImmersive=True` → caption 0) *and* exiting (trigger deactivates → caption 48).

**Fix.** Clone the frozen chrome to a modifiable copy (`CloneCurrentValue()`), set `CaptionHeight`
on the clone, and re-apply via `WindowChrome.SetWindowChrome`. An equality short-circuit avoids
re-applying when nothing changed. (`LumenDialogWindow` was never affected — it assigns its own
per-instance, unfrozen chrome in code.)

**Why the gates missed it — and how that's now closed.** This exact crash first fired at 03:27
during headless `--shot-shell`, which used to enter full player to capture a player screenshot.
Instead of fixing the root cause then, the full-player step was dropped from the shot routine — so
every subsequent gate ran green with a live crash on the primary user action. That was the real
process failure: a diagnostic was weakened to pass rather than the bug fixed. Closed by a new
headless gate, `--e2e-fullscreen` (`FullPlayerRunner`), which shows the real window so the Style and
chrome trigger are live, then enters → exits → re-enters full player and fails on any exception. It
reproduces the frozen-chrome crash on the old code and passes on the fixed code
(`FULLSCREEN-RESULT=PASS`). Added to the standard verification set alongside `--e2e-play`.

**Also hardened.** The same session's logs showed `VirtualizingWrapPanel` `NullReferenceException`s
when `ItemContainerGenerator` is transiently null during template application (`RealizeVisibleItems`
/`CleanupChildren`/`ArrangeOverride`). Guarded all three to bail out cleanly until the generator
exists; measure re-runs once it does. Re-verified: `--scroll-bench` still virtualizes 10k channels
to 12 realized containers.

## Post-release hotfix — animation namescope crash + Settings freeze

Two more issues surfaced in real use, both fixed and gate-verified.

**Animated templates crashed on render.** Entering full player with a stream still connecting threw
`'ArcRotate' name cannot be found in the name scope of 'System.Windows.Controls.ControlTemplate'`
(and the skeleton shimmer threw the equivalent for `ShimmerShift`). Root cause: a `Storyboard`
started from an **`EventTrigger` on `Loaded`** that reaches its target via `Storyboard.TargetName`
pointing at a **nested** element inside a template/DataTemplate namescope. That specific combination
resolves unreliably (the property-trigger hover/press animations elsewhere in the design system use
`EnterActions`/`ExitActions` and were never affected — which is why the app otherwise ran fine). Fix:
give each such animation no `TargetName` at all and run it from the animated element's **own**
`Triggers`, targeting `(UIElement.RenderTransform)…` — no namescope lookup. Applied to the spinner
(`Lumen.Spinner`), the quick-list and guide-flyout slide-ins, and the toast slide-in; the skeleton
already targeted its template **root** (which does resolve) so it kept a property-path form.

This was the same *class* of miss as the chrome crash: the animations only fault once **rendered**,
and the existing gates ran headless without a visual tree. The `--e2e-fullscreen` gate now shows the
real window and additionally renders a spinner + skeleton offscreen, fires a real toast, and forces
the reconnect banner — hooking `DispatcherUnhandledException` (via a new `App.SuppressCrashDialog`
flag) so *any* dispatcher fault, not just a synchronous throw, fails the run. It reproduced the
`ArcRotate` crash on the first fix attempt (targeting the named `Path` still failed) and drove the
switch to the self-targeting pattern.

**Settings froze on large playlists.** The channel-mapping list was a plain `ItemsControl` (default
non-virtualizing `StackPanel`), so with a large playlist it realized a `ComboBox` per unmatched
channel up front — thousands of control trees built synchronously on the UI thread. Fix: virtualize
it (`VirtualizingStackPanel` + `ScrollViewer.CanContentScroll` + recycling), mirroring the Live TV
list. New gate `--settings-bench` loads the real `SettingsView` with **10,000** rows and asserts a
small realized-container count: it now realizes **11** ComboBoxes (was 10,000), populates in 19 ms,
and lays out in ~0.7 s.

## Post-release hotfix — streams 403 without the right User-Agent

Symptom: no channel (live or VOD) would connect, though the same streams played in the user's mobile
IPTV app. The catalog and EPG loaded fine, so credentials and the base URL were correct — the failure
was at the stream layer, and the app captured none of LibVLC's own output, so it was opaque.

**Diagnosis by evidence, not guessing.** Added `--probe-stream` (`StreamProbe`): it resolves the
current profile's first live URL (and HLS variant), turns on LibVLC logging, and plays it through a
matrix of options — default, five User-Agents, `http-reconnect`, HLS — reporting which reach
`Playing` and the error for the rest (credentials redacted). Against the real provider it was
unambiguous: **every request returned `HTTP/1.1 403 Forbidden` except User-Agent
`IPTVSmartersPlayer`, which played.** The panel whitelists the IPTV Smarters client UA and rejects
everything else — exactly why the phone app worked and the desktop app didn't. VOD was worse: the app
set **no** User-Agent on VOD media at all.

**Fix.** A stream now always presents a User-Agent: an explicit per-channel UA (from an M3U
`#EXTVLCOPT`) wins, then a per-profile override, then the app default `IPTVSmartersPlayer` — applied
to **both** live and VOD. The default is applied even to existing profiles (no re-onboarding). Added
a `stream_user_agent` column (migration `0004`, additive) and a **Settings → Playback** field so
users whose panel wants a different identity can change it. The probe confirms the real
`PlaybackService` path end-to-end: `appPath=PLAYING (default UA "IPTVSmartersPlayer")`.

**Also permanent.** LibVLC's warning/error log (HTTP status, TLS, demux) is now forwarded into the
app log, so the next stream issue shows a reason (e.g. `http: HTTP/1.1 403 Forbidden`) instead of a
silent `EncounteredError`.

## Post-release: player controls + picture-in-picture window

Four player issues, three sharing one root cause.

**Root cause of "controls never reappear" (full screen) and "no controls on hover" (mini).** The
player overlay is installed as `VideoView.Content`, which LibVLCSharp.WPF renders in a **separate
layered (`AllowsTransparency`) top-level airspace window** floating over the native video. In a
layered window, fully-transparent (alpha-0) pixels are **click-through at the OS level** — so once
the chrome faded, the entire overlay was transparent and the airspace window stopped receiving mouse
input, freezing `MouseMove` (full) and `IsMouseOver` (mini) permanently. Fix: a design token
`Lumen.Brush.Overlay.HitTarget` = `#01000000` (alpha 1/255, invisible) on the overlay's full-bleed
layers keeps the airspace window hit-testable in every state. This single change restored the
full-screen auto-hide/re-show, the mini hover controls, and reachability of the already-existing
Expand button (restore-to-full).

**Picture-in-picture as its own window.** The mini player was an in-window `Border`; it's now a
separate `MiniPlayerWindow` (`Controls/MiniPlayerWindow.cs`) — borderless, `Topmost`, not in the
taskbar, `ShowActivated=false` (never steals focus), `AllowsTransparency=false` (required so the
native video HWND renders). It reuses the existing surface model unchanged: it hosts a
`VideoSurface Kind=MiniPlayer`, and the shared `VideoView` reparents into it via the normal
register/activate path. `MainWindow` observes `PlaybackService.IsMiniPlayerActive` and shows/hides
the window (closing it on shutdown so it can't orphan). The window is **not** `Owner`-ed to
MainWindow, so it stays visible while the user minimizes Lumen and works. Drag and resize are driven
from the overlay chrome (a top drag bar + a bottom-right grip): mouse-capture + `GetCursorPos` deltas
(DPI-corrected via the window's `TransformFromDevice`) move/resize the floating window, keeping 16:9;
the window is located via the ForegroundWindow's `Owner` with a deterministic fallback to the one
live `MiniPlayerWindow`. `✕` stops playback, the expand button (and double-click) returns to full.

**Verification.** The `--e2e-fullscreen` gate now also asserts the PiP window structurally:
`pipWindow present=True topmost=True taskbar=False visible=True separate=True`, and that re-entering
full hides it. Build/tests/all gates stay green. The **airspace-dependent interactions** (hover
re-show over live video, and whether LibVLCSharp keeps the overlay visually synced while the floating
window is dragged/resized) can only be confirmed with real video in the GUI — flagged for hands-on
verification, to iterate if the ForegroundWindow lags on move/resize.

## Player rework — window controls, true fullscreen, and a native-chrome PiP

The first PiP attempt reused the shared airspace overlay for the mini controls; that fails because
the PiP window is `Topmost` and LibVLCSharp's overlay (ForegroundWindow) is not, so the overlay
paints *behind* the PiP video — no controls, no drag, no pin. **WPF can only paint above a hosted
video HWND from a separate top-level window** (a `Popup` or the ForegroundWindow), so the PiP now
carries its own chrome:

- **PiP controls live in a `Popup`** inside `MiniPlayerWindow` (`AllowsTransparency=False` on the
  window so the video HWND renders; the `Popup` is transparent and paints above it). It holds the
  drag bar, **pin/unpin** (toggles `Topmost`, starts pinned), close (`✕`→stop), center play/pause,
  expand→full, and a 16:9 resize grip. Controls **auto-hide** via a cursor-position poll
  (`GetCursorPos` vs the window rect — the video is a child HWND, so window `IsMouseOver` never fires
  over it). Drag/resize move the window (`GetCursorPos` deltas, DPI-corrected); the Popup is nudged
  on `LocationChanged`/`SizeChanged` to stay glued (Popups don't follow their target otherwise). The
  shared overlay's mini section was removed.

- **Windowed player vs true fullscreen.** Immersive mode was tied to `IsFullPlayerActive`, which hid
  the window's min/max/close during normal playback. Now the windowed player keeps real
  **minimize / maximize / close** buttons in its own top bar (new `PlayerViewModel` commands →
  `SystemCommands.*(Application.Current.MainWindow)`; the maximize glyph tracks `IsWindowMaximized`).
  **True fullscreen** is entered only via the fullscreen button/`F`: `MainWindow` goes borderless,
  `Topmost`, and sizes to the **full monitor rect** (`MonitorFromWindow`+`GetMonitorInfo`) — the old
  code just maximized, which stopped at the work area and **left the taskbar showing**. Gate asserts
  `fullscreen topmost=True … covers=True`.

- **Back (←) returns to browsing.** `ExitFullPlayer(bool)` became `ExitFullPlayer(PlayerExitMode)` —
  `Browse` (hide overlay, hand video to the preview surface, keep playing), `MiniPlayer`, `Stop`. ←
  → `Browse`; the transport mini button → `MiniPlayer`.

The `--e2e-fullscreen` gate additionally asserts the true-fullscreen bounds cover the monitor and the
PiP Popup realizes its command buttons (`pipControls buttons=4 ok=True`). Interactive feel — Popup
hover/drag/resize glue over live video — still needs hands-on GUI verification.

**Corrections after first hands-on pass.** (1) Adding window buttons to the player bar *and* leaving
the app title bar visible produced two sets of min/max/close. Per "only go immersive when clicking
fullscreen," the windowed player keeps the **app title bar** as the single source of window controls;
the player-bar duplicates were removed (only an on-screen exit-fullscreen button remains, shown when
true fullscreen hides the title bar). (2) `Back` was changed to `Browse`, which removed the way into
PiP; reverted so **Back drops to the mini player** (reveals the page behind *and* floats the PiP),
matching the prior behaviour. `PlayerExitMode.Browse` is retained but currently unused.

**Corrections after second hands-on pass (PiP interior).** (1) **No sound** — the mini player kept
the muted preview context; `ExitFullPlayer(MiniPlayer)` now clears `_isPreviewContext` and re-applies
mute, and `LiveTvViewModel` no longer starts a muted preview while the full/mini player is active
(that was stealing the shared surface and muting it). (2) **Close/expand didn't respond** — pin (a
code-behind `Click`) worked but the command-bound buttons didn't: binding into the airspace Popup is
unreliable for nested content (`--e2e-fullscreen` showed only 1 of 3 command bindings resolved even
with the VM DataContext present). So the PiP's commands and dynamic display (title, play/pause glyph,
time, seek) are now **driven entirely from code-behind**, not bindings. (3) Added the requested
**transport**: play/pause, stop, and — for VOD — a seek slider + elapsed/total time (updated on a
200 ms tick; seeking suspends the tick and calls `Seek` on release). Drag is suppressed when the
press lands on a control. Gate: `pipControls buttons=5 clickButtons=4 vm=True`.

**PiP entry/exit refinements.** After hands-on, the Back/PiP roles were separated cleanly: **Back
(←) returns to the list** with the muted list-preview (`ExitFullPlayer(Browse)`), and a **dedicated
picture-in-picture button** next to Back opens the floating window (`ToMiniPlayer` →
`ExitFullPlayer(MiniPlayer)`); the redundant transport-bar mini button was removed. Dismissing the
PiP now **brings the shell forward** — `MainWindow.SetMiniPlayerVisible(false)` restores the window
if minimized and calls `Activate()`, so closing (stop) or expanding (→ full player) no longer leaves
the main window stranded behind another app.

**VOD seek timeline in the full player.** The full overlay had no way to scrub a movie/episode —
live got the read-only EPG programme progress, VOD got nothing (only the PiP window had a seek
slider). The bottom scrim gains a VOD-only timeline row above the transport (elapsed /
full-width slider / total, `Lumen.Text.TabularCaption`), collapsed for live since raw IPTV
streams aren't seekable and the EPG progress already fills that slot. It reuses the PiP's proven
mechanics — code-behind drives the slider from `PositionSeconds`/`DurationSeconds` change
notifications, a `_seeking` flag suspends updates mid-drag (a two-way binding would let the 1 s
position tick yank the thumb), and release calls the existing `PlaybackService.Seek`; while
scrubbing, the elapsed label follows the thumb. Arrow keys now follow the content: live keeps the
TV-remote map (↑/↓ zap, ←/→ volume), VOD uses the media-player map (←/→ seek ±10 s, ↑/↓ volume —
zap is meaningless without a channel list). `--e2e-vod-ui` now also asserts the rendered
timeline: visible with `Maximum` ≈ duration for VOD, a `Seek` moves the rendered slider, and the
bar collapses when switching to live.

## Post-release UX pass — loading states + genuinely-async data layer

Symptom: switching rail sections or starting playback froze the UI briefly with no loading
feedback — even though skeleton screens already existed and were correctly gated on `IsLoading`.

- **Microsoft.Data.Sqlite is fake-async, so repositories now offload via `Task.Run`
  (`DbOffload`).** The provider completes its `...Async` methods synchronously, so every awaited
  Dapper call ran the query + row materialization **inline on the UI thread** (an `await` on an
  already-completed task never yields, and `ConfigureAwait(false)` is a no-op there). Page loads
  fired from `NavigationService.Activate` therefore blocked the dispatcher for the whole load —
  `IsLoading` flipped true→false without a frame ever painting, which is why the skeletons never
  showed. Every method in the five repositories now wraps its body in `Task.Run` via the
  `DbOffload` helper; **UI code must never rely on a repository call completing synchronously.**
  Safe by construction: WAL + 15 s busy timeout were already in place, background-thread DB access
  already existed (EPG scheduler, settings refresh), and no view model uses `ConfigureAwait(false)`
  before mutating UI-bound collections. `SqliteEpgImportSink` stays unwrapped (stateful, driven
  from already-pool-threaded sync paths).
- **Watch-history progress writes are drained at exit.** `SaveVodProgress` is fire-and-forget;
  now that it genuinely runs on a pool thread, `App.OnExit` awaits the last write (2 s cap) so
  pause-then-quit can't lose the resume position.
- **Live TV channel list is replaced wholesale** (`IReadOnlyList` + single assignment, the Guide
  pattern) instead of thousands of `ObservableCollection.Add`s after load.
- **Playback shows feedback from the first millisecond.** `State = Opening` moves to the top of
  `PlayChannelAsync`/`PlayVodAsync` (before LibVLC init), `PlayVodAsync` enters the full player
  *before* dispatching the stream (the spinner is gated on `IsFullPlayerActive`, so it previously
  couldn't render until after the open), LibVLC is **pre-warmed at startup** (`WarmUpAsync`,
  fire-and-forget after shell init; a failed init is retryable instead of memoized-faulted), and
  the overlay spinner gains a caption — "Opening stream…" / "Buffering NN%" from the previously
  discarded `Buffering` event `e.Cache` (new `BufferingProgress`).
- **Coverage gaps filled**: Favorites/VodDetail/Settings get skeletons (Settings only for the
  initial nav load — manual refreshes keep the page visible), Search's existing `IsSearching`
  gets a visible 18 px spinner in the field, and the shell content host is a new
  `TransitioningContentControl` (150 ms fade + 8 px rise on page change; no-op under OS
  reduced-motion, consistent with the toast entrance).

## Loading-states follow-up — paint ordering + video airspace fallbacks

Field testing on a real provider (6 s stream opens; one 25 s cold LibVLC init) showed the first
pass wasn't enough: page switches still appeared to hang ~1 s, and Watch/preview clicks showed a
black pane before any feedback. Log analysis (hang monitor: max UI stall 250 ms) proved the UI
thread was *free* — both problems were ordering, not blocking.

- **Page loads start only after the swap has painted.** `await` continuations resume at `Normal`
  dispatcher priority, which **outranks `Render`** — so a navigation whose load is a chain of
  quick pool-thread queries replays the whole chain before the new page (and its skeleton) ever
  renders: the old page looks frozen, then the new page pops in fully loaded.
  `NavigationService.RunNavigationAsync` now yields once at `Background` priority (below
  `Render`) before calling `OnNavigatedToAsync`, guaranteeing the swap + skeleton paint first.
  **Corollary: never start dispatcher-resuming async work you expect to happen "after the UI
  updates" without dropping below `Render` priority first.**
- **Every video surface shows the Opening/Buffering state, with WPF fallbacks for the airspace
  gaps.** The spinner+caption block lived inside the overlay's `IsFullPlayerActive` section, so
  the muted Live TV preview had *no* loading UI for the full 6–25 s open. It's now at the overlay
  root (the overlay travels with the shared `VideoView` to whatever surface hosts it). But the
  overlay renders inside the video's native airspace, which **doesn't exist before LibVLC init
  and is rebuilt whenever the view is reparented** — exactly the moments feedback matters. The
  full-player layer (`MainWindow`) and the preview pane (`LiveTvView`) therefore carry a plain
  WPF spinner/caption layer *behind* the `VideoSurface`: painted whenever the HWND isn't
  covering, hidden the instant it is.
- **`EnterFullPlayer` defers the surface attach by one `Background` beat** so the click paints
  the player layer + loading caption before the HWND reparent (which can't render that frame
  anyway). The deferred attach re-checks `IsFullPlayerActive` so a fast exit can't resurrect the
  full-player surface.
- **Warm-up starts before shell init** (was after), overlapping native LibVLC init with session
  init + first page load, and `PlayChannelAsync`/`PlayVodAsync` surface an init failure as the
  `Error` state (Back/Retry UI) instead of an eternal Opening spinner.

## Per-view search + capped-scan lookup fixes

Search moved into the views themselves (Movies/Series grid + category sidebar, Live TV
categories + channels), and global-search result activation was fixed for large libraries.

- **Paged grids filter in SQL; wholesale-loaded lists filter in memory.** The VOD grid pages
  120 rows at a time, so its search box became a `name LIKE` clause in `GetVodItemsAsync`
  (300 ms debounce, composing with the existing category/sort/offset SQL). Live TV loads a
  category's channels wholesale and EPG state lives on the shared `ChannelListItem`s, so the
  channel box and both category boxes filter in memory; `FillNowNextAsync`/`TickProgress`
  iterate the master list so filtered-out rows keep their EPG fresh. User input is escaped
  via the shared `SqlLike` helper (also used by global search) and matched with `ESCAPE '\'`.
- **Sidebar refills run under a suppress flag** because `ObservableCollection.Clear()` nulls
  a two-way `SelectedItem` binding synchronously, re-entrantly *inside* the `Clear()` call.
  In `VodLibraryViewModel` that null would cancel `_loadCts` and strand `IsLoading` (the
  superseded-reload `finally` deliberately skips cancelled tokens). The previous selection is
  remembered and re-selected once the filter lets it back in — for Live TV channels under a
  preview-suppress flag, so the restore doesn't restart the muted preview stream.
- **The synthetic "All" rows are pinned through the category filter** — they're the default
  selection and the escape hatch back to everything.
- **Search-result activation resolves by key, not by scanning a capped page.** Clicking a
  movie/series hit used to fetch the first 1000 items by name and `FirstOrDefault` — a silent
  no-op in bigger libraries. `GetVodItemByProviderIdAsync` (indexed unique lookup) replaced
  that scan in Search, Home continue-watching (cap was 5000), and Favorites.
- **`VirtualizingWrapPanel` zeroes its offset on collection Reset.** The grid ListBox is
  collapsed during reloads, so nothing re-clamped a stale deep-scroll offset; after a search
  the user landed mid-results — at worst inside the infinite-scroll threshold, chain-firing
  LoadMore.
- **Settings channel mapping got the same treatment** — a filter box over the (now
  wholesale-swapped) row list, and a type-to-filter box inside the guide-channel dropdown.
  The filter box is baked into the shared `Lumen.ComboBox` template behind an opt-in
  `LumenUi.ShowPopupFilter` attached property, relaying text through `LumenUi.PopupFilterText`
  so each `ChannelMappingRow` filters its own options view; opening the dropdown clears the
  previous filter and moves keyboard focus into the box. The "(no guide)" sentinel and the
  currently selected option are pinned through the filter — dropping the selected item from
  ItemsSource would push null through the two-way `SelectedValue` binding and erase the stored
  mapping mid-keystroke.
- **ComboBox popups now actually virtualize.** The custom template's ScrollViewer never bound
  `CanContentScroll` and no ItemsPanel was pinned, so a dropdown realized every item on open —
  a multi-second UI freeze at EPG scale (tens of thousands of options). The template now
  template-binds `CanContentScroll` and pins a recycling `VirtualizingStackPanel`, and the
  Settings benchmark exercises the new row/options structure.

## Series detail rework — season tabs + episode cards

- **Seasons are tabs, not stacked lists.** The detail page used to render every season's
  episodes in one long scroll; it now shows a pill tab strip (new `Lumen.TabStrip`/`Lumen.Tab`
  styles — a `ListBox` with a `WrapPanel`, so 20-season libraries wrap instead of clipping)
  over a single season's episode cards. Selection reuses the accent-subtle fill vocabulary
  from `Lumen.ListRow`/`Badge.New`. Only the selected season's episodes are realized, which
  also removes the old render-everything cost.
- **Episode rows became cards**: 160×90 thumbnail (`movie_image` when the provider sends one,
  an episode-number monogram otherwise), title, a "42m · 1 Mar 2021 · ★ 8.1" meta line, a
  two-line plot clamp, a resume progress bar inside the thumbnail, and a hover play affordance.
  Season switches animate through the existing `TransitioningContentControl`.
- **Series get a real primary action.** The hero now resolves "next up" from watch history:
  resume the most recent episode when it's mid-flight (>30s, <95%), advance to the following
  episode when it finished, else play S1E1 — and the tab strip lands on that episode's season.
  Metadata chips gained "{n} seasons · {m} episodes", and cast/director surface on both movie
  and series heroes (the fields were already mapped, just never shown).
- **Loading states**: the series section shows tab-pill and episode-card skeletons while
  `get_series_info` is in flight; an M3U/echo-empty series shows an explicit "no episodes"
  message instead of silent blankness.
- **DevServer's series fixture grew to 3 seasons / 15 episodes** with per-episode info blocks
  (assembled via `JsonSerializer` — the raw-string `$$"""` form can't express JSON's `}}}`
  brace runs), plus a `/series/…` stream route so episode playback works against the fixture.
  `--shot-shell` now switches to an Xtream profile (M3U profiles have no series) and captures
  `series-detail.png`.
## Windows 11 chrome + external artwork — the production polish pass

Driven by a field report: classic Windows-95-style caption buttons intermittently painted over
the custom title bar, and the UI needed to land as native Windows 11 rather than "generic dark".

- **The caption-button bug was the zero glass frame.** `Lumen.Window` used
  `GlassFrameThickness="0"`, which removes the DWM's claim on non-client painting; any runtime
  NC change (the fullscreen path swaps `ResizeMode` *and* clones the `WindowChrome`) let the
  classic theme repaint its own `_ □ ✕` strip. The fix is structural, not a repaint patch:
  `GlassFrameThickness="-1"` hands every NC repaint to the DWM permanently. `WindowFx` (new)
  then applies `DWMWA_USE_IMMERSIVE_DARK_MODE`, `DWMWCP_ROUND` corners, and — on 22621+ —
  `DWMWA_SYSTEMBACKDROP_TYPE=Mica` (21H2 falls back to the pre-release attribute 1029; Windows
  10 gets the solid palette). With a live backdrop the window paints a 85% `Window.Tint` so the
  desktop breathes through the chrome without washing out the cinematic dark.
- **Snap Layouts on a custom maximize button** requires answering `HTMAXBUTTON` from
  `WM_NCHITTEST` — but `WindowChromeWorker` answers `HTCLIENT` first for any
  `IsHitTestVisibleInChrome` element. `HwndSource` runs hooks newest-first, so `WindowFx`
  registers its hook at `DispatcherPriority.Loaded` (provably after the chrome's own hook).
  That steals WPF mouse input over the button, so hover/press visuals are driven through a
  `WindowFx.IsNcHover` attached flag and the click is re-dispatched from `WM_NCLBUTTONUP`.
  Verified interactively: flyout appears, snap zones work, click toggles maximize/restore.
- **Fluent shell layering**: pages now sit on a "layer" surface (rounded top-left corner, 1px
  hairline, faint white wash) over the Mica-tinted shell — the Windows 11 Settings/Terminal
  pattern — and elevated surfaces (dialogs, toasts, dropdowns, flyouts, tooltips, zap banner,
  reconnect pill) share a new edge-lit `Lumen.Brush.Glass` gradient + top-bright stroke, since
  WPF popups cannot host a real DWM backdrop. The title bar gains the app icon; `lumen.ico`
  had to become a pack `Resource` (an `ApplicationIcon` only brands the exe). A reusable
  `Lumen.EmptyState` (glyph disc + secondary text) replaced the bare-text empty states on
  Home/Search/Favorites/VOD grids.
- **Screenshot gates stay deterministic** by disabling the translucent backdrop whenever any
  `--` diagnostic argument is present — `RenderTargetBitmap` cannot see a DWM backdrop and
  would bake the tint's alpha into the PNGs.
- **External artwork is on by default, keyless.** Missing posters/backdrops resolve through a
  provider chain — TMDB when a credential exists (v3 key or v4 bearer, detected by shape;
  posters w500, backdrops w1280), else iTunes Search for movies (the 100×100 thumb URL
  rewritten to the 600×900 rendition) and TVMaze for series (plus its `/images` "background"
  for detail backdrops). Shipping a baked-in TMDB key was rejected (ToS + a repo is not a
  secret store); keyless sources make the default real, and Settings → Artwork explains the
  free-key upgrade. `TitleCleaner` (Core, tested) reduces "EN| The.Matrix.(1999) [4K HEVC]" to
  title+year — parenthesized years are authoritative, bare trailing years become hints only
  when other words remain ("1917" stays a title), and dot-runs collapse only in space-poor
  names so "S.W.A.T." survives. `ArtworkMatcher` scores candidates against both title forms
  ("Wonder Woman" + 1984 must match the film titled "Wonder Woman 1984") with a hard accept
  threshold — a wrong poster is worse than a monogram.
- **Every lookup is cached** in `artwork_cache` (migration 0005) keyed by kind + normalized
  title + year, shared across profiles and refreshes; negatives retry after 7 days and are
  flushed wholesale when a TMDB key is first configured (a keyless miss must not suppress the
  better source) or when the user clears the image cache (the wrong-poster escape hatch).
  Resolution runs behind a 2-slot semaphore with in-flight coalescing, detached from page
  tokens once a provider call starts (the result is about to be cached; cancelling wastes it),
  and never surfaces errors — artwork is cosmetic. `VodCard.PosterUrl` became observable so
  grids fill in as answers arrive; channels without playlist logos borrow the mapped XMLTV
  channel's icon (pure local data). The service is inert in diagnostic runs, keeping every
  gate offline-hermetic.

**Correction after first hands-on pass — ghost caption buttons.** With `GlassFrameThickness="-1"`
and the translucent Mica tint, faint duplicate min/max/close glyphs appeared behind Lumen's own
caption buttons: the DWM paints its *standard* caption buttons into any frame sheet extended over
the caption region — invisible under opaque apps, shimmering through an 85% tint. The frame is now
a 1px bottom sliver (`GlassFrameThickness="0,0,0,1"`): still non-zero, so the DWM keeps owning NC
repaints (the original classic-buttons fix holds — re-verified via `--e2e-fullscreen`), but the
caption region is pure client, so the DWM draws no buttons there. The system backdrop is unaffected
(`DWMWA_SYSTEMBACKDROP_TYPE` covers the whole window regardless of extension); the 21H2-only
`DWMWA_MICA_EFFECT` fallback was dropped because that legacy mechanism only rendered where the
frame was extended — 21H2 now gets the solid dark palette like Windows 10. Screenshot gates could
never have caught this: they run with the backdrop disabled and `RenderTargetBitmap` cannot see
DWM-composed layers — translucent-chrome changes need a live-window check.
