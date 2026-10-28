# Development notes

How the plugin is built and why it is built that way. The user-facing guide - installing it, picking
sensors, the variables it publishes and putting them on a button - is in [README.md](README.md).

## What this is (and is not)

Macro Deck 3 plugins run **out of process**: the plugin is its own .NET 10 console app that talks to
the host over the Macro Deck 3 plugin SDK. This repository is that app.

The Macro Deck 2 plugin was a **pure variable provider** - it declared no actions. That is still the
plugin's main job, and every variable it published keeps its old name. What is new is fan control: two
actions that write to a controllable fan or pump through the same hardware library, and a writable
variable for every sensor that has a control, so Macro Deck's built-in Slider can drive it. Watching a
fan on a button and not being able to change it was the one thing the port could not express.

## Project structure

```
Directory.Build.props                  shared MSBuild settings (net10.0)
Directory.Packages.props               central, pinned package versions
build-packages.ps1                     builds the store package and a side-by-side test package
build-packages.cmd                     the same flow from a shell that will not run a .ps1
src/ItaiMarom.LibreHardwareMonitorPlugin/
  Program.cs                           host bootstrap (CreatePlugin/RegisterIntegration/RunAsync)
  LibreHardwareMonitorIntegration.cs   the plugin's single integration
  VariableNaming.cs                    canonical variable-name rules (until the SDK ships its own)
  Hardware/
    IHardwareSensorSource.cs           the seam between the polling loop and the hardware library
    LibreHardwareMonitorSource.cs      the real LibreHardwareMonitor Computer
    HardwareMonitor.cs                 the background polling loop and its reading cache
    HardwareMonitorOptions.cs          polling interval and staleness
    KernelAccess.cs                    why the driver-only readings are missing, in a sentence
    IAccessDiagnosis.cs                the seam over that answer, for a test that has to know it
    IHardwareElevation.cs              the seam that starts and reports an elevated helper
    ElevatedHardwareSource.cs          the source the plugin polls: local, then the helper
    HardwareHost.cs                    the elevated helper's entry point
    HardwareHostOptions.cs             the helper's command line
    HardwareHostExitCodes.cs           the codes the helper exits with, and what each one means
    HardwareHostSession.cs             the helper's end of the pipe: connect, handshake and requests
    HardwareHostHandler.cs             the requests carried out against the helper's hardware
    HardwareHostClient.cs              the plugin's end of that pipe: accepts the helper, sends requests
    HardwareHostProtocol.cs            the framing and wire records
    HardwareHostLauncher.cs            starts the helper through UAC and owns its process
    HardwareHostLog.cs                 the helper's own log file
    JobObject.cs                       best-effort kill-on-close for the helper
    HardwareLocation.cs                where a sensor sits in the hardware tree
    SensorIdentifier.cs                stable sensor identity and variable naming
    SensorFormatting.cs                how a reading is presented (decimals, unit, semantic kind)
    HardwareSensor.cs                  one sample from a source
    ControlRange.cs                    the span a sensor's software control accepts
  Actions/
    ControlAction.cs                   what the fan actions share: sensor picker, options, executor
    SetFanSpeedAction.cs               set a controllable fan or pump to a percentage
    RestoreFanControlAction.cs         hand a fan or pump back to the motherboard's curve
  Variables/
    SensorSelection.cs                 the sensors that become declared variables
    SensorVariable.cs                  a selected sensor and the name/id it is exposed under
    SensorDefinition.cs                the parts of a definition that come from the sensor
    SensorVariableNaming.cs            collision policy for names and declared ids
    SensorCatalogTable.cs              the hardware tree flattened for browsing
    SensorVariableProvider.cs          the IVariableProvider: reads, catalog, subscriptions
  Migration/
    LegacySensorRequest.cs             one line of the old plugin's requestedSensors setting
    LegacySensorSelection.cs           turns a request into the sensors the machine has now
    MacroDeck2Migration.cs             the IIntegrationMigration that claims the old settings
    MigratedSensor.cs                  one sensor as the host configuration stores it
    MigratedSensorSelection.cs         reads the migrated selection back at startup
  Configuration/
    LibreHardwareMonitorConfigFlow.cs  the settings screen: the step that opens the classic window
    ClassicWindowServer.cs             the loopback server: the window's page, and the readings it asks for
    ClassicWindowPage.cs               the old dialog, rendered server-side with a live reading per sensor
    SensorReadingSource.cs             the window's value column: one full read of the machine per refresh
    SensorCategory.cs                  the window's tree: hardware, reading kind, sensors
  Issues/
    MissingReadingsIssueProvider.cs    the warning about readings the driver would answer with
  manifest.json                        plugin identity, icon, entrypoints
  macrodeck-build.json                 per-RID publish recipe used by the CLI
  Assets/icon.svg                      plugin icon
  Localization/Strings.resx            default-language strings
tests/ItaiMarom.LibreHardwareMonitorPlugin.Tests/
```

## How the polling works

The hardware library is not thread-safe and a single poll can block for as long as a disk takes to answer
a SMART query, so it is never touched from anything that serves the Macro Deck session:

- `HardwareMonitor` runs one background loop. It is the only caller of `IHardwareSensorSource`, and
  `SemaphoreSlim` serialises that caller against the two other places hardware is touched (opening, and
  reading the whole tree for the sensor window).
- Each poll writes into an in-memory dictionary of the newest reading per watched sensor.
- Readers - the variable provider - take the dictionary's current value. There is no `await` between a
  Macro Deck request and its answer, and no lock is ever held across a call into the hardware library.
- A reading older than `StaleAfter` is dropped, so a device that really went away stops reporting a value
  that is no longer true.
- Every variable definition declares the loop's polling interval as its `RefreshInterval`, so Macro Deck
  reads a widget's variable at the configured rate instead of falling back to its own five-second default.
  The host polls its variables on a 250 ms tick, so that is the finest update a widget can see even when
  the loop itself runs faster; the value is still whatever the last poll cached.

## How the variables are exposed

The Macro Deck 2 plugin's variables were whatever the user had configured, and a layout referred to one by
name (`{{cpu_core__1_temperature}}`). Both halves of that are preserved:

- **Declared (eager) variables** - the sensors the user - or the Macro Deck 2 migration - selected. Each is
  registered with the host under the exact name the old plugin published, so an upgraded layout resolves
  unchanged. Nothing is declared until a configuration produces a selection;
  `VariablesDependOnConfiguration` tells the host to ask again when one changes.
- **A browsable catalog** - every sensor in the machine, grouped by top-level hardware. A user reaches
  anything the old plugin did not have, binds it by name, and the binding is stored under the sensor's
  library identifier - which does not move when a display name is re-derived.

A few decisions worth knowing about:

- **Names collide** (`GPU Core` on two graphics cards). The first sensor keeps the old name; the rest get a
  hardware-qualified one. Which is "first" is decided by name and then by device path, not by enumeration
  order, so a restart cannot renumber a user's buttons.
- **Ids are derived from the library identifier, not the name**, because the identifier is what is unique
  and stable. `VariableDefinitionId.FromName` returns an empty id for a name with an underscore in it -
  which is what every old name has - so every eager definition sets its `Id` explicitly.
- **A poll and a read never touch the hardware library directly.** A read answers from the cache and says
  "unavailable" for anything it has not seen, so a profile opened on a machine without that hardware binds
  and reads as unavailable rather than blocking the session on enumeration. Only a deliberate,
  user-initiated act enumerates - browsing the catalog, attaching the provider, or the settings window
  while it is open - and a read of the whole machine takes the same semaphore the loop polls behind, so
  neither can catch the hardware library mid-update.
- **The catalog is enumerated when the provider attaches.** The host restores the declarations of the
  catalog bindings it saved and resolves them before it subscribes, so a provider that enumerated only on
  subscribe would leave a saved Slider read-only for a reconcile. Attaching before that step is what makes
  a bound fan writable straight after a restart.
- **Values are not truncated.** The old plugin stored some sensor kinds through `(int)value`; here the full
  precision is kept and `DecimalPlaces` reproduces the rendered text the old plugin showed.

## How fan control works

LibreHardwareMonitor exposes a control on a sensor of its own - usually a `Control` sensor beside a fan's
RPM sensor on a super-I/O chip - and that control is only created when its kernel driver is loaded. The
plugin therefore drives exactly the sensors that report one:

- **Two actions.** **Set fan speed** takes a 0-100 slider and writes it as a software control; **Restore
  fan control** returns the same sensor to the hardware's own curve. The undo matters: a software value
  survives the plugin process, so without it a fan would keep a speed set once until the machine restarts.
- **The same fans are writable variables.** A sensor with a control is declared with `Write`, so Macro
  Deck's own **Slider** widget can bind to a declared variable or to a catalog binding and drive it
  two-way. The reading carries the control's reported range and a one-percent step, the drag commits on
  release rather than writing at every intermediate point, and after an applied write the plugin asks the
  host to read the definition again so the slider settles on what the chip accepted. A catalog binding
  restored at boot resolves after an attach-time enumeration, so it is still writable across a restart
  instead of only after the user rebinds it. No plugin-specific widget is involved.
- **The fan list is built on demand.** The host reads `Actions` before it initializes the integration, so
  the definitions are constructed without touching hardware. `GetDynamicOptionsAsync` enumerates the
  controls only when the action editor opens the picker, and opens the hardware if the polling loop has
  not.
- **The list explains itself.** A process that cannot open the driver cannot see a control at all, so an
  empty list is usually a fact about the process rather than about the machine. The picker says which it
  is: it follows the monitor's own driver diagnosis, and only mentions elevation when the driver is
  actually what is missing.
- **Writes share the polling gate.** Every control read and write goes through `HardwareMonitor`, behind
  the same `SemaphoreSlim` as the loop, so the hardware library is never caught mid-update.
- **The control's own range wins.** The slider offers a percentage, but the value is clamped to the
  control's reported minimum and maximum before it is written. A chip that reports a range no slider can
  use - non-finite, reversed, or zero width - has no range to clamp against, and the request is left to
  the library.
- **A missing fan is a result, not an exception.** A button saved against a fan that has since been
  removed returns `NOT_FOUND` with an explanation, so the flow engine reports a failure instead of a
  crashed action.
- **A failed write has one answer.** A write that throws because the device vanished under the polling
  gate is reported as `Unavailable`, the same as a control that is no longer there: the value was not
  applied and the sensor cannot be driven right now.

## How the Macro Deck 2 migration works

Macro Deck 2 asked the user to build the sensor list themselves, and a layout refers to one by name, so a
port that ignored those settings would leave an upgraded layout with buttons that resolve to nothing. The
integration implements `IIntegrationMigration` for `MigrationSource.MacroDeck2` and claims the old plugin's
settings file:

- The old plugin declared no actions, so `ClaimedActionSources` is empty and `MigrateActionAsync` answers
  `null` for everything. A button belonging to another plugin is never touched.
- `requestedSensors` is a list of `(hardware, type, sensor)` triples, and the third element is a *display*
  name, not an identifier - so it is matched, not looked up. The token, reduced the way the old plugin
  reduced it, must be equal, and the recorded hardware must be the one the sensor sits under. Every match
  of every request is taken, because that is what the old plugin did: a machine with two identical disks
  published both.
- The match happens once, at migration time, and it is the *resolved identity* that is stored - the
  sensor's library id and name, its kind, and its place in the hardware tree. A display name would have to
  be matched again at every start, and matching means enumerating the machine's buses before the plugin can
  say what it has, which is exactly the thing the polling loop exists to keep off the session's thread.
- The old `pollingRate` is carried across and clamped to the loop's own bounds.
- The result is one configuration entry, `Macro Deck 2 sensors`: a `sensors` value holding the JSON list,
  and a `polling_rate` value. It carries no secrets and is not meant to be edited - the integration reads
  it on every initialize, so what it holds *is* the eager variable set. The old plugin had no startup
  elevation option, so the migration does not write `auto_elevate`: it stays unset until the user answers
  the question in the settings window, and an unset value reads back as off.

A sensor that is no longer present simply does not come back, which is what happened to it in Macro Deck 2
too.

## How the settings screen works

Macro Deck 3 has no tree control among the fields a plugin may declare, and what is being ported is a tree,
so the settings screen is the old dialog itself, served to the browser:

- The screen declares no fields at all. Its one button opens a page the plugin serves on a loopback port,
  and that page *is* the configuration dialog: the hardware, then the reading kind, then the sensor, with
  the polling rate under it. A flattened form of grouped multi-selects standing in for the tree was tried
  and removed: it is not the dialog that was asked for, and the tree is the point of it.
- The tree is the old one node for node. The reading kind is named the way the hardware library names it
  (`SmallData`, not `Small Data`), each sensor is labelled the way the old plugin labelled it
  (`CPU Core #1_Temperature` - the text its variable names were built from), and both levels keep the
  library's own order rather than a sorted one. A filter box and a count on each branch are the only
  additions, for machines that report a few hundred sensors.
- The tree also lists the sensors of sub-devices, which the old dialog could not see: a motherboard's
  super-I/O chip is where the voltages and the fan speeds live, and those sensors are still that
  motherboard's third-level leaves rather than a level of their own. (Each device's own `Update` is a
  no-op for what it owns, so the plugin walks the whole tree to refresh them.)
- Every sensor carries the reading it is reporting right now, and the open page asks for them again once a
  second, so what is being picked is a moving value rather than a name. The cell holds the sensor's id
  rather than a position, so a refresh can only ever fill in the row it belongs to, and ticking a box,
  opening a branch or typing in the filter is never disturbed by one. A sensor with nothing to report -
  a stopped fan, a drive that is not answering - shows a dash, never a zero.
- A fan or pump that can be driven carries a `controllable` badge after its name, and its tooltip says the
  built-in Slider widget can write to it. Every hardware and reading-kind branch above such a sensor carries
  a dashed `control below` marker, so a collapsed `Control` branch still points at what it holds; the branch
  marker is deliberately different from the sensor's, because a branch itself cannot be driven.
- A `Control` sensor the hardware library attached no software control to says `read-only` instead, with a
  tooltip saying it can only be watched. The library creates those too - a graphics card's fan duty is one -
  so an unbadged sensor is never an unexplained gap in what the screen claims about the machine.
- When the readings that need the hardware library's kernel driver are out of reach - it is not installed,
  or neither the plugin nor an elevated helper can open it - the window says so in a line under its
  heading, and the log says the same thing at the moment the hardware is opened. The line names the
  readings, says why Windows keeps them out of reach, and says what to do about it, because a dash in a
  few hundred rows is not an explanation. The same sentence is also raised inside Macro Deck itself,
  where a user who never opens the window can still see it: a warning in the host's issue list, whose
  button either starts the elevated helper or opens the settings screen, and a notification when the
  plugin starts. All three are built from one sentence, so they cannot disagree about what is missing or
  about how to fix it.
- The window opens on the configuration the plugin is already running with, so reopening it and pressing
  Save cannot lose a pick, and unticking everything is how the old dialog's "delete all variables" button
  is reached.
- The **Start elevated readings at startup** checkbox sits beside the polling rate. It is the explicit
  answer to whether the plugin should ask Windows for administrator rights at every start, and it opens on
  the stored preference. Saving the window writes it either way, so unticking it is a real "no" rather
  than an absent value. It is also the only thing that writes the preference: the issue's button starts a
  helper for the session and writes nothing.
- Saving posts the ticked sensors to the plugin, and the plugin sends the browser on to the host's OAuth
  callback carrying nothing but a key. The callback is a `GET` whose request line the host caps at 8 KB, so
  the selection travels in the POST: the address only ever says "this window finished".
- What it stores is the shape a migration writes - one entry, a `sensors` value and a `polling_rate` value,
  plus the `auto_elevate` preference the migration leaves unset - so the settings screen and the migration
  are two writers of one document, and a selection survives being reconfigured as well as it survives
  being upgraded.

## How the elevated hardware helper works

The driver-backed readings and fan control need a process with administrator rights. Running Macro Deck
itself elevated prompts at every start and puts the whole host in an elevated process, so the plugin
starts unelevated and moves the privilege into a helper it owns:

- **The plugin starts local.** `ElevatedHardwareSource` begins with an ordinary
  `LibreHardwareMonitorSource`. The issue list says what the driver would add, and its button is
  **Enable elevated readings** when the source can be elevated.
- **The button starts the same binary again.** `WindowsHardwareHostLauncher` starts the plugin's apphost
  with `--hardware-host` through `Verb = "runas"`, so the UAC prompt is the one the user sees. A
  dismissed prompt (`ERROR_CANCELLED`, 1223) is a result rather than an exception: the plugin keeps the
  readings it already has and the issue stays up. The package ships the apphost, so the prompt names the
  plugin rather than `dotnet`. The manifest's entrypoint stays the `.dll` - the CLI requires a
  framework-dependent entrypoint to be a managed assembly - and the launcher finds the apphost beside it.
- **The startup prompt is the setting, not the button.** When the stored `auto_elevate` preference is
  true, the plugin's first initialize starts the helper itself instead of waiting for the issue to be
  opened. The attempt is fire-and-forget so the host's handshake never waits for a UAC prompt, it is gated
  on `CanElevate` so a missing driver or an already-elevated process does not prompt, and only the first
  initialize of the process counts as a start: a reconnect or a settings save cannot produce a second
  prompt, and ticking the box does not raise one immediately. The issue's button stays a one-off - it
  enables the helper for the session and answers with where the setting lives - so the preference is only
  ever written by the settings window.
- **The helper owns the hardware.** `HardwareHost` creates the only `Computer`, opens it, and serves a
  narrow named-pipe protocol: `hello`, `open`, `close`, `read`, `controls`, `set`, `reset`, `ping` and
  `stop`. `HardwareHostHandler` carries the requests out; a hardware failure is an answer for that
  request, not a reason to end the helper.
- **The plugin proxies the source.** `HardwareHostClient` is an `IHardwareSensorSource` whose calls
  travel down the pipe. `ElevatedHardwareSource` closes the local hardware before the helper opens it
  and hands every later read, control list and write to the helper. The polling loop, cache and variable
  surface do not know the difference.
- **A helper that dies falls back.** A broken pipe drops back to the local source and the issue list
  offers elevation again. A write that was in flight is reported as a failure rather than silently
  retried locally, because the local process cannot reach the control.
- **The helper cannot outlive the plugin.** The pipe closing ends it, it watches the parent process, and
  the plugin assigns it to a kill-on-close job object where Windows allows that. The job object is best
  effort: an unelevated process cannot always open an elevated one with the rights
  `AssignProcessToJobObject` needs.
- **The pipe is not a public API.** Its name carries 128 random bits, the first message carries a 256-bit
  token, and `GetNamedPipeClientProcessId` must name the helper the launcher started. The token and the
  process check are both there because a pipe name can be discovered but a process id cannot be borrowed.
  The pipe's DACL names the current user and `BUILTIN\Administrators`, so consent elevation and
  over-the-shoulder elevation both reach the pipe; the token and process id are what decide who is served.
- **The plugin owns the pipe, not the helper.** A pipe created by an elevated process carries the elevated
  integrity level, and an unelevated client is refused write access to it whatever its DACL says - and
  `PipeOptions.CurrentUserOnly` refuses over-the-shoulder elevation outright, because on Windows it compares
  elevation as well as account. So the plugin creates the pipe before the UAC prompt and the elevated helper
  connects to it, which makes the helper the pipe's client and the protocol's server: it presents the
  token, checks that the pipe's server process is the plugin that started it, and answers requests from
  there. The helper also refuses to serve at all when Windows did not give it administrator rights, so a
  limited helper cannot pass itself off as an elevated one.
- **The helper is windowless and logs to a file.** It is the same `WinExe` binary, started with
  `--hardware-host`; it has no host session to send `log.publish` to, so it writes
  `%LOCALAPPDATA%\MacroDeck\logs\libre-hardware-monitor-host.log`.

Accepted constraints: two processes rather than one, one UAC per Macro Deck start (UAC cannot remember a
choice), and the prompt appears on the PC rather than on the deck. A scheduled task or a service would
remove the per-start prompt but adds a protected install path and an update path. If PawnIO ever exposes
non-admin read-only access, the helper goes away entirely.

## Build

The plugin targets .NET 10, so the machine that builds it needs the .NET 10 SDK - the runtime Macro Deck
ships is not enough, and an older SDK fails with `NETSDK1045: The current .NET SDK does not support
targeting .NET 10.0`.

```powershell
dotnet build
dotnet test
```

To produce the installable `.macroDeckPlugin` artifacts, run:

```powershell
.\build-packages.ps1
```

The script runs the test suite and then writes two packages to `dist/`:

| Variant | Plugin id | Can be installed with |
| --- | --- | --- |
| Store | `com.itaimarom.libre-hardware-monitor` | - |
| Test | `com.itaimarom.libre-hardware-monitor.test` | the store package |

Both packages contain the same source. The test package has a distinct id and a `(Test)` display name,
which is what lets Macro Deck keep it installed next to the store package instead of replacing it, and it
is compiled differently in the two places where the host keys things globally:

- **Localization scope.** The analyzer derives the scope from the project's `manifest.json`, which is the
  store one even when the package is built against the test manifest. The script sets
  `MacroDeckLocalizationScope` to the test id for that build, or the host rejects the catalog and every
  string shows up as a raw key.
- **Variable names.** Macro Deck's variable names are global, so the second plugin to declare
  `core_max_temperature` is refused. The test build defines `LHM_TEST_BUILD`, which prefixes every
  declared variable name with `test_`.

Use `-Variant Store` or `-Variant Test` to build one, and `-SkipTests` to skip the test run. The script
finds the .NET 10 SDK in the usual places; pass `-DotnetRoot <directory>` when it lives somewhere else.
`build-packages.cmd` is the same flow for a shell that will not run a `.ps1` directly. After each build
the script reads the packaged assembly back and fails if it carries the wrong localization scope, so a
stale `obj/` cannot silently ship a package whose strings do not resolve.

The script needs the official CLI:

```powershell
dotnet tool install --global MacroDeck.Plugin.Cli --prerelease
```

Under the hood it runs `macrodeck-plugin build` once per variant. For a single manual package:

```powershell
macrodeck-plugin build --output dist
```

The CLI runs the `dotnet` it finds first on `PATH`. When .NET 10 lives somewhere else, put that directory
in front of it for the build rather than changing the machine's default SDK:

```powershell
$env:PATH = "<dotnet-10-sdk-directory>;" + $env:PATH
macrodeck-plugin build --output dist
```

## Status

Step-by-step port from the Macro Deck 2 plugin:

- [x] Repository and project scaffolding
- [x] Hardware polling (out-of-process lifecycle)
- [x] Variable surface (declared variables + browsable catalog)
- [x] Macro Deck 2 settings migration
- [x] Settings screen - the Macro Deck 2 configuration dialog, carried over as the checkbox tree it always
      was, opened in the browser from a step whose only job is that button.
- [x] Fan control - actions that set a controllable fan or pump's speed and hand it back to the
      motherboard. Requires the elevated hardware helper, or Macro Deck running as administrator, so
      the hardware library's driver loads.
- [x] Elevated hardware helper - the plugin starts unelevated and starts the same binary as an
      administrator on demand, so Macro Deck itself does not have to run elevated.

## Maybe

### Fan curves

A temperature-to-speed curve per fan (or per fan group, once groups exist), evaluated by the polling
loop. Configured in the settings window as a **Fan curves** tab - source sensor, points, minimum,
hysteresis - not in a widget: a curve belongs to a fan, and two tiles must not disagree about it.

- Start with four predefined curves (Quiet, Balanced, Performance, Full) and a **Set fan preset**
  action; custom points later.
- The deck switches modes and presets and shows state (`fan_mode`, `fan_curve_target`,
  `fan_curve_active`); it does not draw the curve. The built-in Slider stays the manual override and
  pauses the curve when touched.
- Safety: clamp per fan, never below the configured minimum, 100 % above a critical temperature, hold
  the last target if the source sensor disappears.
- Not before a fan can be controlled reliably on real hardware; a curve on top of an unverified write
  path is a way to cook something.

