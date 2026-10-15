using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// One sensor as a sample taken from <see cref="IHardwareSensorSource"/>: what it is, and the value it
/// held at the moment of the sample.
/// </summary>
/// <param name="Value">
/// Null when the sensor is present but has no reading right now - which is normal, not an error: a
/// stopped fan, a disk that did not answer, or a sensor before its first update.
/// </param>
public readonly record struct HardwareSensor(
	string HardwarePath,
	string SensorName,
	SensorType SensorType,
	float? Value)
{
	/// <summary>This sample's stable identity.</summary>
	public SensorIdentifier Identifier => new(HardwarePath, SensorName, SensorType);
}
