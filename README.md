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

Each `Log*` method accepts an optional `string.Format` template plus arguments, e.g.
`CwsDebugPage.LogInfo("Connected to {0}:{1}", host, port);`

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

## Building

```bash
dotnet build CwsDebug/CwsDebug.csproj -c Release
```

## Documentation

A formatted reference is included: [`CwsDebug_Documentation.pdf`](CwsDebug_Documentation.pdf).

## License

[MIT](LICENSE) © 2026 Mark Jervis
