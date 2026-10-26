# ItaiMarom.LibreHardwareMonitorPlugin-v3

Reads the CPU, GPU, memory, motherboard, storage and network sensors of a Windows machine through
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) and publishes them as
Macro Deck 3 variables, with no separate monitoring application running. Fans and pumps the hardware
library can drive are exposed as writable variables and actions, so a button or the built-in Slider
widget can set their speed.

The plugin runs without administrator rights. PawnIO and elevation are optional: they are only needed for
the readings that go through a kernel driver - processor temperature and clocks, fan speeds, voltages and
drive health - and for fan control. Everything else (GPU, memory, storage and network sensors) works
without them.

<a href="https://macro-deck.app/store/com.itaimarom.libre-hardware-monitor">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://assets.macro-deck.app/branding/Open%20in%20the%20Macro%20Deck%20Store%20Badge%20Dark.png">
    <img alt="Open in the Macro Deck Store" src="https://assets.macro-deck.app/branding/Open%20in%20the%20Macro%20Deck%20Store%20Badge%20Light.png" height="56">
  </picture>
</a>

## Features

- **Every sensor as a variable** - CPU, GPU, memory, motherboard, storage and network readings, with the
  names the Macro Deck 2 plugin used, so an upgraded layout keeps working.
- **A browsable catalog** - every sensor on the machine, including the ones you did not tick, one binding
  away in Macro Deck's variable browser.
- **Fan and pump control** - two actions, *Set fan speed* and *Restore fan control*, plus a writable
  variable for every sensor that reports a software control, which is what the built-in **Slider** widget
  binds to. No plugin-specific widget is involved.
- **The polling rate you choose** - the rate set in the sensor window is how often the plugin reads the
  hardware and how often Macro Deck reads a variable, rather than the host's own five-second default.
- **A sensor window instead of a form** - the Macro Deck 2 dialog, carried over whole and served to your
  browser, with a live reading beside every sensor.

## Getting started

### 1. Pick the sensors you want

1. Open the plugin's settings in Macro Deck and press **Continue** under **Libre Hardware Monitor**. The
   sensor window opens in your browser.
2. The window is the hardware tree: hardware, then reading kind, then sensor, with the value each sensor
   reports right now. Tick the sensors you want to see in Macro Deck. A filter box and a count on each
   branch are there for machines that report a few hundred sensors, and a dash means "nothing to report
   right now" - a stopped fan, a drive that is not answering - never a zero.
3. Set the **polling rate** at the bottom of the window, in milliseconds.
4. Press **Save**.

What you tick becomes variables as soon as the save reaches Macro Deck. Unticking everything is how the
"remove all variables" button of the old plugin is reached. Nothing you skip is lost: every sensor on the
machine stays reachable from the variable catalog.

Three badges explain what a sensor can be used for:

- **controllable** - the hardware library attached a software control, so this fan or pump can be driven
  by the Slider widget and by the fan actions.
- **control below** on a branch - one of the sensors under it is controllable; the branch itself cannot be
  driven.
- **read-only** on a control sensor - the library created the control sensor but attached no control to
  it, so it can only be watched. A graphics card's fan duty is a common example.

### 2. The variables you get

There are two ways a sensor becomes a variable, and both are used the same way afterwards.

- **Ticked sensors** are declared variables. Their names are the ones the Macro Deck 2 plugin produced
  from the sensor, so `CPU Core #1 (Temperature)` is `{{ vars.cpu_core__1_temperature }}` and a button
  saved against the old plugin keeps working. If two sensors would produce the same name - two graphics
  cards, both with a `GPU Core` - the first keeps it and the rest get a hardware-qualified name.
- **Anything else** comes from the catalog on demand. Open Macro Deck's variable browser, pick the
  **Libre Hardware Monitor** catalog, and drill down hardware, reading kind, sensor. Binding one creates
  an ordinary variable under a name derived from the sensor, which you can then use anywhere. A fan bound
  from the catalog is just as writable as a ticked one, so it works in the Slider the same way.

A variable carries its unit and how to render it, so a voltage reads as `3.4 V` by itself, a load as
`13 %`, and a disk's used space as `19.4 GB`. There is nothing to append in the text.

### 3. Use them on a button

- **Show a reading.** Put a variable reference in any field that accepts a template - a widget's label,
  an action parameter, a script input - for example `CPU {{ vars.cpu_total_load }}`. Pick the variable
  from the browser instead of typing the name if you prefer; Macro Deck resolves both the same way.
- **Feed an action from it.** A templated field hands the reading to the action in place of the
  reference, so a notification can say what the GPU temperature is rather than referring to a variable
  name.
- **Set a fan speed.** Add the **Set fan speed** action, pick the fan or pump in the **Fan** dropdown
  (the list says whether the hardware driver, and therefore elevation, is what is hiding it), and give a
  **Speed** between 0 and 100 percent of that fan's own range.
- **Hand a fan back.** A speed set by software outlives the plugin process, so add **Restore fan control**
  to the same button, or to a "silence" button, to return the fan to the motherboard's curve.
- **Drag instead of press.** Bind a **Slider** widget to a controllable fan's variable. The track takes
  its range from the chip, the write happens when you let go rather than at every intermediate point, and
  the slider settles on what the chip accepted. The plugin also holds every write to the chip's own range,
  so a value outside it arrives clamped.

## Administrator rights and PawnIO are optional

Everything read through Windows itself - GPU, memory, storage and network sensors - needs neither. Two
other things are needed for the readings that go through LibreHardwareMonitor's kernel driver, and for
fan control; both are optional in the sense that the plugin installs, starts and publishes every other
sensor without them:

1. **PawnIO**, installed once from [pawnio.eu](https://pawnio.eu). It is the driver the hardware library
   loads its modules into.
2. **Administrator rights for Macro Deck.** Windows only lets an administrator open that driver, so
   without elevation it is installed but cannot be used. Right-click Macro Deck's shortcut, open
   *Properties* > *Advanced*, and tick *Run as administrator*.

These are what the affected readings - **processor temperatures and clocks, fan speeds, voltages and drive
health** - are read through. Without them those branches are not empty but absent from the sensor window,
because the library cannot enumerate the devices behind them, and a fan control does not exist at all.
The plugin says so rather than leaving a gap: a warning in Macro Deck's issue list whose button opens the
sensor window, a notification when it starts, and a line under the window's heading, all built from the
same sentence and naming what to do about it.

## Requirements

- Windows x64 - the platform the plugin is packaged for.
- Macro Deck 3 (prerelease).
- Optional: PawnIO and administrator rights, for processor temperature and clocks, fan speeds, voltages,
  drive health and fan control. See
  [Administrator rights and PawnIO are optional](#administrator-rights-and-pawnio-are-optional).

## Development

The plugin's internals - the polling loop, sensor identity and variable naming, the Macro Deck 2
migration, how the sensor window is served, the project layout, and how to build and package the plugin -
are in [DEVELOPMENT.md](DEVELOPMENT.md).

## License

Apache-2.0. Third-party: LibreHardwareMonitor (MPL-2.0).
