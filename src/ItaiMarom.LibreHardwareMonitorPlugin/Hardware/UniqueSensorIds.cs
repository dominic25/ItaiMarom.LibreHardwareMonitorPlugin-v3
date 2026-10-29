using System.Globalization;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// Hands out the sensor ids the rest of the plugin keys by, settling the ones the hardware library
/// repeats.
/// </summary>
/// <remarks>
/// <para>
/// A LibreHardwareMonitor identifier is built from the sensor's index on its device, and the library does
/// not insist that two sensors of one kind carry different indexes. An NVIDIA card, for instance, reports
/// <c>GPU Bus</c> and <c>GPU Memory</c> both as <c>/gpu-nvidia/0/load/3</c>. Everything the plugin caches,
/// declares or catalogs is keyed by that identifier, so a repeat is not a cosmetic problem: the first
/// sensor answers for both and the second disappears wherever the ids are indexed - which is exactly what
/// happens to the GPU's memory load reading.
/// </para>
/// <para>
/// The fix is applied once, at the source, so no consumer has to know the library can do this. The first
/// sensor keeps the library's id verbatim, because a configuration written before this rule existed keeps
/// resolving, and each repeat after it is suffixed the way the catalog's own ids fall back to. The order
/// the sensors arrive in is the library's activation order, which does not change for a given device and
/// driver, so the numbering stays put across restarts.
/// </para>
/// </remarks>
internal sealed class UniqueSensorIds
{
	private readonly HashSet<string> _used = new(StringComparer.Ordinal);

	/// <summary>
	/// The sensor under an id nothing handed out before already answers to: its own when it is free, a
	/// suffixed one when it is not.
	/// </summary>
	public SensorIdentifier Apply(SensorIdentifier sensor)
	{
		ArgumentNullException.ThrowIfNull(sensor);

		if (_used.Add(sensor.Id))
		{
			return sensor;
		}

		return sensor with { Id = NextFreeId(sensor.Id) };
	}

	/// <summary>
	/// The first free suffix of one repeated id, reserved on the way out and kept inside the length a
	/// resource id may have.
	/// </summary>
	private string NextFreeId(string id)
	{
		for (var suffix = 2; ; suffix++)
		{
			var tail = "-" + suffix.ToString(CultureInfo.InvariantCulture);
			var room = VariableNaming.MaxResourceIdLength - tail.Length;
			var head = id.Length > room ? id[..room] : id;
			var candidate = head + tail;

			if (_used.Add(candidate))
			{
				return candidate;
			}
		}
	}
}
