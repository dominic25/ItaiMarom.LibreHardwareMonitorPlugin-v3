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

			// A device does not update what it owns. The motherboard's own Update is a documented no-op -
			// its super-I/O chip is a piece of hardware of its own, and that is where the fans and the
			// voltages are read - so the whole tree is walked. The old plugin did the same with a visitor;
			// it just never listed what it found down there.
			Update(hardware);
			Collect(hardware, HardwareLocation.Root(hardware.Identifier.ToString(), hardware.Name), sensors);
		}

		return sensors;
	}

	/// <inheritdoc />
	public void Dispose() => _computer.Close();

	/// <summary>Updates one piece of hardware and everything below it, because it does not do that itself.</summary>
	private static void Update(IHardware hardware)
	{
		hardware.Update();

		foreach (var subHardware in hardware.SubHardware)
		{
			Update(subHardware);
		}
	}

	private static void Collect(IHardware hardware, HardwareLocation location, List<HardwareSensor> into)
	{
		foreach (var sensor in hardware.Sensors)
		{
			var identifier = new SensorIdentifier(
				location,
				sensor.Identifier.ToString(),
				sensor.Name,
				sensor.SensorType);

			into.Add(new HardwareSensor(identifier, sensor.Value));
		}

		foreach (var subHardware in hardware.SubHardware)
		{
			Collect(subHardware, location.Child(subHardware.Identifier.ToString(), subHardware.Name), into);
		}
	}
}
