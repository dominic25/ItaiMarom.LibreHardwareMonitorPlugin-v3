using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;
using MacroDeck.Sdk.Variables;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

[TestFixture]
public sealed class SensorFormattingTests
{
	[TestCase(SensorType.Temperature)]
	[TestCase(SensorType.Clock)]
	[TestCase(SensorType.Load)]
	[TestCase(SensorType.Fan)]
	[TestCase(SensorType.Flow)]
	[TestCase(SensorType.Control)]
	[TestCase(SensorType.Level)]
	[TestCase(SensorType.Frequency)]
	public void The_kinds_the_old_plugin_stored_as_integers_show_no_decimals(SensorType sensorType)
	{
		Assert.That(SensorFormatting.IsIntegerLike(sensorType), Is.True);
		Assert.That(SensorFormatting.DecimalPlaces(sensorType), Is.Zero);
	}

	[TestCase(SensorType.Voltage)]
	[TestCase(SensorType.Current)]
	[TestCase(SensorType.Power)]
	[TestCase(SensorType.Data)]
	[TestCase(SensorType.Throughput)]
	[TestCase(SensorType.Noise)]
	public void The_kinds_it_stored_as_floats_show_one(SensorType sensorType)
	{
		Assert.That(SensorFormatting.IsIntegerLike(sensorType), Is.False);
		Assert.That(SensorFormatting.DecimalPlaces(sensorType), Is.EqualTo(1));
	}

	[TestCase(SensorType.Temperature, "°C")]
	[TestCase(SensorType.Fan, "RPM")]
	[TestCase(SensorType.Power, "W")]
	[TestCase(SensorType.Throughput, "B/s")]
	[TestCase(SensorType.Data, "GB")]
	public void A_kind_with_an_obvious_unit_reports_it(SensorType sensorType, string unit)
	{
		Assert.That(SensorFormatting.Unit(sensorType), Is.EqualTo(unit));
	}

	[Test]
	public void A_kind_the_library_added_later_gets_no_unit_rather_than_a_wrong_one()
	{
		Assert.That(SensorFormatting.Unit(SensorType.Factor), Is.Null);
	}

	[TestCase(SensorType.Load)]
	[TestCase(SensorType.Control)]
	[TestCase(SensorType.Humidity)]
	public void A_ratio_is_rendered_as_a_percentage(SensorType sensorType)
	{
		Assert.That(SensorFormatting.SemanticKind(sensorType), Is.EqualTo(VariableSemanticKinds.Percentage));
	}

	[Test]
	public void A_throughput_carries_its_rate_in_the_unit()
	{
		// This SDK build has no bytes-per-second semantic kind, so the rate is said by the unit.
		Assert.That(SensorFormatting.SemanticKind(SensorType.Throughput), Is.EqualTo(VariableSemanticKinds.None));
		Assert.That(SensorFormatting.Unit(SensorType.Throughput), Is.EqualTo("B/s"));
	}

	[TestCase(SensorType.Data)]
	[TestCase(SensorType.SmallData)]
	public void A_size_the_library_already_scaled_is_not_a_byte_count(SensorType sensorType)
	{
		// "Data" is already gigabytes, so declaring it as bytes would render 16 GB as 16 bytes.
		Assert.That(SensorFormatting.SemanticKind(sensorType), Is.EqualTo(VariableSemanticKinds.None));
	}

	[TestCase(SensorType.Temperature, 46.4f, "46 °C")]
	[TestCase(SensorType.Temperature, 46.6f, "47 °C")]
	[TestCase(SensorType.Power, 13.04f, "13.0 W")]
	[TestCase(SensorType.Load, 4f, "4 %")]
	[TestCase(SensorType.Fan, 812.6f, "813 RPM")]
	[TestCase(SensorType.Voltage, 0.948f, "0.9 V")]
	public void A_reading_is_shown_at_the_precision_its_variable_carries(
		SensorType sensorType,
		float value,
		string expected)
	{
		Assert.That(SensorFormatting.Display(Sample(sensorType, value)), Is.EqualTo(expected));
	}

	[TestCase(SensorType.Temperature, SensorUnit.Fahrenheit, 46.6f, "116 °F")]
	[TestCase(SensorType.Temperature, SensorUnit.Celsius, 46.6f, "47 °C")]
	[TestCase(SensorType.Data, SensorUnit.Gigabytes, 16_000_000_000f, "16.0 GB")]
	[TestCase(SensorType.Data, SensorUnit.Gibibytes, 1073741824f, "1.0 GiB")]
	[TestCase(SensorType.SmallData, SensorUnit.Megabytes, 512f, "512.0 MB")]
	[TestCase(SensorType.SmallData, SensorUnit.Mebibytes, 1024f, "976.6 MiB")]
	[TestCase(SensorType.Clock, SensorUnit.Megahertz, 3700f, "3700 MHz")]
	[TestCase(SensorType.Clock, SensorUnit.Gigahertz, 3700f, "3.70 GHz")]
	[TestCase(SensorType.Throughput, SensorUnit.BytesPerSecond, 54268.9f, "54268.9 B/s")]
	[TestCase(SensorType.Throughput, SensorUnit.KilobytesPerSecond, 54268.9f, "53.0 KB/s")]
	[TestCase(SensorType.Throughput, SensorUnit.MegabytesPerSecond, 54268.9f, "0.05 MB/s")]
	public void A_chosen_unit_converts_the_reading_and_names_itself(
		SensorType sensorType,
		SensorUnit unit,
		float value,
		string expected)
	{
		var sensor = TestSensor.Create("CPU", "Reading", sensorType);

		Assert.That(SensorFormatting.Display(sensor with { DisplayUnit = unit }, value), Is.EqualTo(expected));
	}

	[Test]
	public void A_converted_reading_keeps_the_library_s_precision_when_the_number_is_large()
	{
		// A gigahertz clock is small and needed two decimals to stay useful, while the megahertz form it
		// came from keeps the integer precision the old plugin used.
		var clock = TestSensor.Create("CPU", "CPU Core #1", SensorType.Clock);

		Assert.That(SensorFormatting.DecimalPlaces(SensorType.Clock, SensorUnit.Gigahertz), Is.EqualTo(2));
		Assert.That(SensorFormatting.DecimalPlaces(SensorType.Clock, SensorUnit.Megahertz), Is.Zero);
		Assert.That(SensorFormatting.DecimalPlaces(SensorType.Clock, null), Is.Zero);
		Assert.That(SensorFormatting.DecimalPlaces(clock.SensorType, clock.DisplayUnit), Is.Zero);
	}

	[Test]
	public void A_unit_that_does_not_suit_the_sensor_kind_falls_back_to_the_library_s_own()
	{
		var temperature = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);

		// A hand-edited document, or one written by a later version, must not make a temperature be
		// converted as though it were a size.
		Assert.That(
			SensorFormatting.Display(temperature with { DisplayUnit = SensorUnit.Gigabytes }, 46.6f),
			Is.EqualTo("47 °C"));
		Assert.That(
			SensorFormatting.Unit(SensorType.Temperature, SensorUnit.Gigabytes),
			Is.EqualTo("°C"));
	}

	[Test]
	public void A_cached_reading_is_converted_into_the_unit_its_definition_declares()
	{
		var temperature = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);

		// The cache holds Celsius, so the value a Fahrenheit variable serves has to be converted or the
		// widget would show a Celsius number next to a Fahrenheit unit.
		Assert.That(
			SensorFormatting.Value(temperature with { DisplayUnit = SensorUnit.Fahrenheit }, 46.6),
			Is.EqualTo((46.6 * 1.8) + 32).Within(1e-9));
		Assert.That(SensorFormatting.Value(temperature, 46.6), Is.EqualTo(46.6));
	}

	[Test]
	public void A_kind_with_no_unit_choice_is_never_converted()
	{
		var fan = TestSensor.Create("Motherboard", "Fan #1", SensorType.Fan);

		// Fan has no unit choice, so even a stored unit from a hand-edited document is left alone.
		Assert.That(SensorFormatting.Value(fan with { DisplayUnit = SensorUnit.Fahrenheit }, 812), Is.EqualTo(812));
	}

	[Test]
	public void A_reading_of_a_kind_with_no_unit_is_shown_as_the_number_alone()
	{
		Assert.That(SensorFormatting.Display(Sample(SensorType.Factor, 1.26f)), Is.EqualTo("1.3"));
	}

	[TestCase(null)]
	[TestCase(float.NaN)]
	[TestCase(float.PositiveInfinity)]
	public void A_sensor_with_no_reading_shows_nothing(float? value)
	{
		// Zero would be a statement about the machine; a stopped fan and an unreadable sensor both have
		// nothing to say, and the window says that instead.
		Assert.That(SensorFormatting.Display(Sample(SensorType.Temperature, value)), Is.Null);
	}

	private static HardwareSensor Sample(SensorType sensorType, float? value) =>
		new(TestSensor.Create("CPU", "CPU Core #1", sensorType), value);
}
