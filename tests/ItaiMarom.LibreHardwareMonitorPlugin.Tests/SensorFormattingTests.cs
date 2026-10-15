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
}
