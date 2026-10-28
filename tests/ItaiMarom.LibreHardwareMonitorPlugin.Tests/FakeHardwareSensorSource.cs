using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// A scripted <see cref="IHardwareSensorSource"/>, so the polling loop's lifecycle - start, read, stall,
/// stop, restart - can be tested without a machine full of sensors behind it.
/// </summary>
internal sealed class FakeHardwareSensorSource : IHardwareSensorSource, IAccessNoteSource
{
	private readonly List<Entry> _entries = [];
	private readonly Dictionary<SensorIdentifier, FakeControl> _controls = [];

	/// <summary>How many times the loop opened the hardware.</summary>
	public int OpenCount { get; private set; }

	/// <summary>How many times the loop closed it.</summary>
	public int CloseCount { get; private set; }

	/// <summary>How many times the loop polled.</summary>
	public int ReadCount { get; private set; }

	/// <summary>How many times the control list was asked for.</summary>
	public int ControlReadCount { get; private set; }

	/// <summary>The hardware filter handed to the most recent <see cref="Read"/>.</summary>
	public IReadOnlyCollection<string>? LastFilter { get; private set; }

	/// <summary>The sensor id handed to the most recent <see cref="SetControl"/>, or null.</summary>
	public string? LastSetSensorId { get; private set; }

	/// <summary>The value handed to the most recent <see cref="SetControl"/>, already clamped by the fake.</summary>
	public float? LastSetValue { get; private set; }

	/// <summary>The sensor id handed to the most recent <see cref="ResetControl"/>, or null.</summary>
	public string? LastResetSensorId { get; private set; }

	public bool IsOpen { get; private set; }

	/// <summary>What the source reports it still cannot read, when a test wants an elevated note.</summary>
	public string? AccessNote { get; set; }

	/// <summary>Runs at the start of <see cref="Open"/>. Used to simulate hardware that will not open.</summary>
	public Action? OnOpen { get; set; }

	/// <summary>Runs inside <see cref="Read"/>, on the loop's thread. Used to block or observe a poll.</summary>
	public Action? OnRead { get; set; }

	/// <summary>
	/// Runs at the start of <see cref="SetControl"/>, on the caller's thread. Used to simulate a device
	/// that throws instead of accepting the value.
	/// </summary>
	public Action? OnSetControl { get; set; }

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

	/// <summary>
	/// Adds a control the action can drive. The sensor does not have to be in the read set: a control
	/// sensor is present whether or not anything watches its reading.
	/// </summary>
	/// <remarks>
	/// The real hardware walk marks a sensor controllable when it finds an <c>IControl</c> attached, so
	/// adding a fake control marks the sensor the same way - and adds it to the read set when it was not
	/// there, because a control a catalog cannot see is a fan no variable can offer.
	/// </remarks>
	public FakeHardwareSensorSource AddControl(
		SensorIdentifier sensor,
		float min = 0f,
		float max = 100f,
		float value = 0f,
		ControlMode mode = ControlMode.Undefined)
	{
		_controls[sensor] = new FakeControl(min, max, value, mode);

		var controllable = sensor with { Controllable = true };
		var index = _entries.FindIndex(entry => entry.Sensor == sensor);
		if (index < 0)
		{
			_entries.Add(new Entry(controllable, value));
		}
		else
		{
			_entries[index] = _entries[index] with { Sensor = controllable };
		}

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

		_entries[index] = _entries[index] with { Value = value };
		return this;
	}

	/// <inheritdoc />
	public void Open()
	{
		OnOpen?.Invoke();
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
			.Select(Sample)
			.ToArray();
	}

	/// <inheritdoc />
	public IReadOnlyList<SensorIdentifier> ReadControls()
	{
		ControlReadCount++;

		return [.. _controls.Keys];
	}

	/// <inheritdoc />
	public bool SetControl(string sensorId, float value)
	{
		OnSetControl?.Invoke();

		var (sensor, control) = FindControl(sensorId);
		if (sensor is null)
		{
			return false;
		}

		_controls[sensor] = control with
		{
			SoftwareValue = Math.Clamp(value, control.MinSoftwareValue, control.MaxSoftwareValue),
			Mode = ControlMode.Software,
		};

		LastSetSensorId = sensorId;
		LastSetValue = _controls[sensor].SoftwareValue;
		return true;
	}

	/// <inheritdoc />
	public bool ResetControl(string sensorId)
	{
		var (sensor, control) = FindControl(sensorId);
		if (sensor is null)
		{
			return false;
		}

		_controls[sensor] = control with { Mode = ControlMode.Default };
		LastResetSensorId = sensorId;
		return true;
	}

	/// <inheritdoc />
	public void Dispose() => Close();

	private (SensorIdentifier? Sensor, FakeControl Control) FindControl(string sensorId)
	{
		foreach (var pair in _controls)
		{
			if (string.Equals(pair.Key.Id, sensorId, StringComparison.Ordinal))
			{
				return (pair.Key, pair.Value);
			}
		}

		return (null, default);
	}

	/// <summary>
	/// One sample: a sensor with a control carries its range, and a software-controlled one reports the
	/// value that was written to it - which is how the library's own control sensor behaves.
	/// </summary>
	private HardwareSensor Sample(Entry entry)
	{
		if (!_controls.TryGetValue(entry.Sensor, out var control))
		{
			return new HardwareSensor(entry.Sensor, entry.Value);
		}

		return new HardwareSensor(
			entry.Sensor,
			control.Mode == ControlMode.Software ? control.SoftwareValue : entry.Value,
			new ControlRange(control.MinSoftwareValue, control.MaxSoftwareValue));
	}

	private sealed record Entry(SensorIdentifier Sensor, float? Value);

	private readonly record struct FakeControl(
		float MinSoftwareValue,
		float MaxSoftwareValue,
		float SoftwareValue,
		ControlMode Mode);
}
