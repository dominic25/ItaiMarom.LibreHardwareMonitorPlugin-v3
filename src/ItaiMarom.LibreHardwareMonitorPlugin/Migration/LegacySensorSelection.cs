using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Migration;

/// <summary>
/// Turns the sensors a Macro Deck 2 layout asked for into the sensors this machine currently exposes.
/// </summary>
/// <remarks>
/// The old plugin stored a display name, not an identifier, so the migration is a match and not a
/// lookup: what the user asked for is compared against the hardware library's own names, on the machine
/// as it is now. A sensor that is no longer there simply does not come back, which is the same thing
/// that happened to it in Macro Deck 2.
/// </remarks>
public static class LegacySensorSelection
{
	/// <summary>
	/// Every currently present sensor that one of <paramref name="requests"/> names, in the order the
	/// requests were stored.
	/// </summary>
	/// <remarks>
	/// A request keeps every sensor it matches rather than the first, because that is what the old plugin
	/// did: it walked every hardware and set a value on each match, so a machine with two identical disks
	/// produced a value from both. Taking one would silently drop a device the user was watching.
	/// </remarks>
	public static IReadOnlyList<SensorIdentifier> Select(
		IEnumerable<LegacySensorRequest> requests,
		IEnumerable<SensorIdentifier> available)
	{
		ArgumentNullException.ThrowIfNull(requests);
		ArgumentNullException.ThrowIfNull(available);

		var candidates = available.ToArray();
		var selected = new List<SensorIdentifier>();
		var seen = new HashSet<SensorIdentifier>();

		foreach (var request in requests)
		{
			foreach (var sensor in candidates)
			{
				if (request.Matches(sensor) && seen.Add(sensor))
				{
					selected.Add(sensor);
				}
			}
		}

		return selected;
	}
}
