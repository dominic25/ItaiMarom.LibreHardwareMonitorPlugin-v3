using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Variables;

/// <summary>
/// The sensors the user - or the Macro Deck 2 migration - asked to have as ordinary, always-present
/// variables. This is the eager half of the variable provider.
/// </summary>
/// <remarks>
/// <para>
/// The full sensor tree is served on demand by the catalog instead, so nothing has to be listed here for
/// a user to reach it. What belongs here is exactly the set a Macro Deck 2 layout already refers to by
/// name: a layout that says <c>{{cpu_core__1_temperature}}</c> only keeps working if that name is a
/// declared variable, and a declared variable is one of these.
/// </para>
/// <para>
/// Deliberately settable at run time and read on every request rather than captured once, because the
/// configuration behind it can change and a change has to take effect without re-registering the
/// provider.
/// </para>
/// </remarks>
public sealed class SensorSelection
{
	private readonly object _sync = new();
	private IReadOnlyList<SensorIdentifier> _sensors = [];

	/// <summary>The selected sensors. Never null; empty until something is configured or migrated.</summary>
	public IReadOnlyList<SensorIdentifier> Sensors
	{
		get
		{
			lock (_sync)
			{
				return _sensors;
			}
		}
	}

	/// <summary>
	/// Replaces the selection. A different list instance is stored every time, so a reader that caches
	/// the last list it was handed can tell by reference that its copy is stale.
	/// </summary>
	public void Replace(IEnumerable<SensorIdentifier>? sensors)
	{
		var replacement = sensors is null
			? []
			: sensors.Distinct().ToArray();

		lock (_sync)
		{
			_sensors = replacement;
		}
	}
}
