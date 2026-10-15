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
    SensorIdentifier.cs                stable sensor identity and variable naming
    SensorFormatting.cs                how a reading is presented (decimals, unit, semantic kind)
    HardwareSensor.cs                  one sample from a source
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
  reading the whole tree for the sensor picker).
- Each poll writes into an in-memory dictionary of the newest reading per watched sensor.
- Readers - the variable provider, once it lands - take the dictionary's current value. There is no
  `await` between a Macro Deck request and its answer, and no lock is ever held across a call into the
  hardware library.
- A reading older than `StaleAfter` is dropped, so a device that really went away stops reporting a value
  that is no longer true.


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
- [ ] Variable provider
- [ ] Configuration
- [ ] Macro Deck 2 settings migration

## License

Apache-2.0. Third-party: LibreHardwareMonitor (MPL-2.0).
