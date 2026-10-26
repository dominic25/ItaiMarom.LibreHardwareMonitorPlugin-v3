
# ItaiMarom.LibreHardwareMonitorPlugin-v3

A plugin that reads system hardware metrics (CPU, GPU, memory, motherboard, storage, and network sensors) using LibreHardwareMonitor, exposes them as Macro Deck variables, and can set controllable fan and pump speeds from Macro Deck actions. Requires PawnIO and administrator rights for full sensor access (CPU/fan/voltage readings) and for fan control.

<a href="https://macro-deck.app/store/com.itaimarom.libre-hardware-monitor">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://assets.macro-deck.app/branding/Open%20in%20the%20Macro%20Deck%20Store%20Badge%20Dark.png">
    <img alt="Open in the Macro Deck Store" src="https://assets.macro-deck.app/branding/Open%20in%20the%20Macro%20Deck%20Store%20Badge%20Light.png" height="56">
  </picture>
</a>

## Features

Macro Deck 3 out-of-process rewrite of [ItaiMarom.LibreHardwareMonitorPlugin](https://github.com/dominic25/ItaiMarom.LibreHardwareMonitorPlugin).

Reads CPU, GPU, memory, motherboard, storage and network sensors through
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), exposes them
as Macro Deck variables - no separate monitoring application required - and offers actions that set a
controllable fan or pump's speed or hand it back to the motherboard's own curve. A controllable fan is
also a writable variable, so the built-in Slider widget can drive it.

## What this is (and is not)

Macro Deck 3 plugins run **out of process**: the plugin is its own .NET 10 console app that talks to
the host over the Macro Deck 3 plugin SDK. This repository is that app.

The Macro Deck 2 plugin was a **pure variable provider** - it declared no actions. That is still the
plugin's main job, and every variable it published keeps its old name. What is new is fan control: two
actions that write to a controllable fan or pump through the same hardware library, and a writable
variable for every sensor that has a control, so Macro Deck's built-in Slider can drive it. Watching a
fan on a button and not being able to change it was the one thing the port could not express.

## Requirements

- Macro Deck 3 (prerelease)

## ⚠️ System Requirements and Permissions ⚠️

For the plugin to read all available hardware metrics, especially **CPU temperatures, fan speeds, and voltages**, the following are mandatory:

1.  **Administrator Rights:** Macro Deck **must be started as an administrator**. This is required because the underlying hardware library needs elevated privileges to access system sensors.
    *   *Action:* Right-click Macro Deck's shortcut, go to *Properties* > *Advanced* > *Run as administrator*.
2.  **PawnIO:** The plugin requires the [PawnIO](https://pawnio.eu) library to be installed on the machine.

If these prerequisites are not met, the plugin will still function but will report "unavailable" for the restricted sensors.

## Requirements

- Macro Deck 3 (prerelease)

```
Directory.Build.props                  shared MSBuild settings (net10.0)
Directory.Packages.props               central, pinned package versions
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
  user-initiated act enumerates - browsing the catalog, or the settings window while it is open - and a
  read of the whole machine takes the same semaphore the loop polls behind, so neither can catch the
  hardware library mid-update.
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
  host to read the definition again so the slider settles on what the chip accepted. No plugin-specific
  widget is involved.
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
  control's reported minimum and maximum before it is written.
- **A missing fan is a result, not an exception.** A button saved against a fan that has since been
  removed returns `NOT_FOUND` with an explanation, so the flow engine reports a failure instead of a
  crashed action.


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
  it on every initialize, so what it holds *is* the eager variable set.

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
  or Macro Deck is not running as an administrator - the window says so in a line under its heading, and
  the log says the same thing at the moment the hardware is opened. The line names the readings, says why
  Windows keeps them out of reach, and says what to do about it, because a dash in a few hundred rows is
  not an explanation. The same sentence is also raised inside Macro Deck itself, where a user who never
  opens the window can still see it: a warning in the host's issue list, whose button opens the settings
  screen, and a notification when the plugin starts. All three are built from one sentence, so they cannot
  disagree about what is missing or about how to fix it.
- The window opens on the configuration the plugin is already running with, so reopening it and pressing
  Save cannot lose a pick, and unticking everything is how the old dialog's "delete all variables" button
  is reached.
- Saving posts the ticked sensors to the plugin, and the plugin sends the browser on to the host's OAuth
  callback carrying nothing but a key. The callback is a `GET` whose request line the host caps at 8 KB, so
  the selection travels in the POST: the address only ever says "this window finished".
- What it stores is the shape a migration writes - one entry, a `sensors` value and a `polling_rate` value
  - so the settings screen and the migration are two writers of one document, and a selection survives
  being reconfigured as well as it survives being upgraded.

## Build

The plugin targets .NET 10, so the machine that builds it needs the .NET 10 SDK - the runtime Macro Deck
ships is not enough, and an older SDK fails with `NETSDK1045: The current .NET SDK does not support
targeting .NET 10.0`.

```powershell
dotnet build
dotnet test
```

To produce a packaged plugin with the official CLI:

```powershell
dotnet tool install --global MacroDeck.Plugin.Cli --prerelease
macrodeck-plugin build
```

The CLI runs the `dotnet` it finds first on `PATH`. When .NET 10 lives somewhere else, put that directory
in front of it for the build rather than changing the machine's default SDK:

```powershell
$env:PATH = "<dotnet-10-sdk-directory>;" + $env:PATH
macrodeck-plugin build
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
      motherboard. Requires Macro Deck to run as administrator so the hardware library's driver loads.

## License

Apache-2.0. Third-party: LibreHardwareMonitor (MPL-2.0).
