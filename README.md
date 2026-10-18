# ItaiMarom.LibreHardwareMonitorPlugin-v3

Macro Deck 3 out-of-process rewrite of [ItaiMarom.LibreHardwareMonitorPlugin](https://github.com/dominic25/ItaiMarom.LibreHardwareMonitorPlugin).

Reads CPU, GPU, memory, motherboard, storage and network sensors through
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) and exposes them
as Macro Deck variables - no separate monitoring application required.

## What this is (and is not)

Macro Deck 3 plugins run **out of process**: the plugin is its own .NET 10 console app that talks to
the host over the Macro Deck 3 plugin SDK. This repository is that app.

The Macro Deck 2 plugin was a **pure variable provider** - it declared no actions. The rewrite keeps
that shape.

## Requirements

- .NET SDK 10.0
- Macro Deck 3 (prerelease)

Some readings are taken through a kernel driver rather than through Windows: processor temperatures and
clocks, a motherboard's fan speeds and voltages, and a drive's health. The hardware library asks for
[PawnIO](https://pawnio.eu), which has to be installed on the machine, and Windows only opens it for an
administrator - so **start Macro Deck as an administrator** if you expect those readings: right-click
Macro Deck's shortcut, *Properties* > *Advanced* > *Run as administrator*. If Macro Deck starts with
Windows, elevate that startup entry too - a shortcut in the *Startup* folder, or a Task Scheduler task with
*Run with highest privileges*. Everything else is published either way, and the plugin says a line about it
in three places when they are out of reach: as a warning in Macro Deck's own issue list, with a button that
opens the settings screen; as a notification when the plugin starts, so it is seen without going looking;
and as a line under the heading of the sensor window. (The driver the Macro Deck 2 plugin's library
version used let any process read the machine, which is why the same sensor could work there and be
missing here.)

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
  Variables/
    SensorSelection.cs                 the sensors that become declared variables
    SensorVariable.cs                  a selected sensor and the name/id it is exposed under
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

```powershell
dotnet build
dotnet test
```

To produce a packaged plugin with the official CLI:

```powershell
dotnet tool install --global MacroDeck.Plugin.Cli --prerelease
macrodeck-plugin build
```

## Package versions

Every Macro Deck package is pinned by `MacroDeckSdkVersion` in `Directory.Packages.props`. The SDK
is prerelease and its surface changes between builds, so bumping it is a deliberate act. Override it
for a one-off build:

```powershell
dotnet build -p:MacroDeckSdkVersion=3.0.0-preview.10
```

The pinned build is `3.0.0-preview.10`, which is currently the newest published one. Note that the
documentation on `docs.macro-deck.app` describes `main`, which is ahead of it in a few places - for
example `MacroDeck.Sdk.Variables.VariableNames` and the `bytesPerSecond` semantic kind do not exist in
`preview.10` yet, which is why `VariableNaming` exists in this repository. It reproduces those rules
exactly and should be deleted once an SDK release ships them.

## Status

Step-by-step port from the Macro Deck 2 plugin:

- [x] Repository and project scaffolding
- [x] Hardware polling (out-of-process lifecycle)
- [x] Variable surface (declared variables + browsable catalog)
- [x] Macro Deck 2 settings migration
- [x] Actions - none, because the Macro Deck 2 plugin had none either; `Actions` is deliberately empty
      and the integration is a variable provider and nothing more.
- [x] Settings screen - the Macro Deck 2 configuration dialog, carried over as the checkbox tree it always
      was, opened in the browser from a step whose only job is that button.

## License

Apache-2.0. Third-party: LibreHardwareMonitor (MPL-2.0).
