using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// Builds <see cref="SensorIdentifier"/> values with plausible library identifiers, so a test can say
/// what a sensor is in the terms it cares about and let the identifier look like a real one.
/// </summary>
/// <remarks>
/// The identifiers here follow LibreHardwareMonitor's shape - <c>/test/cpu/temperature/cpu-core-1</c> -
/// rather than being hand-written at each call site, because the id is what a variable id is derived
/// from and a test that gets it wrong is testing the wrong thing.
/// </remarks>
internal static class TestSensor
{
	/// <summary>Builds a sensor on a root device, or on a sub-device when a name is given.</summary>
	/// <param name="remote">
	/// The remote machine the sensor came from, or null for this machine. A remote sensor's root name is
	/// qualified the way the parser qualifies it, because that is what the window has to strip back off.
	/// </param>
	public static SensorIdentifier Create(
		string hardwareName,
		string sensorName,
		SensorType sensorType,
		string? subHardwareName = null,
		RemoteSourceDefinition? remote = null)
	{
		var rootId = remote is null
			? $"/test/{VariableNaming.ToDeclaredId(hardwareName)}"
			: $"/remote/{remote.Key}/test/{VariableNaming.ToDeclaredId(hardwareName)}";
		var rootName = remote?.Qualify(hardwareName) ?? hardwareName;
		var hardware = subHardwareName is null
			? HardwareLocation.Root(rootId, rootName, remote?.Key)
			: HardwareLocation.Root(rootId, rootName, remote?.Key)
				.Child($"{rootId}/{VariableNaming.ToDeclaredId(subHardwareName)}", subHardwareName);

		var id = string.Join(
			'/',
			hardware.Id,
			VariableNaming.ToDeclaredId(sensorType.ToString()),
			VariableNaming.ToDeclaredId(sensorName));

		return new SensorIdentifier(hardware, id, sensorName, sensorType);
	}
}
