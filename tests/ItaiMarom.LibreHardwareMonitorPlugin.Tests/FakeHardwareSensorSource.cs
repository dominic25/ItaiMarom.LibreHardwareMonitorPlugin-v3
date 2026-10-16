using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// A scripted <see cref="IHardwareSensorSource"/>, so the polling loop's lifecycle - start, read, stall,
/// stop, restart - can be tested without a machine full of sensors behind it.
/// </summary>
internal sealed class FakeHardwareSensorSource : IHardwareSensorSource
{
	private readonly List<Entry> _entries = [];

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

	/// <summary>The registered sensors, in the order they were added.</summary>
	public IReadOnlyList<SensorIdentifier> Sensors =>
		_entries.Select(entry => entry.Sensor).ToArray();

	/// <summary>Adds a sensor built from the pieces a test usually has to hand.</summary>
	public FakeHardwareSensorSource Add(
		string hardwareName,
		string sensorName,
		SensorType sensorType,
		float? value = null) =>
		Add(TestSensor.Create(hardwareName, sensorName, sensorType), value);

	/// <summary>Adds an already-built sensor to the fake hardware tree.</summary>
	public FakeHardwareSensorSource Add(SensorIdentifier sensor, float? value = null)
	{
		_entries.Add(new Entry(sensor, value));
		return this;
	}

	/// <summary>Sets the value the next poll reports for a sensor, or null to report "no reading".</summary>
	public FakeHardwareSensorSource Set(SensorIdentifier sensor, float? value)
	{
		var index = _entries.FindIndex(entry => entry.Sensor == sensor);
		if (index < 0)
		{
			throw new InvalidOperationException($"No such sensor: {sensor.Id}.");
		}

		_entries[index] = new Entry(sensor, value);
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

		return _entries
			.Where(entry => filter is null || filter.Contains(entry.Sensor.RootHardwareName))
			.Select(entry => new HardwareSensor(entry.Sensor, entry.Value))
			.ToArray();
	}

	/// <inheritdoc />
	public void Dispose() => Close();

	private sealed record Entry(SensorIdentifier Sensor, float? Value);
}
