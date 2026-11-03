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
	/// How many decimals a reading carries once shown in its chosen unit. A gigabyte or a megahertz is a
	/// large number and keeps the library's precision, but a gigahertz and a megabyte per second are small
	/// ones: keeping the library's integer precision would flatten a 3.7 GHz clock to 4.
	/// </summary>
	public static int DecimalPlaces(SensorType sensorType, SensorUnit? unit)
	{
		var chosen = ChosenUnit(sensorType, unit);

		return chosen switch
		{
			SensorUnit.Gigahertz => 2,
			SensorUnit.MegabytesPerSecond => 2,
			_ => DecimalPlaces(sensorType),
		};
	}

	/// <summary>
	/// A reading as the text the sensor window puts beside it: the number at <see cref="DecimalPlaces(SensorType)"/>,
	/// in the unit the sensor is configured to show, then that unit. Null when the sensor has no reading at
	/// all.
	/// </summary>
	/// <remarks>
	/// A sensor with no value is the ordinary case the window has to survive - a fan that has stopped, a
	/// disk that is not answering, a reading behind a driver this process is not allowed to open - so it
	/// is reported as nothing to show. Zero would be a claim about the machine instead.
	/// </remarks>
	public static string? Display(HardwareSensor sample) => Display(sample.Sensor, sample.Value);

	/// <summary>The same reading, for a sensor whose identifier came from somewhere else than the sample.</summary>
	public static string? Display(SensorIdentifier sensor, float? value)
	{
		ArgumentNullException.ThrowIfNull(sensor);

		if (value is not { } number || !float.IsFinite(number))
		{
			return null;
		}

		var converted = SensorUnits.ToDisplay(sensor.SensorType, number, sensor.DisplayUnit);
		if (!double.IsFinite(converted))
		{
			return null;
		}

		var places = DecimalPlaces(sensor.SensorType, sensor.DisplayUnit);
		var text = converted.ToString(
			string.Create(CultureInfo.InvariantCulture, $"F{places}"),
			CultureInfo.InvariantCulture);

		return Unit(sensor.SensorType, sensor.DisplayUnit) is { } unit ? $"{text} {unit}" : text;
	}

	/// <summary>
	/// A cached reading converted into the unit it is shown in. The cache keeps the library's unit, so this
	/// is what makes a Fahrenheit or gigahertz variable agree with the unit its definition declares.
	/// </summary>
	public static double Value(SensorIdentifier sensor, double value)
	{
		ArgumentNullException.ThrowIfNull(sensor);
		return SensorUnits.ToDisplay(sensor.SensorType, value, sensor.DisplayUnit);
	}

	/// <summary>
	/// The unit a sensor is shown in: the one the user chose when it is one its kind accepts, otherwise
	/// the library's own.
	/// </summary>
	private static SensorUnit? ChosenUnit(SensorType sensorType, SensorUnit? unit) =>
		unit is { } candidate && SensorUnits.IsValidFor(sensorType, candidate)
			? candidate
			: SensorUnits.IsSupported(sensorType) ? SensorUnits.DefaultFor(sensorType) : null;

	/// <summary>
	/// The unit symbol shown after the value, in the sensor's chosen unit when its kind offers a choice.
	/// Null for a kind with no obvious one.
	/// </summary>
	public static string? Unit(SensorType sensorType, SensorUnit? unit) =>
		ChosenUnit(sensorType, unit) is { } chosen ? SensorUnits.Symbol(chosen) : Unit(sensorType);

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
