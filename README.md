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
- **Signals panel** — register a touchpanel with one line and view or change its signals live
  from the page, Toolbox-style: digital (low / pulse / high), analog and serial, plus smart-object
  signals picked by name. Changes sit behind an arm switch and every one is recorded in the log.
- **Drive either direction** — send a value *into* the panel to change what it shows, or drive a
  signal *out of* it to make the program react exactly as if the panel had been used, so a control
  on the page can exercise the code behind a control on the panel.
- **Crestron Construct / CH5 contracts** — register a panel with its contract and address signals
  by their contract item path, with smart objects and their signals listed by contract name.
- **Signal logging** — switch on `Log` for any signal in the panel and every change of it is
  written to the log, without adding anything to the program.

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
| `RegisterDevice(name, device, sgdPath?)` | Exposes a device's joins in the Signals panel. Any `BasicTriList` (XPanel, TSW, …). Pass the SGD path to label smart objects with their design-time names. |
| `RegisterContract(name, device, cse2jPath)` | As `RegisterDevice`, plus a Crestron Construct contract so signals can be addressed by contract item path. |
| `UnregisterDevice(name)`            | Drops a device. Call before disposing it.                                                        |
| `UnregisterAllDevices()`            | Drops every registered device.                                                                   |
| `ValueWritesAllowed` *(property)*   | Master switch for changing signals. `false` leaves the panel read-only. Defaults to `true`.     |
| `ValueWritesArmed` *(property)*     | Arm gate for changing signals. Defaults to `false` at every program start; never persisted.     |
| `ValueArmTimeoutMs` *(property)*    | Idle timeout before the arm gate closes itself. Defaults to 300000 (5 min); `<= 0` disables it. |

Each `Log*` method accepts an optional `string.Format` template plus arguments, e.g.
`CwsDebugPage.LogInfo("Connected to {0}:{1}", host, port);`

## Signals panel

Register a device once, and its signals become browsable and drivable from the page:

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

- **In** — signals going *into* the panel: button feedback, gauge levels, indirect text. Driving
  these changes what the panel **shows**.
- **Out** — signals coming *out of* the panel: presses and slider moves. Driving these makes the
  program react exactly as if the panel had been used, so it changes what the system **does**.
- **Both** — one row showing a join's two directions together.

Both directions are driven the same way, with **Pulse**, **High** and **Low** — the difference is
what a change reaches, not how you make it.

Enter a **Smart Object ID** to reach a smart object, and its signals are listed by name
(`Item 1 Pressed`, `Select Item`, `Item 1 Text`) rather than by number. The field offers the
objects it knows about, so you can type a number or part of a name. Rows can be given friendly
names, and the list is remembered in the browser.

To have smart objects labelled with their design-time names, pass the SGD file you already give
`LoadSmartObjects`:

```csharp
tsPanel.LoadSmartObjects(sgdPath);
CwsDebugPage.RegisterDevice("Panel", tsPanel, sgdPath);   // objects show as "Button List", etc.
```

### Crestron Construct / CH5 panels

Register the panel with its contract, and its signals can additionally be addressed by contract
item path. Smart objects and their signals are then listed by contract name — `Main.Dpad` with
`Up`, `Down`, `Left`, `Right`, `Center` — rather than by number:

```csharp
CwsDebugPage.RegisterContract("Touchpanel", ch5Panel, "My Project.cse2j");
```

The manifest is the `<Project>.cse2j` file from the Construct project's
`output\contract\mapping\` folder, and the program must ship it as content so it exists on the
processor (a `CopyToOutputDirectory` entry in the `.csproj`). If it is missing or unreadable the
panel still registers and everything remains reachable by number — a warning in the log says so,
rather than failing quietly.

Digital rows show the signal's state and light a lamp while it is high, with buttons for
**Pulse**, **High** and **Low**; analog and serial rows are edited by typing a value and pressing
Enter. Values are written to the join itself — unlike Toolbox there is no separate "unforce", so a
value stands until the program or the panel changes it again.

On an **Out** row those same buttons simulate panel activity: the program's own logic runs just as
it would from a real press, so a button on the page can exercise the code behind a button on the
panel — no changes to your program required. Where that is unavailable, the buttons simply do not
appear and the row stays a live read-out.

Each row also has a **Log** toggle. With it on, the processor writes a line to the log every time
that signal changes:

```
Signal Panel Power Button (od1) -> true
Signal Panel Volume (a1) -> 46536
```

That gives a running trace of any join without adding a single `Log*` call to the program. The
lines are written on the processor, so they carry the moment the change happened and catch
transitions too brief to land on a poll. Digital edges are always logged; analog and serial have a
floor between entries so a dragged slider cannot flood the buffer. Logging a signal stops shortly
after the page stops asking for it, so closing the browser cannot leave a processor logging
indefinitely.

Forcing is disabled until it is armed on the page, and arming lapses after five idle minutes or
whenever the program restarts. Every force is recorded in the log with the address it came from.

### What stops the page updating

Three things are easy to confuse:

| Control | What it stops |
|---------|---------------|
| **Logging** | Recording on the processor. It does *not* stop the page requesting updates. |
| **Pause** | The page requesting updates. The processor keeps recording; resuming catches up. |
| *(automatic)* | Requests, while the tab is hidden and after fifteen idle minutes. |

Signals refresh about four times a second while the panel is open, so a button press shows up in
roughly a quarter of a second; the log refreshes every two seconds. Only one request is ever in
flight, so a slow connection simply refreshes less often. The automatic stop treats arriving
values as activity, so watching a panel being pressed keeps the page live even with no mouse or
keyboard use; it resumes on a click, a keystroke, or the tab being shown.

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

## A note on network access

The debug page has no login. Anyone who can reach the processor on the network can open the
page, read the log, clear it, or switch logging off — the same way Crestron's own built-in
web pages work. On a dedicated control-system network this is normally fine. If the processor
sits on a network shared with general office traffic or guests, keep in mind that the log is
visible to everyone on that network — so avoid logging passwords, door codes, or anything else
sensitive, and ask your IT/network administrator to place the processor on a restricted VLAN
if that's a concern.

The same applies to the Signals panel, which can change values on a running system — and, on an
**Out** row, make the program act as though the panel had been used. Both are switched off until
someone arms them, arming is forgotten whenever the program restarts or the page is reloaded, it
switches itself back off after five idle minutes, and every change is written to the log with the
address of the machine that sent it. For a system being handed over, set
`CwsDebugPage.ValueWritesAllowed = false` to leave the panel read-only while keeping the live view.

## Building

```bash
dotnet build CwsDebug/CwsDebug.csproj -c Release
```

## Documentation

A formatted reference is included: [`CwsDebug_Documentation.pdf`](CwsDebug_Documentation.pdf).
Release history is tracked in [`CHANGELOG.md`](CHANGELOG.md).

## License

[MIT](LICENSE) © 2026 Mark Jervis
