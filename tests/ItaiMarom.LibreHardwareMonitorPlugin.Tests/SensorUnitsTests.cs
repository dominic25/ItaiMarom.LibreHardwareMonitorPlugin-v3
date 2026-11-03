using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The unit model behind a sensor's display choice: which units a kind offers, how a stored unit is read
/// back, and how a reading another machine formatted is returned to the unit this plugin caches.
/// </summary>
[TestFixture]
public sealed class SensorUnitsTests
{
	[TestCase(SensorType.Temperature, SensorUnit.Celsius)]
	[TestCase(SensorType.Data, SensorUnit.Gigabytes)]
	[TestCase(SensorType.SmallData, SensorUnit.Megabytes)]
	[TestCase(SensorType.Clock, SensorUnit.Megahertz)]
	[TestCase(SensorType.Throughput, SensorUnit.BytesPerSecond)]
	public void The_library_s_own_unit_is_the_default(SensorType sensorType, SensorUnit expected) =>
		Assert.That(SensorUnits.DefaultFor(sensorType), Is.EqualTo(expected));

	[TestCase(SensorType.Fan)]
	[TestCase(SensorType.Voltage)]
	[TestCase(SensorType.Load)]
	public void A_kind_with_no_choice_offers_no_options(SensorType sensorType)
	{
		Assert.That(SensorUnits.IsSupported(sensorType), Is.False);
		Assert.That(SensorUnits.OptionsFor(sensorType), Is.Empty);
	}

	[Test]
	public void Only_a_unit_the_kind_can_be_shown_in_is_accepted()
	{
		Assert.That(SensorUnits.IsValidFor(SensorType.Temperature, SensorUnit.Fahrenheit), Is.True);
		Assert.That(SensorUnits.IsSupported(SensorType.Temperature), Is.True);

		// A size is not a temperature, even though both are units this plugin knows.
		Assert.That(SensorUnits.IsValidFor(SensorType.Temperature, SensorUnit.Gigabytes), Is.False);
		Assert.That(SensorUnits.IsValidFor(SensorType.Data, SensorUnit.Fahrenheit), Is.False);
	}

	[TestCase("Fahrenheit", SensorUnit.Fahrenheit)]
	[TestCase("fahrenheit", SensorUnit.Fahrenheit)]
	[TestCase("Gibibytes", SensorUnit.Gibibytes)]
	[TestCase("°F", SensorUnit.Fahrenheit)]
	[TestCase("°C", SensorUnit.Celsius)]
	[TestCase("GiB", SensorUnit.Gibibytes)]
	[TestCase("MB", SensorUnit.Megabytes)]
	[TestCase("MiB", SensorUnit.Mebibytes)]
	[TestCase("GHz", SensorUnit.Gigahertz)]
	[TestCase("KB/s", SensorUnit.KilobytesPerSecond)]
	[TestCase("KiB/s", SensorUnit.KilobytesPerSecond)]
	[TestCase("MB/s", SensorUnit.MegabytesPerSecond)]
	public void A_stored_unit_reads_back_from_its_enum_name_or_its_symbol(string text, SensorUnit expected)
	{
		Assert.That(SensorUnits.TryParse(text, out var unit), Is.True);
		Assert.That(unit, Is.EqualTo(expected));
	}

	[TestCase(null)]
	[TestCase("")]
	[TestCase("  ")]
	[TestCase("not-a-unit")]
	[TestCase("99")]
	public void A_text_that_is_not_a_unit_is_rejected(string? text) =>
		Assert.That(SensorUnits.TryParse(text, out _), Is.False);

	[Test]
	public void Every_unit_has_the_symbol_a_user_reads()
	{
		Assert.That(SensorUnits.Symbol(SensorUnit.Celsius), Is.EqualTo("°C"));
		Assert.That(SensorUnits.Symbol(SensorUnit.Fahrenheit), Is.EqualTo("°F"));
		Assert.That(SensorUnits.Symbol(SensorUnit.Gigabytes), Is.EqualTo("GB"));
		Assert.That(SensorUnits.Symbol(SensorUnit.Gibibytes), Is.EqualTo("GiB"));
		Assert.That(SensorUnits.Symbol(SensorUnit.Megabytes), Is.EqualTo("MB"));
		Assert.That(SensorUnits.Symbol(SensorUnit.Mebibytes), Is.EqualTo("MiB"));
		Assert.That(SensorUnits.Symbol(SensorUnit.Megahertz), Is.EqualTo("MHz"));
		Assert.That(SensorUnits.Symbol(SensorUnit.Gigahertz), Is.EqualTo("GHz"));
		Assert.That(SensorUnits.Symbol(SensorUnit.BytesPerSecond), Is.EqualTo("B/s"));
		Assert.That(SensorUnits.Symbol(SensorUnit.KilobytesPerSecond), Is.EqualTo("KB/s"));
		Assert.That(SensorUnits.Symbol(SensorUnit.MegabytesPerSecond), Is.EqualTo("MB/s"));
	}

	[TestCase("°F", 120.2, 49.0)]
	[TestCase("F", 120.2, 49.0)]
	[TestCase("°C", 49.0, 49.0)]
	public void A_temperature_another_machine_reported_in_fahrenheit_is_cached_in_celsius(
		string unit,
		double reported,
		double expected)
	{
		Assert.That(SensorUnits.FromReported(SensorType.Temperature, reported, unit), Is.EqualTo(expected).Within(1e-9));
	}

	[TestCase("GB", 12.2)]
	[TestCase("GiB", 12.2)]
	[TestCase("gib", 12.2)]
	public void A_size_a_server_scaled_is_cached_as_bytes(string unit, double reported)
	{
		// LibreHardwareMonitor scales a size with 1024 even when it labels the result GB, so the suffix
		// is undone with the same factor and the reading is converted exactly once.
		Assert.That(
			SensorUnits.FromReported(SensorType.Data, reported, unit),
			Is.EqualTo(reported * 1024 * 1024 * 1024).Within(1e-6));
	}

	[Test]
	public void A_small_size_a_server_scaled_is_cached_in_megabytes()
	{
		// SmallData is cached in native megabytes, so a server's binary GB has to land there, not in bytes.
		Assert.That(
			SensorUnits.FromReported(SensorType.SmallData, 1, "GB"),
			Is.EqualTo(1024 * 1024 * 1024 / 1_000_000d).Within(1e-9));
	}

	[Test]
	public void A_rate_a_server_scaled_is_cached_in_bytes_per_second()
	{
		Assert.That(
			SensorUnits.FromReported(SensorType.Throughput, 53, "KB/s"),
			Is.EqualTo(53 * 1024).Within(1e-9));
		Assert.That(
			SensorUnits.FromReported(SensorType.Throughput, 1.5, "MB/s"),
			Is.EqualTo(1.5 * 1024 * 1024).Within(1e-9));
	}

	[Test]
	public void A_network_link_reported_in_bits_is_cached_in_bytes()
	{
		// The library's network sensors format a link speed as bits per second, which is not the byte
		// rate the cache holds.
		Assert.That(SensorUnits.FromReported(SensorType.Throughput, 800, "bps"), Is.EqualTo(100).Within(1e-9));
	}

	[Test]
	public void A_clock_a_server_scaled_is_cached_in_megahertz()
	{
		Assert.That(SensorUnits.FromReported(SensorType.Clock, 3.7, "GHz"), Is.EqualTo(3700).Within(1e-9));
	}

	[Test]
	public void A_number_with_no_unit_is_already_in_the_library_s_unit()
	{
		// The server's RawValue is a plain JSON number in some versions, and that is the library's own
		// unit - scaling it again would turn 16 GB into 16 KiB.
		Assert.That(SensorUnits.FromReported(SensorType.Data, 16, null), Is.EqualTo(16));
		Assert.That(SensorUnits.FromReported(SensorType.Temperature, 51, null), Is.EqualTo(51));
		Assert.That(SensorUnits.FromReported(SensorType.Clock, 3700, ""), Is.EqualTo(3700));
	}

	[TestCase(SensorType.Temperature, SensorUnit.Fahrenheit, 50, 122)]
	[TestCase(SensorType.Data, SensorUnit.Gigabytes, 16_000_000_000, 16)]
	[TestCase(SensorType.Data, SensorUnit.Gibibytes, 1073741824, 1)]
	[TestCase(SensorType.SmallData, SensorUnit.Megabytes, 512, 512)]
	[TestCase(SensorType.SmallData, SensorUnit.Mebibytes, 1024, 976.5625)]
	[TestCase(SensorType.Clock, SensorUnit.Gigahertz, 3700, 3.7)]
	[TestCase(SensorType.Throughput, SensorUnit.KilobytesPerSecond, 54272, 53)]
	[TestCase(SensorType.Throughput, SensorUnit.MegabytesPerSecond, 1048576, 1)]
	public void A_cached_reading_is_converted_into_the_chosen_unit(
		SensorType sensorType,
		SensorUnit unit,
		double cached,
		double expected)
	{
		Assert.That(
			SensorUnits.ToDisplay(sensorType, cached, unit),
			Is.EqualTo(expected).Within(1e-9));
	}

	[Test]
	public void A_kind_with_no_choice_is_never_converted() =>
		Assert.That(
			SensorUnits.ToDisplay(SensorType.Fan, 812, SensorUnit.Fahrenheit),
			Is.EqualTo(812));
}
