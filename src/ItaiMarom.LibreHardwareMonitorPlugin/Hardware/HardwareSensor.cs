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
/// <param name="Control">
/// The span the sensor's software control accepts, or null when it has none. It is carried with the
/// sample so the variable surface can offer a writable fan with the range the chip reports without
/// walking the hardware a second time.
/// </param>
public readonly record struct HardwareSensor(
	SensorIdentifier Sensor,
	float? Value,
	ControlRange? Control = null);

