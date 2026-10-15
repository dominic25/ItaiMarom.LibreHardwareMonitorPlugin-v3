using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// A scripted <see cref="IHardwareSensorSource"/>, so the polling loop's lifecycle - start, read, stall,
/// stop, restart - can be tested without a machine full of sensors behind it.
/// </summary>
internal sealed class FakeHardwareSensorSource : IHardwareSensorSource
{
	private readonly List<Sensor> _sensors = [];

	/// <summary>How many times the loop opened the hardware.</summary>
	public int OpenCount { get; private set; }

	/// <summary>How many times the loop closed it.</summary>
	public int CloseCount { get; private set; }

	/// <summary>How many times the loop polled.</summary>
	public int ReadCount { get; private set; }

	/// <summary>The hardware filter handed to the most recent <see cref="Read"/>.</summary>
	public IReadOnlyCollection<string>? LastFilter { get; private set; }

	public bool IsOpen { get; private set; }

	/// <summary>Runs inside <see cref="Read"/>, on the loop's thread. Used to block or observe a poll.</summary>
	public Action? OnRead { get; set; }

	/// <summary>Registered sensors in the order they were added.</summary>
	public IReadOnlyList<(string HardwarePath, string SensorName, SensorType SensorType, float? Value)> Sensors =>
		_sensors.Select(sensor => (sensor.HardwarePath, sensor.SensorName, sensor.SensorType, sensor.Value)).ToArray();

	/// <summary>Adds a sensor to the fake hardware tree.</summary>
	public FakeHardwareSensorSource Add(string hardwarePath, string sensorName, SensorType sensorType, float? value = null)
	{
		_sensors.Add(new Sensor(hardwarePath, sensorName, sensorType, value));
		return this;
	}

	/// <summary>Sets the value the next poll reports for a sensor, or null to report "no reading".</summary>
	public FakeHardwareSensorSource Set(string sensorName, float? value)
	{
		var sensor = _sensors.Single(entry => entry.SensorName == sensorName);
		sensor.Value = value;
		return this;
	}

	/// <inheritdoc />
	public void Open()
	{
		OpenCount++;
		IsOpen = true;
	}

	/// <inheritdoc />
	public void Close()
	{
		CloseCount++;
		IsOpen = false;
	}

	/// <inheritdoc />
	public IReadOnlyList<HardwareSensor> Read(IReadOnlyCollection<string>? hardwareFilter)
	{
		ReadCount++;
		LastFilter = hardwareFilter;
		OnRead?.Invoke();

		var filter = hardwareFilter is { Count: > 0 }
			? new HashSet<string>(hardwareFilter, StringComparer.Ordinal)
			: null;

		return _sensors
			.Where(sensor => filter is null || filter.Contains(Root(sensor.HardwarePath)))
			.Select(sensor => new HardwareSensor(
				sensor.HardwarePath,
				sensor.SensorName,
				sensor.SensorType,
				sensor.Value))
			.ToArray();
	}

	/// <inheritdoc />
	public void Dispose() => Close();

	private static string Root(string hardwarePath)
	{
		var separator = hardwarePath.IndexOf('/', StringComparison.Ordinal);
		return separator < 0 ? hardwarePath : hardwarePath[..separator];
	}

	private sealed class Sensor(
		string hardwarePath,
		string sensorName,
		SensorType sensorType,
		float? value)
	{
		public string HardwarePath { get; } = hardwarePath;

		public string SensorName { get; } = sensorName;

		public SensorType SensorType { get; } = sensorType;

		public float? Value { get; set; } = value;
	}
}
