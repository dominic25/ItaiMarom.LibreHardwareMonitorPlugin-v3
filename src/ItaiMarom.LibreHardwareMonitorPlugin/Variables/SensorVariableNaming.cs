using System.Globalization;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Variables;

/// <summary>
/// Decides the name and id each selected sensor's eager variable is declared under.
/// </summary>
/// <remarks>
/// <para>
/// Unlike a catalog resource - whose suggested name the host re-derives and de-duplicates on its own -
/// a declared variable's name is taken literally, so uniqueness has to be settled here. That is why this
/// works on the whole selection at once rather than on one sensor: the answer for one sensor depends on
/// the others.
/// </para>
/// <para>
/// The Macro Deck 2 plugin built names from the sensor name and kind alone, ignoring the owning hardware,
/// so two graphics cards or two disks that report the same sensor both wanted the same name. The user's
/// choice was to keep the old spelling for the first and to qualify the rest: the sensor that sorts
/// first keeps the exact name a Macro Deck 2 layout already uses, and the others are prefixed with the
/// device they belong to. The ordering is by name and then by hardware, so which sensor wins is stable
/// across restarts.
/// </para>
/// </remarks>
public static class SensorVariableNaming
{
	/// <summary>Assigns a unique name and id to every sensor in <paramref name="sensors"/>.</summary>
	public static IReadOnlyList<SensorVariable> Assign(IEnumerable<SensorIdentifier> sensors)
	{
		ArgumentNullException.ThrowIfNull(sensors);

		var ordered = sensors
			.Distinct()
			.OrderBy(sensor => sensor.VariableName, StringComparer.Ordinal)
			.ThenBy(sensor => sensor.Hardware.Path, StringComparer.Ordinal)
			.ThenBy(sensor => sensor.SensorType)
			.ThenBy(sensor => sensor.SensorName, StringComparer.Ordinal)
			.ToArray();

		var names = new HashSet<string>(StringComparer.Ordinal);
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var variables = new List<SensorVariable>(ordered.Length);

		foreach (var sensor in ordered)
		{
			var name = UniqueName(sensor, names);
			names.Add(name);

			var id = UniqueId(sensor.VariableId, ids);
			ids.Add(id);

			variables.Add(new SensorVariable(sensor, name, id));
		}

		return variables;
	}

	private static string UniqueName(SensorIdentifier sensor, HashSet<string> used)
	{
		foreach (var candidate in Candidates(sensor))
		{
			if (candidate.Length > 0 && !used.Contains(candidate))
			{
				return candidate;
			}
		}

		// Two devices of the same model reporting the same sensor: the device-qualified names collide too,
		// so fall back to a counter. Sanitize collapses the run of separators it may introduce.
		var stem = sensor.VariableName.Length > 0 ? sensor.VariableName : "sensor";
		for (var suffix = 2; ; suffix++)
		{
			var candidate = VariableNaming.Sanitize($"{stem} {suffix}");
			if (!used.Contains(candidate))
			{
				return candidate;
			}
		}
	}

	private static IEnumerable<string> Candidates(SensorIdentifier sensor)
	{
		// Macro Deck 2's exact spelling first - already a legal name for every sensor it published, and
		// kept verbatim, runs of underscores included, so an upgraded button resolves.
		yield return sensor.VariableName;

		// Then the same name qualified by the device, which is what tells two identical disks apart.
		yield return VariableNaming.Sanitize($"{sensor.Hardware.RootName} {sensor.VariableName}");

		// A motherboard's super-I/O chip, say, needs the whole path rather than just the top-level device.
		yield return VariableNaming.Sanitize($"{sensor.Hardware.Path} {sensor.VariableName}");
	}

	private static string UniqueId(string id, HashSet<string> used)
	{
		if (!used.Contains(id))
		{
			return id;
		}

		for (var suffix = 2; ; suffix++)
		{
			var tail = "-" + suffix.ToString(CultureInfo.InvariantCulture);
			var room = VariableNaming.MaxDeclaredIdLength - tail.Length;
			var head = id.Length > room ? id[..room].TrimEnd('-') : id;
			var candidate = head.Length > 0 ? head + tail : "v" + tail;

			if (!used.Contains(candidate))
			{
				return candidate;
			}
		}
	}
}
