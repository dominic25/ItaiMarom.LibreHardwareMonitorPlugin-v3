using System.Globalization;
using LibreHardwareMonitor.Hardware;
using MacroDeck.Sdk.Variables;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// How a LibreHardwareMonitor reading is presented as a Macro Deck variable.
/// </summary>
/// <remarks>
/// Macro Deck 2 split sensor kinds in two: the "integer" ones were stored through <c>(int)value</c> and
/// everything else kept its full precision. That split is carried over here as a display precision, not
/// as a value truncation: a temperature still holds its fraction so a graph or a later comparison sees
/// it, while <see cref="VariableDefinition.DecimalPlaces"/> keeps the rendered text the integer a user
/// of the old plugin expects.
/// </remarks>
public static class SensorFormatting
{
	/// <summary>Whether Macro Deck 2 stored this kind of sensor as an integer.</summary>
	public static bool IsIntegerLike(SensorType sensorType) => sensorType switch
	{
		SensorType.Clock or
		SensorType.Temperature or
		SensorType.Load or
		SensorType.Frequency or
		SensorType.Fan or
		SensorType.Flow or
		SensorType.Control or
		SensorType.Level => true,
		_ => false,
	};

	/// <summary>How many decimals the rendered value carries.</summary>
	public static int DecimalPlaces(SensorType sensorType) => IsIntegerLike(sensorType) ? 0 : 1;

	/// <summary>
	/// A reading as the text the sensor window puts beside it: the number at <see cref="DecimalPlaces"/>,
	/// then the unit. Null when the sensor has no reading at all.
	/// </summary>
	/// <remarks>
	/// A sensor with no value is the ordinary case the window has to survive - a fan that has stopped, a
	/// disk that is not answering, a reading behind a driver this process is not allowed to open - so it
	/// is reported as nothing to show. Zero would be a claim about the machine instead.
	/// </remarks>
	public static string? Display(HardwareSensor sample)
	{
		if (sample.Value is not { } value || !float.IsFinite(value))
		{
			return null;
		}

		var places = DecimalPlaces(sample.Sensor.SensorType);
		var number = value.ToString(
			string.Create(CultureInfo.InvariantCulture, $"F{places}"),
			CultureInfo.InvariantCulture);

		return Unit(sample.Sensor.SensorType) is { } unit ? $"{number} {unit}" : number;
	}

	/// <summary>
	/// The unit symbol shown after the value, or null for a kind with no obvious one.
	/// </summary>
	/// <remarks>A sensor kind the library adds later falls through to null rather than to a wrong unit.</remarks>
	public static string? Unit(SensorType sensorType) => sensorType switch
	{
		SensorType.Voltage => "V",
		SensorType.Current => "A",
		SensorType.Power => "W",
		SensorType.Clock => "MHz",
		SensorType.Temperature => "°C",
		SensorType.Load => "%",
		SensorType.Frequency => "Hz",
		SensorType.Fan => "RPM",
		SensorType.Flow => "L/h",
		SensorType.Control => "%",
		SensorType.Level => "%",
		SensorType.Data => "GB",
		SensorType.SmallData => "MB",
		SensorType.Throughput => "B/s",
		SensorType.TimeSpan => "s",
		SensorType.Energy => "mWh",
		SensorType.Noise => "dBA",
		SensorType.Conductivity => "µS/cm",
		SensorType.Humidity => "%",
		_ => null,
	};

	/// <summary>
	/// The formatting hint for a value that needs more than "number plus unit".
	/// </summary>
	/// <remarks>
	/// <see cref="SensorType.Data"/> and <see cref="SensorType.SmallData"/> are deliberately
	/// <see cref="VariableSemanticKinds.None"/>: the library already scaled them to GB and MB, so telling
	/// the host they are byte counts would render a gigabyte reading as one byte.
	/// <para>
	/// <see cref="SensorType.Throughput"/> is likewise <see cref="VariableSemanticKinds.None"/>: this SDK
	/// build has no bytes-per-second kind, and its <c>B/s</c> unit already says what the number is.
	/// </para>
	/// </remarks>
	public static string SemanticKind(SensorType sensorType) => sensorType switch
	{
		SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity =>
			VariableSemanticKinds.Percentage,
		SensorType.TimeSpan => VariableSemanticKinds.Duration,
		_ => VariableSemanticKinds.None,
	};
}
