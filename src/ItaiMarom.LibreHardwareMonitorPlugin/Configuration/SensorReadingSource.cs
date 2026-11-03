using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;

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
	private readonly SensorSelection? _selection;

	public SensorReadingSource(HardwareMonitor hardware, SensorSelection? selection = null)
	{
		_hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
		_selection = selection;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyDictionary<string, string?>> ReadingsAsync(CancellationToken cancellationToken) =>
		ValuesOf(
			await _hardware.SampleAsync(cancellationToken).ConfigureAwait(false),
			UnitsOf(_selection));

	/// <summary>
	/// Turns one read of a machine into the window's pairs. The first sensor under a resource id is the
	/// one that counts, which is the same rule the window's tree follows when it drops its duplicates.
	/// </summary>
	/// <remarks>
	/// A sample comes from the hardware source, which knows nothing about the selection, so the unit the
	/// user chose for a sensor has to be looked up separately. Without that the window would show a
	/// Fahrenheit setting's Celsius number while the chosen unit sat in the selector beside it.
	/// </remarks>
	public static IReadOnlyDictionary<string, string?> ValuesOf(
		IReadOnlyList<HardwareSensor> samples,
		IReadOnlyDictionary<string, SensorUnit>? units = null)
	{
		ArgumentNullException.ThrowIfNull(samples);

		var values = new Dictionary<string, string?>(samples.Count, StringComparer.Ordinal);
		foreach (var sample in samples)
		{
			var sensor = sample.Sensor;
			if (units is not null && units.TryGetValue(sensor.ResourceId, out var unit))
			{
				sensor = sensor with { DisplayUnit = unit };
			}

			values.TryAdd(sensor.ResourceId, SensorFormatting.Display(sensor, sample.Value));
		}

		return values;
	}

	/// <summary>The units the selection carries, keyed the way the window names a sensor.</summary>
	public static IReadOnlyDictionary<string, SensorUnit>? UnitsOf(SensorSelection? selection)
	{
		if (selection is null)
		{
			return null;
		}

		var units = new Dictionary<string, SensorUnit>(StringComparer.Ordinal);
		foreach (var sensor in selection.Sensors)
		{
			if (sensor.DisplayUnit is { } unit && SensorUnits.IsValidFor(sensor.SensorType, unit))
			{
				units[sensor.ResourceId] = unit;
			}
		}

		return units.Count > 0 ? units : null;
	}
}
