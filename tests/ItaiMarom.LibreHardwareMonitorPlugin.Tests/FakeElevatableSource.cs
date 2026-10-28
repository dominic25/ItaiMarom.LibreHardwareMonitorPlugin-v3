using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// A source that is also elevatable, so the issue's button can be driven without a pipe, a helper process
/// or a UAC prompt.
/// </summary>
/// <remarks>
/// The real source is <see cref="ElevatedHardwareSource"/>, which wraps a local source and swaps it for a
/// helper connection. This one keeps the same shape - the hardware calls all go to the fake behind it -
/// and records what the button asked for.
/// </remarks>
internal sealed class FakeElevatableSource : IHardwareSensorSource, IHardwareElevation
{
	private readonly FakeHardwareSensorSource _hardware;

	public FakeElevatableSource(FakeHardwareSensorSource? hardware = null) =>
		_hardware = hardware ?? new FakeHardwareSensorSource();

	/// <summary>The sensors and controls the plugin actually reads.</summary>
	public FakeHardwareSensorSource Hardware => _hardware;

	/// <summary>What the next <see cref="EnableAsync"/> answers. Enabled by default.</summary>
	public ElevationResult NextResult { get; set; } = new(ElevationOutcome.Enabled);

	/// <summary>How many times the issue's button asked for a helper.</summary>
	public int EnableCount { get; private set; }

	/// <inheritdoc />
	public bool IsElevated { get; private set; }

	/// <inheritdoc />
	public string? ElevatedAccessNote { get; private set; }

	/// <inheritdoc />
	public Task<ElevationResult> EnableAsync(CancellationToken cancellationToken = default)
	{
		EnableCount++;

		if (NextResult.Outcome == ElevationOutcome.Enabled)
		{
			IsElevated = true;
			ElevatedAccessNote = NextResult.AccessNote;
		}

		return Task.FromResult(NextResult);
	}

	/// <inheritdoc />
	public void Open() => _hardware.Open();

	/// <inheritdoc />
	public void Close() => _hardware.Close();

	/// <inheritdoc />
	public IReadOnlyList<HardwareSensor> Read(IReadOnlyCollection<string>? hardwareFilter) =>
		_hardware.Read(hardwareFilter);

	/// <inheritdoc />
	public IReadOnlyList<SensorIdentifier> ReadControls() => _hardware.ReadControls();

	/// <inheritdoc />
	public bool SetControl(string sensorId, float value) => _hardware.SetControl(sensorId, value);

	/// <inheritdoc />
	public bool ResetControl(string sensorId) => _hardware.ResetControl(sensorId);

	/// <inheritdoc />
	public void Dispose() => _hardware.Dispose();
}
