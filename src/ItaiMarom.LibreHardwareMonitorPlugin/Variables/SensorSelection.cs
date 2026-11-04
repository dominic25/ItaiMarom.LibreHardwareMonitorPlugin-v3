using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;

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
/// <para>
/// The defaults are kept here rather than on each sensor because they are a property of the
/// configuration, not of a reading: a sensor with no <see cref="SensorIdentifier.DisplayUnit"/> follows
/// them even when it is not part of the selection - the catalog serves those - and changing a default has
/// to change every sensor that never made a choice.
/// </para>
/// </remarks>
public sealed class SensorSelection
{
	private readonly object _sync = new();
	private IReadOnlyList<SensorIdentifier> _sensors = [];
	private IReadOnlyDictionary<SensorType, SensorUnit> _unitDefaults = SensorUnits.NormalizeDefaults(null);

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
	/// The default display unit per sensor kind, complete for every kind that offers a choice. A sensor
	/// with no choice of its own is shown in the one named here.
	/// </summary>
	public IReadOnlyDictionary<SensorType, SensorUnit> UnitDefaults
	{
		get
		{
			lock (_sync)
			{
				return _unitDefaults;
			}
		}
	}

	/// <summary>The unit a sensor of this kind uses when it has no choice of its own.</summary>
	public SensorUnit DefaultUnit(SensorType sensorType) =>
		SensorUnits.DefaultFor(sensorType, UnitDefaults);

	/// <summary>
	/// The sensor with its kind's default unit applied when it has no choice of its own. The result is
	/// what formatting and the variable definitions use; the stored sensor is left untouched, so changing
	/// the default later still reaches it.
	/// </summary>
	public SensorIdentifier WithDefaultUnit(SensorIdentifier sensor)
	{
		ArgumentNullException.ThrowIfNull(sensor);

		return sensor.DisplayUnit is null && SensorUnits.IsSupported(sensor.SensorType)
			? sensor with { DisplayUnit = DefaultUnit(sensor.SensorType) }
			: sensor;
	}

	/// <summary>
	/// Replaces the selection and the defaults. A different list instance is stored every time, so a
	/// reader that caches the last list it was handed can tell by reference that its copy is stale.
	/// </summary>
	/// <param name="sensors">The selected sensors, or null for none.</param>
	/// <param name="unitDefaults">
	/// The configured defaults, or null to use the library's own units. They are normalized into a
	/// complete map, so every kind that offers a choice has an answer.
	/// </param>
	public void Replace(
		IEnumerable<SensorIdentifier>? sensors,
		IReadOnlyDictionary<SensorType, SensorUnit>? unitDefaults = null)
	{
		var replacement = sensors is null
			? []
			: sensors.Distinct().ToArray();
		var defaults = SensorUnits.NormalizeDefaults(unitDefaults);

		lock (_sync)
		{
			_sensors = replacement;
			_unitDefaults = defaults;
		}
	}
}
