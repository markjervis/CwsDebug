# Changelog

All notable changes to CwsDebug are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed
- Documentation and code comments now call CWS by its SDK name, Crestron Web Scripting.
- `PathMap` keeps the build machine's folder paths out of `CwsDebug.dll`.
- Documented page URLs now use `https://`; VC-4 URLs name the `<server>` and explain `<roomId>`.

### Fixed
- The page's 2-second log poll now keeps only one round in flight, as the signals poll already
  does. On a slow link, requests no longer stack up and the same entries can no longer be
  appended twice. A tick that lands while a round is still outstanding is skipped, and the next
  one catches up.

## [1.7.0] - 2026-10-20

### Added
- **Non-printable characters shown as hex** (suggested in #13). Control characters
  0x00–0x1F (except TAB and LF), DEL (0x7F) and bytes 0x80–0xFF now appear in the log as a muted,
  tinted escape such as `\x0D` or `\xF3`, so device responses and protocol strings can be read
  byte for byte. A CR+LF pair is still a line break; a lone CR shows as `\x0D`. Characters above
  0xFF are real Unicode and show as-is. The 0x80–0xFF range is escaped on purpose: in SIMPL# a
  device response is one byte per character, so 0xF3 is a byte rather than "ó" — which means
  accented Latin-1 text such as "café" shows as `caf\xE9`.
- **Bytes button** at the right of the level-filter bar cycles the display between `\x##` (the
  default), `[##]` and raw (the previous behaviour). The choice is saved per browser.
- **Search and export follow the display.** Search also matches the escaped form, so typing
  `\x0d` finds real carriage returns, and the exported text file contains the escaped form
  rather than raw control bytes. Read-only Serial values in the Signals panel use the same display.

### Changed
- **Whitespace is preserved in log messages** (suggested in #12). Runs of spaces,
  tabs and line breaks are no longer collapsed, so aligned output stays aligned and multi-line
  messages — such as the stack trace written by `LogException` — show on several lines.

## [1.6.2] - 2026-10-15

### Fixed
- **Full signal names on hover.** Names in the Signals panel are clipped to the column width,
  so similar ones (`Main.HButtonList - Li…`, `Main.HButtonList - Bu…`) couldn't be told apart.
  Hovering a name now shows the full name first, then its device and signal keys, then the
  rename hint.
- **Log readable on phones and narrow windows.** The timestamp kept its full width while the
  message shrank, so on a narrow screen (especially with the Signals panel open) messages
  wrapped one character per line. The timestamp now drops to its own line when space runs out,
  the header buttons wrap instead of running off the screen, and at 760px wide or less the
  Signals panel stacks above the log instead of beside it. Desktop layout is unchanged.

## [1.6.1] - 2026-10-15

### Fixed
- A **Both** row for a digital join in the Signals panel now offers the Out (out-of-panel)
  **Low / Pulse / High** controls as well as the In ones. Previously a paired digital row only had
  the In controls, which change the feedback the panel shows, so driving it lit the button but
  never ran the program's own handlers. A button wired to program logic, such as a relay, did
  nothing from the page even though the same press on the panel worked. The two sets are now
  labelled **In** and **Out**. Rows added as In or Out alone, and analog or serial Both rows,
  were not affected.

## [1.6.0] - 2026-10-14

### Added
- **Drop-in replacements for `ErrorLog` and `CrestronConsole`** (suggested in #7). Existing code
  that logs through Crestron's static classes can write to the debug page by changing one
  using-alias per class, with no edits at the call sites:
  `using ErrorLog = CwsDebug.CwsErrorLog;` and `using CrestronConsole = CwsDebug.CwsConsole;`.
  Both classes cover their SDK class's entire public static surface with the SDK's own
  signatures, so existing calls bind unchanged and aliasing compiles even where the file also
  registers console commands. The logging methods (`ErrorLog.*`, `Print`, `PrintLine`) never throw.
- **`CwsErrorLog`** mirrors `ErrorLog`: `Error` (template plus args, a single string, and the
  `(string, string)` pair), `Exception(string, Exception)`, `Info`, `Notice`, `Ok` and `Warn`.
  Levels map as Info to Info, Notice to Notice, Warn to Warning, Error and Exception to Error, and
  Ok to Notice (the page has no OK level). `Exception` records the message followed by the full
  exception text — type, message, stack trace and inner exceptions. The `params` overloads accept
  nullable arguments (`object?[]`) exactly like `ErrorLog`'s, so nullable-enabled callers get no
  new warnings.
- **`CwsConsole`** mirrors `CrestronConsole.Print` / `PrintLine`. The console is a character stream
  but the page is a list of entries, so output is line-buffered: `Print` text is held until a
  newline arrives, each complete line becomes one entry, `PrintLine` flushes whatever is pending,
  and blank lines are skipped. Lines reach the page in the order they were buffered, even across
  threads. A fragment left without a newline is published as its own entry once it has been
  pending at least 2 s, on the library's periodic 5-second tick — so typically 2–7 s later, and
  only once `CwsDebugPage.Register()` has run — timestamped when it is published rather than when
  `Print` was called. The new `CwsConsole.Flush()` publishes a pending fragment immediately, and
  any pending fragment is flushed at program stop, so text printed in pieces with long pauses
  between them can appear split across entries. `CwsConsole.PrintLevel` sets the level all console
  output is recorded at (defaults to Info); at Fatal, lines behave like `CwsDebugPage.LogFatal` —
  recorded as Fatal on the page and also written to the processor error log.
- **`CwsConsole`** also carries the rest of `CrestronConsole`'s public surface as pure
  pass-throughs to the real class — `AddNewConsoleCommand` (2 overloads), `RemoveConsoleCommand`,
  `ConsoleCommandResponse` (2 overloads), `SendControlSystemCommand` (4 overloads: `ref string` and,
  as in the SDK, `out StringBuilder` responses) and the get-only `ConsoleRegistered` — with the
  same signatures, return values and exceptions. They are not gated by `PassThrough` and record
  nothing on the page; `ConsoleCommandResponse` is always forwarded, since it is the reply to
  whoever typed at the console.
- **`CwsErrorLog.MirrorToErrorLog`** and **`CwsConsole.PassThrough`** (both default off) also
  forward every logging call, with its original arguments, to the real `ErrorLog` /
  `CrestronConsole` (`PassThrough` covers `Print` / `PrintLine`).
  Forwarding happens even when `LoggingEnabled` or `MinLevel` keeps the message off the page.

**By default these classes write ONLY to the debug page — not to the processor error log and not
to the console.** Turn on `MirrorToErrorLog` / `PassThrough` to keep the old destinations as well;
on an appliance a mirrored `ErrorLog` message then shows up twice on the page (once in the app log,
once in the processor error log view). Page recording honours `LoggingEnabled` and `MinLevel` like
the `Log*` methods.

`Error(msginfo, errorstring)` follows the SDK: `msginfo` is the method name or other context,
`errorstring` the error text, and the page shows `msginfo: errorstring`. As a convenience, if
`msginfo` uses argument 0 as a format placeholder — in any form .NET accepts, such as `{0}`,
`{0:X}` or `{0,8}`, but not the escaped `{{0}}` — it is formatted with `errorstring` instead. That
covers a call such as `Error("Failed {0}", x)` with a string `x`, which C# binds to this overload
rather than to the template-plus-args one.

## [1.5.1] - 2026-08-21

### Fixed
- A contract device's signal picker no longer offers joins the manifest doesn't declare a
  name for. Those were shown with CH5's own generic placeholder ("Bool Sig Number 7"),
  which read as a mismatch against the same smart object's other direction when that one
  has a real contract name (e.g. a Dpad's Out picker offering "Up", "Down"... while its In
  picker offered a meaningless placeholder for an unrelated, undeclared join). Manual
  Join# entry still reaches anything the picker now leaves out. SGD-loaded devices are
  unaffected — their SDK name is already meaningful, not a placeholder.
- The PDF documentation no longer carries a browser-print header (title/date) and footer
  (source file path) on every page.

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
