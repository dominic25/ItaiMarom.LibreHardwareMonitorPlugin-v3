using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The shape the fan-control actions work against: the ids a saved button refers to, and a controllable
/// sensor of the kind a real machine exposes.
/// </summary>
internal static class TestFanActions
{
	/// <summary>
	/// The ids a saved Macro Deck button refers to, in the order the integration registers them. They are
	/// pinned here rather than read off the actions under test, because an id is persisted in every button
	/// that uses it and a test that followed a rename would never notice one.
	/// </summary>
	public static readonly string[] Ids = ["set-fan-speed", "restore-fan-control"];

	/// <summary>
	/// A controllable sensor of the kind a super-I/O chip really exposes: a <see cref="SensorType.Control"/>
	/// sensor of its own, sitting beside the fan's RPM sensor rather than being it.
	/// </summary>
	public static SensorIdentifier Control(string name) =>
		TestSensor.Create("Motherboard", name, SensorType.Control, subHardwareName: "Nuvoton NCT6798D");
}
