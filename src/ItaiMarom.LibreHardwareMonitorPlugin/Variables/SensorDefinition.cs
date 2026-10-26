using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Localization;
using MacroDeck.Sdk.Variables;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Variables;

/// <summary>
/// The parts of a variable definition that come from the sensor alone, shared by the declared set and the
/// catalog so the two cannot describe the same fan differently.
/// </summary>
internal static class SensorDefinition
{
	/// <summary>
	/// Fills in what a sensor says about itself: how to render it, what it is called, and - for a fan or
	/// pump the library gave a control to - that the host may write to it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The write capability is declared with <see cref="VariableWriteCapability.CommitOnRelease"/>, so a
	/// drag is one write when the user lets go rather than one per waypoint. The library persists a
	/// control's software value through its settings on every change, and a fan that audibly ramps through
	/// every intermediate point of a gesture is not a useful preview of the value it lands on.
	/// </para>
	/// <para>
	/// The same capability is what the host's Slider widget needs for two-way binding: it reads the range
	/// off the reading, writes through <c>SetValueAsync</c>, and settles on whatever the next read says.
	/// A sensor the walk found no control on declares nothing, so a graphics card's fan duty stays a
	/// reading rather than a slider that answers "unavailable" when it is moved.
	/// </para>
	/// </remarks>
	public static VariableDefinition Describe(SensorIdentifier sensor, VariableDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(sensor);
		ArgumentNullException.ThrowIfNull(definition);

		return definition with
		{
			DisplayName = LocalizedText.FromLiteral(sensor.DisplayName),
			DecimalPlaces = SensorFormatting.DecimalPlaces(sensor.SensorType),
			Unit = SensorFormatting.Unit(sensor.SensorType),
			SemanticKind = SensorFormatting.SemanticKind(sensor.SensorType),
			Write = sensor.Controllable ? new VariableWriteCapability { CommitOnRelease = true } : null,
		};
	}
}
