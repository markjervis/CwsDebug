# Changelog

All notable changes to CwsDebug are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.3.0] - 2026-08-11

### Added
- **Signals panel** — a device's joins can be viewed and forced live from the debug page,
  alongside the log. `CwsDebugPage.RegisterDevice(name, device)` exposes any `BasicTriList`
  (XPanel, TSW, and other touchpanels); `UnregisterDevice` / `UnregisterAllDevices` release it.
- Digital, analog and serial joins in both directions, plus Smart Graphics object signals
  addressed by their SGD names. Joins that exist in both directions can be shown as a single
  paired row.
- Forcing: **High**, **Low** and **Pulse** on digital joins, and typed values on analog and
  serial joins. Signals coming *from* a panel are read-only, since output signals cannot be
  written.
- Force safety: `ValueWritesAllowed` disables forcing entirely for a handed-over system;
  forcing is otherwise off until armed on the page, arming is never persisted and lapses after
  `ValueArmTimeoutMs` (default 5 minutes) of inactivity; every force and every refusal is
  written to the log with the requesting address, regardless of the logging switch or severity
  floor.
- The signal list is built on the page, with friendly names per row, and is remembered in the
  browser rather than declared in the program.
- Panel layout: the signals panel and the log sit side by side with a draggable divider.

### Changed
- The `APP` / `SYS` source tag on log rows is shown only when both log sources are displayed.
- Buttons, dropdowns and fields across the page have hover help.

### Requirements
- Adds a reference to `Crestron.SimplSharpPro.DeviceSupport`.

## [1.2.1] - 2026-08-11

### Fixed
- Log messages containing tab or other control characters no longer break the page's
  JSON feed (previously one such entry made the page show "Connection error" until the
  entry aged out of the buffer). All control characters are now escaped in API responses.
- `Log*` calls can no longer throw a `FormatException` into the calling program — a
  mismatched template/argument pair or literal braces now falls back to recording the raw
  template with the arguments appended. `LogFatal` mirrors pre-formatted text to the
  system error log the same way.
- Messages logged before `Register()` runs are now reliably persisted; previously they
  could be lost on restart if nothing else logged afterward.
- Registering a base path already owned by another program on the same appliance no
  longer throws out of `InitializeSystem()`; the conflict is reported to the system error
  log and the page is simply unavailable. A retry after a failed registration no longer
  duplicates the persisted log or leaks timers.
- `Register()` is marked `NoInlining` so the host program's name resolves correctly in
  the page header regardless of JIT inlining.
- The page title (room/program name) is HTML-encoded before being placed in the page.
- Processor error-log timestamps are normalized (invariant culture) so the combined
  Debugger + Error Log view sorts chronologically across both sources.
- The processor error-log pane updates correctly once the console log is at capacity,
  and a transient fetch failure no longer delays rendering of already-received entries.

### Changed
- Ring-buffer trimming is amortized: the buffer may transiently hold up to ~10% more
  than `maxEntries` and is trimmed back in one operation, making logging under load
  significantly cheaper. At least `maxEntries` entries are always retained.
- Incremental log fetches locate the client's cursor by binary search.
- The page only rebuilds its display when a poll actually returns new data.
- Registration is thread-safe against concurrent `Register()` calls.

### Documentation
- Added a plain-language "note on network access" to the README and the PDF
  documentation (the page has no login; guidance for shared networks).

## [1.2.0] - 2026-06-09

First public release.

### Added
- `LogWarning(msg, args)` severity level.
- `LogException(ex, context?)` — records an exception with full type, message and stack
  trace (including inner exceptions).
- `MinLevel` property — severity floor; below-floor messages are dropped at the source.
- `LoggingEnabled` property and page "Logging" button — master record switch, persisted
  across restarts.

### Earlier
- Core library (pre-1.2.0): CWS-hosted single-page log viewer with search, severity
  filtering, light/dark themes, text export, pause control, bounded ring buffer,
  disk persistence, millisecond timestamps, incremental polling, and the processor
  error-log view on hardware appliances.
