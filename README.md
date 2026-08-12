# CwsDebug

A self-contained CWS debug log page for Crestron SIMPL#Pro programs.

CwsDebug hosts a small web page on the Crestron Web Server (CWS) that streams your program's
own log messages and — on a hardware appliance — the processor error log. The entire UI
(markup, CSS, and JavaScript) is embedded in the library, so there are no external content
files to deploy. It builds as a **Program Library** and is referenced by your SIMPL#Pro program.

## Features

- **Live log page** with search, severity filtering (Info / Notice / Warning / Error / Fatal),
  light/dark themes, and one-click text export.
- **Process-wide singleton** — a fully static API; log from any class on any thread without
  passing an instance around.
- **Bounded ring buffer**, sized at registration (defaults to 2000 entries), so memory and the
  page payload stay bounded.
- **Persistence across restarts** — the buffer is mirrored to the application directory and
  reloaded on startup, so recent history survives a program restart.
- **Master logging switch** (`LoggingEnabled`) and a **severity floor** (`MinLevel`) to keep
  recording cheap in production; both controllable at runtime.
- **Pause** control on the page (recording continues on the processor while paused).
- **Millisecond timestamps** and incremental polling (only new entries are fetched).
- **Processor error log** view on hardware appliances (read via the console `err` command).
- **Signals panel** — register a touchpanel with one line and view or force its joins live from
  the page, Toolbox-style: digital (high / low / pulse), analog and serial, plus Smart Graphics
  object signals picked by their SGD names. Forcing sits behind an arm switch and every force is
  recorded in the log.

## Requirements

- .NET 8.0
- [Crestron.SimplSharp.SDK.ProgramLibrary](https://www.nuget.org/packages/Crestron.SimplSharp.SDK.ProgramLibrary) 2.21.257

## Installation

Reference the project (or the compiled `CwsDebug.dll`) from your SIMPL#Pro program:

```xml
<ItemGroup>
  <ProjectReference Include="..\CwsDebug\CwsDebug.csproj" />
</ItemGroup>
```

## Usage

The API is static — there is nothing to instantiate. Call `Register()` once from
`InitializeSystem()`; you can log from anywhere, on any thread, even before `Register()` runs.

```csharp
using CwsDebug;

public class ControlSystem : CrestronControlSystem
{
    public override void InitializeSystem()
    {
        CwsDebugPage.Register("/debugger");          // default base path; buffer defaults to 2000 entries
        // CwsDebugPage.Register("/debugger", 5000);  // optional: larger ring buffer
        // CwsDebugPage.MinLevel = CwsDebugPage.DebugLevel.Notice;  // optional: drop anything below Notice

        CwsDebugPage.LogInfo("System starting");
        CwsDebugPage.LogNotice("Loaded {0} devices", deviceCount);
        CwsDebugPage.LogWarning("Low buffer on {0}", deviceName);
        CwsDebugPage.LogError("Comms timeout on {0}", deviceName);
        CwsDebugPage.LogFatal("Unrecoverable: {0}", reason);   // also written to the system ErrorLog

        try { /* ... */ }
        catch (Exception e)
        {
            CwsDebugPage.LogException(e, "Error during init");  // full type + message + stack trace
        }
    }
}
```

Cleanup is automatic — the library releases the CWS server and flushes the log to disk when the
program stops or restarts — so calling `Dispose()` / `Shutdown()` by hand is optional.

### Browsing to the page

| Platform               | URL                                                                          |
|------------------------|------------------------------------------------------------------------------|
| Appliance (RMC4, etc.) | `http://<processor>/cws/debugger/debugger.html`                              |
| VC-4 (Server)          | `http://<processor>/VirtualControl/Rooms/<roomId>/cws/debugger.html`         |

## API reference

All members are `static` on `CwsDebugPage`.

| Member                              | Description                                                                                      |
|-------------------------------------|--------------------------------------------------------------------------------------------------|
| `LogInfo(msg, args)`                | Records an informational message.                                                                |
| `LogNotice(msg, args)`              | Records a notice-level message.                                                                  |
| `LogWarning(msg, args)`             | Records a warning-level message.                                                                 |
| `LogError(msg, args)`               | Records an error-level message.                                                                  |
| `LogFatal(msg, args)`               | Records a fatal message and mirrors it to the system error log.                                  |
| `LogException(ex, context?)`        | Records an exception at Error level with its full type, message, and stack trace.                |
| `Register(basePath, maxEntries?)`   | Starts the CWS server. `basePath` defaults to `"/debugger"`; `maxEntries` defaults to `2000`.    |
| `MinLevel` *(property)*             | Minimum severity recorded (`DebugLevel`: Info, Notice, Warning, Error, Fatal). Defaults to Info. |
| `LoggingEnabled` *(property)*       | Master record switch. Persisted across restarts. Defaults to `true`.                             |
| `Dispose()` / `Shutdown()`          | Stops the server, flushes the log, releases resources. Idempotent and optional.                  |
| `RegisterDevice(name, device)`      | Exposes a device's joins in the Signals panel. Any `BasicTriList` (XPanel, TSW, …).              |
| `UnregisterDevice(name)`            | Drops a device. Call before disposing it.                                                        |
| `UnregisterAllDevices()`            | Drops every registered device.                                                                   |
| `ValueWritesAllowed` *(property)*   | Master switch for forcing. `false` leaves the panel read-only. Defaults to `true`.               |
| `ValueWritesArmed` *(property)*     | Arm gate for forcing. Defaults to `false` at every program start; never persisted.               |
| `ValueArmTimeoutMs` *(property)*    | Idle timeout before forcing disarms itself. Defaults to 300000 (5 min); `<= 0` disables it.      |

Each `Log*` method accepts an optional `string.Format` template plus arguments, e.g.
`CwsDebugPage.LogInfo("Connected to {0}:{1}", host, port);`

## Signals panel

Register a device once, and its joins become browsable and forceable from the page:

```csharp
public override void InitializeSystem()
{
    CwsDebugPage.Register("/debugger");
    CwsDebugPage.RegisterDevice("Panel", tsPanel);   // any BasicTriList
}
```

Call `UnregisterDevice` before disposing the device — a program's own stop handler usually runs
before the library's, so the page can otherwise briefly reach a disposed panel:

```csharp
CwsDebugPage.UnregisterDevice("Panel");
panel.Dispose();
```

Open **Signals** on the page and add a signal by device, type (Digital / Analog / Serial) and
direction:

- **Out** — the program's value going *to* the panel: button feedback, gauge levels, indirect
  text. This is the side that can be forced.
- **In** — what the panel sends *back*: presses and slider moves. Read-only, since a panel's
  output signals cannot be written.
- **Both** — one row showing a join's two directions together.

Enter a **Smart Object ID** to reach a Smart Graphics object, and its signals are listed by their
SGD names (`Item 1 Pressed`, `Select Item`, `Item 1 Text`) rather than by number. Rows can be
given friendly names, and the list is remembered in the browser.

Digital rows show the signal's state and light a lamp while it is high, with force buttons for
**High**, **Low** and **Pulse**; analog and serial rows are edited by typing a value and pressing
Enter. Forcing writes to the join itself — unlike Toolbox there is no separate "unforce", so the
value stands until the program or the panel changes it again.

Forcing is disabled until it is armed on the page, and arming lapses after five idle minutes or
whenever the program restarts. Every force is recorded in the log with the address it came from.

## Platform behavior

- **Base path (VC-4):** with the default base path, the redundant segment is collapsed so the
  page is served directly at `.../cws/debugger.html`. Pass an explicit (non-default) base path to
  keep it in the URL on every platform — required if a single program hosts more than one page.
- **Concurrent rooms (VC-4):** each room is an isolated program instance with its own CWS
  namespace and its own log.
- **Header title:** shown as `"{Name} - Debugger"`, where Name is the room name on VC-4 and the
  host program's namespace/assembly name on an appliance.
- **Processor error log:** read via the console `err` command and shown only on a hardware
  appliance; hidden on VC-4, where no per-program console error log exists.

## Roadmap

- **Crestron Construct Contracts** *(planned)* — support for panels built with Crestron
  Construct, addressing signals through the project's Contract definitions the same way
  Smart Graphics signals are addressed through the SGD today.

## A note on network access

The debug page has no login. Anyone who can reach the processor on the network can open the
page, read the log, clear it, or switch logging off — the same way Crestron's own built-in
web pages work. On a dedicated control-system network this is normally fine. If the processor
sits on a network shared with general office traffic or guests, keep in mind that the log is
visible to everyone on that network — so avoid logging passwords, door codes, or anything else
sensitive, and ask your IT/network administrator to place the processor on a restricted VLAN
if that's a concern.

The same applies to the Signals panel, which can change values on a running system. Forcing is
switched off until someone arms it, arming is forgotten whenever the program restarts or the
page is reloaded, it switches itself back off after five idle minutes, and every force is
written to the log with the address of the machine that sent it. For a system being handed over,
set `CwsDebugPage.ValueWritesAllowed = false` to leave the panel read-only while keeping the
live view.

## Building

```bash
dotnet build CwsDebug/CwsDebug.csproj -c Release
```

## Documentation

A formatted reference is included: [`CwsDebug_Documentation.pdf`](CwsDebug_Documentation.pdf).
Release history is tracked in [`CHANGELOG.md`](CHANGELOG.md).

## License

[MIT](LICENSE) © 2026 Mark Jervis
