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

## Layout

```
Directory.Build.props                  shared MSBuild settings (net10.0)
Directory.Packages.props               central, pinned package versions
src/ItaiMarom.LibreHardwareMonitorPlugin/
  Program.cs                           host bootstrap (CreatePlugin/RegisterIntegration/RunAsync)
  LibreHardwareMonitorIntegration.cs   the plugin's single integration
  manifest.json                        plugin identity, icon, entrypoints
  macrodeck-build.json                 per-RID publish recipe used by the CLI
  Assets/icon.svg                      plugin icon
  Localization/Strings.resx            default-language strings
tests/ItaiMarom.LibreHardwareMonitorPlugin.Tests/
```

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

## Status

Step-by-step port from the Macro Deck 2 plugin:

- [x] Repository and project scaffolding
- [ ] Hardware polling (out-of-process lifecycle)
- [ ] Variable provider
- [ ] Configuration UI (declarative UI model)
- [ ] Macro Deck 2 settings migration

## License

Apache-2.0. Third-party: LibreHardwareMonitor (MPL-2.0).
