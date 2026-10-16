using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Localization;
using MacroDeck.Sdk.Variables;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Variables;

/// <summary>
/// One sensor together with the variable name and id it is exposed under.
/// </summary>
/// <param name="Name">
/// The name the user types in a button. For a sensor the Macro Deck 2 plugin already published this is
/// that plugin's exact spelling, so an upgraded layout resolves unchanged.
/// </param>
/// <param name="Id">
/// The declared local id, derived from the sensor's library identifier rather than from the name: it only
/// has to be unique and stable, and it must not move when a name is re-derived.
/// </param>
public sealed record SensorVariable(SensorIdentifier Sensor, string Name, string Id)
{
	/// <summary>The eager definition the host registers for this variable.</summary>
	public VariableDefinition EagerDefinition() => VariableDefinition.Eager(
		Name,
		VariableType.Numeric,
		SensorFormatting.DecimalPlaces(Sensor.SensorType)) with
	{
		Id = Id,
		Unit = SensorFormatting.Unit(Sensor.SensorType),
		SemanticKind = SensorFormatting.SemanticKind(Sensor.SensorType),
		DisplayName = LocalizedText.FromLiteral(Sensor.DisplayName),
	};
}
