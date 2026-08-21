# Changelog

All notable changes to CwsDebug are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.5.0] - 2026-08-20

### Added
- **Crestron Construct / CH5 contracts** — `CwsDebugPage.RegisterContract(name, device, cse2jPath)`
  registers a panel and loads its Construct contract, so its signals can be addressed by their
  contract item path (`c~Main.B1.Press`) as well as by join number. The manifest is the project's
  own `<Project>.cse2j`, which the host program ships as content. If it is missing or unreadable
  the device still registers and numeric addressing still works — a warning says so rather than
  failing silently.
- **Named smart objects.** Smart-object numbers are labelled with the object's own name, taken
  from the SGD for a Smart Graphics panel (`Button List`, `Simple Keypad`) or from the contract for
  a Construct panel (`Main.Dpad`, `Main.HButtonList`). `RegisterDevice` takes an optional `sgdPath`
  to supply the SGD; without it, numbers are still listed, just unlabelled. The SO field offers the
  known objects as a searchable list — type a number or part of a name.
- **Named signals inside a smart object.** Contract-backed objects list their signals by contract
  name (`Left`, `Button3.ButtonPress`) instead of the generic `Bool Sig Number 5`.
- **Paired rows for contract items.** A contract's press and its selected-state can be shown as one
  row even though they sit on different joins.
- **Driving the Out direction.** The **Pulse / High / Low** controls now work on Out signals too.
  Driving one makes the program react exactly as if the panel had been used — its own signal
  handlers and Construct contract events run — so a control on the page can exercise the code behind
  a control on the panel, with no changes to the program. Subject to the same arm gate, kill switch
  and audit trail as the In direction. Where it is unavailable the controls do not appear and the row
  stays a live read-out.
- The **Log** toggle is on by default for a newly added signal.
- Clicking the clock in the page header switches between 12- and 24-hour time.

### Changed
- **Signal directions are now named from the panel's point of view, matching Crestron's own
  naming: `In` goes *into* the panel (feedback, text — changes what the panel shows) and `Out`
  comes *out of* it (presses — changes what the system does).** This is the reverse of 1.4.0, so a
  saved row may now show the opposite label; the signal it refers to has not changed. The signal
  keys were already API-aligned (`od5` is an Out signal) and are unchanged.
- Both directions are driven the same way, with **Low**, **Pulse** and **High**.
- The direction selector comes after the smart-object field, since which directions are available
  depends on the object chosen.
- The Signals button appears only while at least one device is registered, and follows
  registration while the page is open.
- Signals are polled with a request body rather than a query string, so a long list of logged
  signals cannot outgrow the request.

### Fixed
- A signal row is identified by the row itself, so renaming, un-logging or removing one row no
  longer affects another that happens to share a join.
- Adding a second valid paired row that shares one of its joins is no longer silently ignored.
- A signal that could not be resolved when it was first requested is retried, so a row added
  before the program finished starting recovers instead of showing an error for good.
- A contract's smart-object names are no longer left blank when the project has a page-level
  signal with no dotted path.
- One malformed entry in a contract manifest no longer discards the whole contract.
- Duplicate item paths in a manifest keep their first declaration and are reported, rather than
  silently changing a signal's direction.
- Reaching a device by a differently-cased name no longer writes duplicate log lines for the same
  change.
- The persisted log keeps a recoverable copy on disk throughout a flush, so power loss during a
  write cannot lose the history.
- Requests are bounded and guarded: a capped watch list and key length, a hard cap on cached
  resolutions, and a top-level guard so a browser closing mid-request cannot surface an error.
- Signal-change subscriptions are attached and detached outside the library's own lock, removing a
  potential stall when a busy panel is re-registered.
- Log lines and error replies are never written while the device lock is held.
- The force arm timer is read atomically, so it cannot mis-expire on a 32-bit processor.
- Reading a Smart Graphics file is bounded and streamed rather than buffered whole.

## [1.4.0] - 2026-08-12

### Added
- **Signal logging** — each signal in the Signals panel has a **Log** toggle. With it on, the
  processor writes a line every time that signal changes (`Signal XPanel Power Button (od1) ->
  true`), so a join can be followed without adding anything to the program. Lines come from the
  processor, so they carry the moment the change happened and include transitions too brief to
  fall on a poll. Digital edges always log; analog and serial have a floor between entries so a
  dragged slider cannot flood the buffer. Logging stops by itself shortly after the page stops
  asking, and the master logging switch and severity floor still apply.

### Changed
- Signals poll on their own 250 ms cadence rather than the log's two seconds, so a button press
  reaches the page in about a quarter of a second. One request is in flight at a time, so a slow
  connection throttles itself instead of queueing.
- The page stops requesting updates while its tab is hidden, and after fifteen minutes in which
  neither the user touched the page nor any value arrived. Arriving values count as activity, so
  watching a panel being pressed never suspends it. It resumes on a click, a keystroke, or the
  tab being shown.
- The Logging and Pause tooltips now state which one is a recording switch on the processor and
  which one stops the page requesting updates.

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
