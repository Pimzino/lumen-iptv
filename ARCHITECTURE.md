# Lumen — Architecture

## Layers

```
Lumen.App        WPF: views, view models, styles, navigation, playback host (composition root)
   │ references
   ├── Lumen.Core       domain models + service abstractions (no UI, no storage, no HTTP)
   ├── Lumen.Data       SQLite repositories, migrations, image cache, DPAPI protector
   └── Lumen.Providers  Xtream client, M3U parser, XMLTV parser
```

Dependency direction is enforced: `Core` references nothing; `Data` and `Providers` reference
`Core` only; `App` references all three. Provider *interfaces* (`IXtreamClient`,
`IM3uPlaylistParser`, `IXmltvParser`) live in `Providers`, not `Core` — the spec's layering
allows `App` to reference `Providers`, so `Core` stays dependency-free. The one contract shared
across the storage/provider boundary is `IEpgImportSink` (Providers produce parsed entities,
Data consumes them into SQLite); it lives in `Core`. Internals are exposed to test projects via
`InternalsVisibleTo` only.

## Composition

`App.xaml.cs` builds a Generic Host (`Host.CreateApplicationBuilder`) in `OnStartup`:
Serilog is configured first (rolling file in `%LocalAppData%\Lumen\logs` + debug sink), then
services register, the host starts, the database initializes (WAL, migrations) off the UI thread,
and only then is `MainWindow` resolved and shown. `ShutdownMode` stays `OnExplicitShutdown` until
the window exists to avoid a race with async startup.

Page view models are transient (fresh state per navigation); shell, services, repositories, and
the playback service are singletons. `App.GetService<T>()` exists only for visual-only plumbing
that cannot take constructor injection (the video-surface attachment and async image loading);
view models never use it.

## Navigation

ViewModel-first: `NavigationService` (an `ObservableObject`) exposes `CurrentViewModel`, which the
shell's `ContentControl` binds to; `Views/ViewLocator.xaml` maps each VM type to its view via
`DataTemplate`. Each navigation cancels the previous page's `CancellationToken`; pages implement
`INavigationAware` and flow that token through every load so leaving a page cancels its I/O. A
back stack supports detail pages (`GoBack`); top-level rail navigation clears it so Back stays
page-local.

## Storage

Single SQLite database at `%LocalAppData%\Lumen\lumen.db`, WAL journaling, foreign keys on.
Versioned migrations are embedded SQL scripts (`Lumen.Data/Migrations/NNNN_name.sql`) applied in
ascending order inside transactions and recorded in `schema_migrations`. Timestamps are unix
seconds (UTC); enums are stored as integers. Snapshot syncs (channels, categories, VOD) match on
stable provider ids so favorites and EPG mappings survive refreshes. EPG import batches 5,000
rows per transaction via prepared commands, keeping memory flat on multi-GB XMLTV files.

## Threading model

- **UI thread** owns the visual tree, bindings, and view-model observable state.
- **Library code** (`Core`/`Data`/`Providers`) is async end-to-end with `ConfigureAwait(false)`
  enforced by analyzer (CA2007 as error). No library code touches WPF types.
- **Long work** (EPG import, catalog sync, playlist parse) runs on the thread pool via
  `Task.Run`; progress crosses back to the UI through `IProgress<T>`.
- **Playback events** arrive on LibVLC's native threads. `PlaybackService` marshals every state
  mutation to the dispatcher (`OnUi`) and runs every player command (`Play`/`Stop`/`SetAudioTrack`)
  on the thread pool, so the UI thread never blocks on a native call and native callbacks never
  touch WPF directly.
- A **debug-only `DispatcherHangMonitor`** pings the dispatcher every 50ms and logs any UI-thread
  stall over the 50ms quality budget.

## Playback handoff design

The whole app shares **one `LibVLC`, one `MediaPlayer`, and one `VideoView`** owned by the
`PlaybackService` singleton. Three named surface slots — Preview (Live TV), FullPlayer, and
MiniPlayer — are `VideoSurface` decorators declared in their respective views. Switching between
them **reparents the single `VideoView`** (`Decorator.Child`) rather than recreating anything, so
moving from the muted preview to the full player to the floating mini player is seamless — the
stream never restarts.

The player overlay (transport, now/next, zap banner) lives in `VideoView.Content`, the only WPF
layer that composites above LibVLC's native video airspace. The shell installs it once via
`PlaybackService.SetOverlay`. Because that airspace is invisible to `RenderTargetBitmap`, the
player is validated by an automated headless driver (`--e2e-play`) rather than screenshots.

Two quirks of that native airspace are handled explicitly. The host window LibVLCSharp creates
is a bare Win32 "static" child that never paints — until VLC's vout renders a frame it shows
white/stale pixels (and WPF drawn *behind* it shines through). `VideoHostBlackout` subclasses it
to erase/paint black, so opens, buffering, reconnects, and resizes present a clean black pane on
every surface. And because the un-painted host does not hide WPF content, page-level fallback
loading blocks (Live TV preview, full-player layer) gate on `IsColdOpenLoading` — "opening or
buffering *and* no surface hosts the shared view" — rather than raw playback state, so the
in-video overlay's spinner is the only loading indicator once the view is attached. Decoder-side,
LibVLC starts with `--no-avcodec-corrupted`: IPTV joins land mid-GOP and TS discontinuities are
routine, and rendering those known-broken frames (the libvlc default) reads as glitching; holding
the last clean frame does not.

Live playback resolves its URL from the M3U `stream_url` or, for Xtream, from credentials +
container preference (`.ts`/`.m3u8`). Stream drops trigger reconnect with 1/2/4/8/8s backoff
(max 5 attempts). VOD playback (`PlayVodAsync`) additionally seeks to a resume position on the
first Playing event and tracks position into `watch_history` on a 1s timer, saving on pause,
stop, and navigation. Live channels land in `watch_history` too — once per channel play, after
10s of real (non-preview) playback so zapping through channels leaves no trace — which feeds
the Home page's "Recently watched" rail.

## Window chrome (Windows 11)

`WindowFx` (attached behavior, applied by the `Lumen.Window` style) owns the DWM story: immersive
dark frame, `DWMWCP_ROUND` corners, and — on Windows 11 22H2+ — a Mica system backdrop. The
style's `WindowChrome` keeps `GlassFrameThickness="0,0,0,1"` deliberately. Non-zero, because with
a zero glass frame the classic theme repaints the non-client area whenever
`ResizeMode`/`WindowChrome` change at runtime (the fullscreen path does both), which used to draw
Windows-95-style caption buttons over the custom title bar; a bottom-only sliver, because a frame
sheet extended over the caption region makes the DWM paint its own ghost min/max/close glyphs
there, faintly visible through the translucent Mica tint. The backdrop needs no extension —
`DWMWA_SYSTEMBACKDROP_TYPE` is whole-window base material. With the DWM owning every NC repaint,
both failure modes are gone. When the backdrop is
live, the window template swaps its opaque background for a near-opaque tint
(`Lumen.Brush.Window.Tint`) and the shell hosts pages on a Fluent "layer" surface; with no
backdrop (Windows 10, or any diagnostic run — captures must stay deterministic) everything
collapses onto the solid dark palette. Snap Layouts work on the custom maximize button via a
`WM_NCHITTEST → HTMAXBUTTON` hook registered *after* `WindowChromeWorker`'s own hook (hooks run
newest-first; registering at Loaded priority guarantees the ordering), with hover/press visuals
mirrored through the `WindowFx.IsNcHover` attached flag because WPF never sees NC mouse input.

## Artwork enrichment

`ArtworkService` fills catalog artwork gaps from external metadata services, on by default:
TMDB when the user pasted a credential (v3 key or v4 token, detected by shape), keyless
iTunes (movies) / TVMaze (series) otherwise. `TitleCleaner` (Core) reduces messy IPTV names
("EN| The.Matrix.(1999) [4K]") to a searchable title + year; `ArtworkMatcher` scores candidates
and rejects anything below a confidence threshold — a wrong poster is worse than no poster.
Every lookup, including "found nothing", lands in the `artwork_cache` SQLite table keyed by
kind + normalized title + year, so a title is resolved online once per install; negative entries
retry after 7 days and are flushed when a TMDB key is first configured. Lookups run behind a
2-slot semaphore with in-flight coalescing, are fire-and-forget from view models (page tokens
cancel them), and treat every failure as debug-log-and-move-on. Channels missing a playlist logo
fall back to the mapped XMLTV channel's `<icon>` — local data, no network. The whole service is
inert during diagnostic runs so gates stay hermetic.

## Signature effect — ambient glow

`AmbientColor` downsamples an image to 16×16, averages its saturated pixels, and normalizes the
result to a consistent lightness/saturation band — the seed color for Lumen's glow. `AmbientGlow`
is an attached behavior on a `Border` that turns that color into a soft radial wash at ~12%
opacity, fading in when the color resolves off-thread. It's applied to the player's now-playing
bar and zap banner, bound to the current channel's logo, so the content's mood bleeds into the
chrome.

## Motion

Durations are markup extensions (`{theming:Dur Fast|Slow|Shimmer}`), not resources, because a
merged "animations off" dictionary can't reliably reach storyboards compiled into other
dictionaries. They read a static flag captured from `SystemParameters.ClientAreaAnimation` in the
`App` static constructor; when the OS disables animation, every duration bakes to zero.

## Accessibility & keyboard

Every interactive element carries `AutomationProperties.Name` and a 2px accent focus ring (2px
offset). The app is fully keyboard-navigable: Tab/Shift+Tab across the nav rail, lists, and
controls; Enter/Space activates; arrow keys move within lists and the guide. The player has its
own map (Space play/pause, F fullscreen, M mute, Esc back, Enter channel list; arrows follow the
content — live: ↑/↓ zap, ←/→ volume; VOD: ←/→ seek ±10s, ↑/↓ volume), routed from the window so
focus never eats a key. Ctrl+K opens search from anywhere;
Ctrl+Shift+G opens the design-system gallery (debug).

## Diagnostics & gates

Headless modes on the app exe drive the phase gates without a human:
`--e2e`/`--e2e-verify` (onboarding + restart persistence), `--e2e-play` (play/zap/reconnect,
plus the live watch-history write behind "Recently watched"),
`--e2e-fullscreen` (enter/exit/re-enter full player through the live window chrome — guards the
frozen-`WindowChrome` class of crash), `--e2e-resume` (VOD resume), `--scroll-bench` (10k-channel
list), `--guide-bench` (500×7-day guide + timezone), `--search-bench` (search latency), `--soak`
(memory), `--glow-probe` (ambient color), and `--shot-shell`/`--gallery-shot` (screenshots).
`tools/Lumen.DevServer` emulates an Xtream portal + M3U playlist + XMLTV feed + seekable media for
these runs. The `LUMEN_DATA_ROOT` environment variable points any run at an alternate data root, so
gates stay hermetic on a machine whose `%LocalAppData%\Lumen` holds a real library; window-showing
capture runs accept `--shot-size WxH` to lay out long scrolling pages in full for design review. Gates that show a window (`--e2e-fullscreen`, `--shot-shell`) exercise the real Style
and triggers — the layer a ViewModel-only test can't reach — so window-chrome regressions are caught
headlessly. `--settings-bench` loads the real Settings page with 10k channel-mapping rows and
asserts the list virtualizes (only a handful of realized containers) so a large playlist can't freeze
the UI thread. `--probe-stream` resolves the current profile's real stream URLs and plays them
through a matrix of User-Agents/options with LibVLC logging captured, to diagnose provider-side
rejections (e.g. an HTTP 403 that only a whitelisted player User-Agent clears). LibVLC's own
warnings/errors are forwarded into the app log at all times, so stream failures show a cause rather
than a silent error.
