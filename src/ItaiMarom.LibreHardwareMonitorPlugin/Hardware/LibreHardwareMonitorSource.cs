using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// The real <see cref="IHardwareSensorSource"/>: a LibreHardwareMonitor <see cref="Computer"/> with every
/// hardware group the Macro Deck 2 plugin enabled.
/// </summary>
/// <remarks>
/// The old plugin loaded a second assembly at runtime by reflection to dodge a reference it did not want
/// to take. There is no such constraint here: the library ships a <c>net10.0</c> target, so it is an
/// ordinary package reference and this class is all that is left of that dance.
/// </remarks>
public sealed class LibreHardwareMonitorSource : IHardwareSensorSource
{
	private readonly Computer _computer = new()
	{
		IsCpuEnabled = true,
		IsGpuEnabled = true,
		IsMemoryEnabled = true,
		IsMotherboardEnabled = true,
		IsControllerEnabled = true,
		IsNetworkEnabled = true,
		IsStorageEnabled = true,
	};

	/// <inheritdoc />
	public void Open() => _computer.Open();

	/// <inheritdoc />
	public void Close() => _computer.Close();

	/// <inheritdoc />
	public IReadOnlyList<HardwareSensor> Read(IReadOnlyCollection<string>? hardwareFilter)
	{
		HashSet<string>? filter = hardwareFilter is { Count: > 0 }
			? new HashSet<string>(hardwareFilter, StringComparer.Ordinal)
			: null;

		var sensors = new List<HardwareSensor>();
		foreach (var hardware in _computer.Hardware)
		{
			if (filter is not null && !filter.Contains(hardware.Name))
			{
				continue;
			}

			// IHardware.Update walks into the sub-hardware itself, which is where a motherboard's
			// super-I/O sensors live - the ones the old plugin never listed.
			hardware.Update();
			Collect(hardware, parentPath: string.Empty, sensors);
		}

		return sensors;
	}

	/// <inheritdoc />
	public void Dispose() => _computer.Close();

	private static void Collect(IHardware hardware, string parentPath, List<HardwareSensor> into)
	{
		var path = parentPath.Length == 0 ? hardware.Name : $"{parentPath}/{hardware.Name}";

		foreach (var sensor in hardware.Sensors)
		{
			into.Add(new HardwareSensor(path, sensor.Name, sensor.SensorType, sensor.Value));
		}

		foreach (var subHardware in hardware.SubHardware)
		{
			Collect(subHardware, path, into);
		}
	}
}
