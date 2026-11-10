# CwsDebugExample

A small SIMPL#Pro program and Crestron Construct (CH5) panel that show CwsDebug in use: logging
at each level, exception logging in every handler, and `RegisterContract`, which lists the
panel's signals in the Signals panel by their Construct names.

The panel controls a simple room:

- **Sources**: a Widget List of five buttons. The program sends their labels when the panel comes
  online, and one is selected at a time (radio behaviour).
- **Volume**: a slider. The program echoes its level back as feedback, and logs a warning when
  the level rises above 80%.
- **Power**: a toggle. The program flips the power state and sends it back.

Sources and power are independent: choosing a source does not change power, and the reverse.

## Layout

```
CwsDebugExample/
  CwsDebugExample.cpz        the compiled program, ready to load
  Construct/                 Construct project (open CwsDebugExample.csln)
    output/                  build output, included prebuilt:
      CwsDebugExample.ch5z     the panel project to load
      contract/                contract code and CwsDebugExample.cse2j used by the program
  Program/
    CwsDebugExample.sln      solution with the program and the CwsDebug library project
    CwsDebugExample.csproj
    ControlSystem.cs         the whole program
```

The program references the CwsDebug library project in this repository
(`../../CwsDebug/CwsDebug.csproj`), so it always builds against the current source.

## Requirements

- .NET 8 SDK
- Crestron SIMPL# Pro SDK (restored from NuGet by the build)
- A 4-series processor or VC-4
- Crestron Construct, only to edit the panel

## Build

From this folder:

```bash
dotnet build Program/CwsDebugExample.csproj -c Release
```

This builds CwsDebug too, and produces `Program/bin/Release/net8.0/CwsDebugExample.cpz`. The
contract code is compiled from `Construct/output`, and `CwsDebugExample.cse2j` ships inside the
`.cpz`.

## Load

Both files are included prebuilt, so the example can be loaded without building anything.

1. Load `CwsDebugExample.cpz` (in this folder) onto the processor:
   - **4-series appliance:** copy it to the processor's `/program01` folder (for example with
     SFTP), then run `progload -p:1` at the processor's console (SSH). Remove any other
     `.cpz` from `/program01` first: a slot holds one program.
   - **VC-4:** add it as a program in the VC-4 web interface, then assign it to a room.
2. Load `Construct/output/CwsDebugExample.ch5z` to an XPanel or touch panel at IP ID 03.

To load your own build instead, use `Program/bin/Release/net8.0/CwsDebugExample.cpz` from the
build step above. The prebuilt `.cpz` was made with
`dotnet build Program/CwsDebugExample.csproj -c Release -p:ContinuousIntegrationBuild=true`,
which keeps the build machine's folder paths out of the debug symbols.

## Open the debug page

| Platform     | URL                                                                         |
|--------------|-----------------------------------------------------------------------------|
| Appliance    | `http://<processor>/cws/example/debugger.html`                              |
| VC-4         | `http://<server>/VirtualControl/Rooms/<roomId>/cws/example/debugger.html`   |

The program registers the page at `/example`, a non-default base path, so the segment stays in
the URL on VC-4 too.

## Things to try

The log shows the program starting, the panel coming online, and an entry for every source
change, volume move and power press.

Open **Signals**. The panel is listed as `Panel`, with its signals named from the contract:

1. Add a row for `Main.Slider` or for one of the `Main.WidgetList.Items[n]` buttons.
2. Arm the page: changes are disabled until it is armed, and arming lapses after five idle
   minutes or when the program restarts.
3. Use the **Out** buttons to simulate a press. The program runs the same handler as a real
   touch: a Pulse on a source button selects that source, and an Out level on the slider moves
   the volume (and logs a warning when it crosses 80%).
4. Use the **In** buttons to set what the panel shows without the program's logic running,
   for example the button labels or the slider position.

Turn on **Log** for a row to have every change of that signal written to the log.

## Editing the UI

Open `Construct/CwsDebugExample.csln` in Crestron Construct, make the changes and build. The
contract code and `CwsDebugExample.cse2j` regenerate into `Construct/output`, and the program
picks them up on its next build. If a control is renamed, update the matching names in
`ControlSystem.cs`.
