using System.Globalization;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using SensorKind = LibreHardwareMonitor.Hardware.SensorType;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Migration;

/// <summary>
/// One migrated sensor as it is stored in the host's configuration entry: the whole of a
/// <see cref="SensorIdentifier"/>, flattened.
/// </summary>
/// <remarks>
/// <para>
/// The identity is stored rather than the request that produced it. A request is a display name and has
/// to be matched against the live hardware again at every start, which would mean probing every bus
/// before the plugin can say what variables it has - the exact thing the polling loop exists to keep off
/// the session's thread. The stored identity needs no hardware at all.
/// </para>
/// <para>
/// Every member is nullable because this is deserialized from text the host has held across upgrades: a
/// document that is missing a field must be rejected, not thrown over.
/// </para>
/// </remarks>
public sealed record MigratedSensor(
	string? Sensor,
	string? SensorName,
	string? SensorType,
	string? Hardware,
	string? HardwareName,
	string? HardwarePath,
	string? RootHardware,
	string? RootHardwareName)
{
	/// <summary>Flattens a sensor into the stored shape.</summary>
	public static MigratedSensor From(SensorIdentifier sensor)
	{
		ArgumentNullException.ThrowIfNull(sensor);

		return new MigratedSensor(
			sensor.Id,
			sensor.SensorName,
			sensor.SensorType.ToString(),
			sensor.Hardware.Id,
			sensor.Hardware.Name,
			sensor.Hardware.Path,
			sensor.Hardware.RootId,
			sensor.Hardware.RootName);
	}

	/// <summary>
	/// Rebuilds the sensor, or reports that the stored record is not complete enough to. A record that is
	/// not is dropped by the caller rather than defaulted, so a mangled entry cannot invent a variable.
	/// </summary>
	public bool TryRestore(out SensorIdentifier sensor)
	{
		sensor = null!;

		if (string.IsNullOrWhiteSpace(Sensor)
			|| string.IsNullOrWhiteSpace(SensorName)
			|| string.IsNullOrWhiteSpace(SensorType)
			|| string.IsNullOrWhiteSpace(Hardware)
			|| string.IsNullOrWhiteSpace(HardwareName)
			|| string.IsNullOrWhiteSpace(RootHardware)
			|| string.IsNullOrWhiteSpace(RootHardwareName)
			|| !Enum.TryParse<SensorKind>(SensorType, out var kind))
		{
			return false;
		}

		// The tree path is the one part that can be left out: it only qualifies a name, and the hardware
		// name is what the sensor is filed under when it is.
		var path = string.IsNullOrWhiteSpace(HardwarePath) ? HardwareName : HardwarePath;

		sensor = new SensorIdentifier(
			new HardwareLocation(Hardware, HardwareName, path, RootHardware, RootHardwareName),
			Sensor,
			SensorName,
			kind);

		return true;
	}

	/// <inheritdoc />
	public override string ToString() =>
		string.Create(CultureInfo.InvariantCulture, $"{HardwareName}/{SensorName} ({SensorType})");
}
