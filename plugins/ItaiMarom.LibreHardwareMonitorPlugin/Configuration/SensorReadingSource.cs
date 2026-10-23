using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Configuration;

/// <summary>
/// Where the sensor window's value column gets what it shows, once per refresh.
/// </summary>
internal interface ISensorReadingSource
{
	/// <summary>
	/// One reading per sensor on the machine, keyed by the resource id the window names it by, and null
	/// for a sensor that has no reading right now.
	/// </summary>
	Task<IReadOnlyDictionary<string, string?>> ReadingsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The real one: it reads the whole machine through <see cref="HardwareMonitor"/>, which is what makes
/// the values live rather than a copy of whatever was true when the window opened.
/// </summary>
/// <remarks>
/// A full read takes a couple of hundred milliseconds on a machine with everything in it, which is why
/// the window asks for one a second and not on every frame.
/// </remarks>
internal sealed class SensorReadingSource : ISensorReadingSource
{
	private readonly HardwareMonitor _hardware;

	public SensorReadingSource(HardwareMonitor hardware) =>
		_hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));

	/// <inheritdoc />
	public async Task<IReadOnlyDictionary<string, string?>> ReadingsAsync(CancellationToken cancellationToken) =>
		ValuesOf(await _hardware.SampleAsync(cancellationToken).ConfigureAwait(false));

	/// <summary>
	/// Turns one read of a machine into the window's pairs. The first sensor under a resource id is the
	/// one that counts, which is the same rule the window's tree follows when it drops its duplicates.
	/// </summary>
	public static IReadOnlyDictionary<string, string?> ValuesOf(IReadOnlyList<HardwareSensor> samples)
	{
		ArgumentNullException.ThrowIfNull(samples);

		var values = new Dictionary<string, string?>(samples.Count, StringComparer.Ordinal);
		foreach (var sample in samples)
		{
			values.TryAdd(sample.Sensor.ResourceId, SensorFormatting.Display(sample));
		}

		return values;
	}
}
