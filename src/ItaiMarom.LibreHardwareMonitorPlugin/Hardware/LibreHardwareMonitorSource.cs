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

			foreach (var (sensor, native) in Walk(hardware, RootOf(hardware)))
			{
				sensors.Add(new HardwareSensor(sensor, native.Value, Range(native)));
			}
		}

		return sensors;
	}

	/// <inheritdoc />
	public IReadOnlyList<SensorIdentifier> ReadControls()
	{
		var controls = new List<SensorIdentifier>();
		foreach (var hardware in _computer.Hardware)
		{
			Update(hardware);

			// Which sensors carry a control is the library's answer, not the sensor type's: a super-I/O
			// chip exposes a Control sensor of its own beside the fan's RPM sensor, and only that one has
			// an IControl behind it.
			controls.AddRange(Walk(hardware, RootOf(hardware))
				.Where(entry => entry.Native.Control is not null)
				.Select(entry => entry.Sensor));
		}

		return controls;
	}

	/// <inheritdoc />
	public bool SetControl(string sensorId, float value)
	{
		if (FindControl(sensorId) is not { } control)
		{
			return false;
		}

		// The control's own range is the authority: the plugin asks in percentages, but a write outside
		// what the chip accepts is a request it never agreed to.
		control.SetSoftware(Math.Clamp(value, control.MinSoftwareValue, control.MaxSoftwareValue));
		return true;
	}

	/// <inheritdoc />
	public bool ResetControl(string sensorId)
	{
		if (FindControl(sensorId) is not { } control)
		{
			return false;
		}

		control.SetDefault();
		return true;
	}

	/// <inheritdoc />
	public void Dispose() => _computer.Close();

	/// <summary>The identity of one top-level device, which is what every sensor below it hangs off.</summary>
	private static HardwareLocation RootOf(IHardware hardware) =>
		HardwareLocation.Root(hardware.Identifier.ToString(), hardware.Name);

	/// <summary>
	/// The span a sensor's control accepts, or null when the library gave it no control. The chip owns the
	/// range, so it is read here rather than assumed: the plugin asks in percentages and the library holds
	/// the request to these same numbers.
	/// </summary>
	private static ControlRange? Range(ISensor sensor) =>
		sensor.Control is { } control
			? new ControlRange(control.MinSoftwareValue, control.MaxSoftwareValue)
			: null;

	/// <summary>Updates one piece of hardware and everything below it, because it does not do that itself.</summary>
	private static void Update(IHardware hardware)
	{
		hardware.Update();

		foreach (var subHardware in hardware.SubHardware)
		{
			Update(subHardware);
		}
	}

	/// <summary>
	/// Walks a device and everything below it, pairing each sensor with the identity the plugin keys it
	/// by and with the library's own <see cref="ISensor"/> - the only thing that knows whether the sensor
	/// carries a control.
	/// </summary>
	private static IEnumerable<(SensorIdentifier Sensor, ISensor Native)> Walk(
		IHardware hardware,
		HardwareLocation location)
	{
		foreach (var sensor in hardware.Sensors)
		{
			yield return (new SensorIdentifier(
				location,
				sensor.Identifier.ToString(),
				sensor.Name,
				sensor.SensorType)
			{
				Controllable = sensor.Control is not null,
			}, sensor);
		}

		foreach (var subHardware in hardware.SubHardware)
		{
			var subLocation = location.Child(subHardware.Identifier.ToString(), subHardware.Name);
			foreach (var entry in Walk(subHardware, subLocation))
			{
				yield return entry;
			}
		}
	}

	/// <summary>
	/// Finds the control a write names. There is no <see cref="Update"/> here on purpose: a control is
	/// attached to its sensor when the hardware is created, and its range belongs to the chip, so a write
	/// depends on nothing a poll refreshes.
	/// </summary>
	private IControl? FindControl(string sensorId)
	{
		foreach (var hardware in _computer.Hardware)
		{
			foreach (var (sensor, native) in Walk(hardware, RootOf(hardware)))
			{
				if (native.Control is { } control
					&& string.Equals(sensor.Id, sensorId, StringComparison.Ordinal))
				{
					return control;
				}
			}
		}

		return null;
	}
}
