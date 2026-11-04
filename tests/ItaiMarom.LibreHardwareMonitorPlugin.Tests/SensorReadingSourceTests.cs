using ItaiMarom.LibreHardwareMonitorPlugin.Configuration;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The bridge between one read of the machine and the value column of the sensor window.
/// </summary>
[TestFixture]
public sealed class SensorReadingSourceTests
{
	[Test]
	public void Every_reading_is_keyed_by_the_id_the_window_names_its_sensor_by()
	{
		var sensor = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);

		var values = SensorReadingSource.ValuesOf([new HardwareSensor(sensor, 46.6f)]);

		// The window's rows carry the resource id, so this is the only key that can be looked up.
		Assert.That(values, Has.Count.EqualTo(1));
		Assert.That(values[sensor.ResourceId], Is.EqualTo("47 °C"));
	}

	[Test]
	public void A_sensor_with_no_reading_is_carried_as_one_with_nothing_to_show()
	{
		var sensor = TestSensor.Create("GPU", "GPU Fan", SensorType.Fan);

		var values = SensorReadingSource.ValuesOf([new HardwareSensor(sensor, null)]);

		Assert.That(values, Has.Count.EqualTo(1));
		Assert.That(values[sensor.ResourceId], Is.Null);
	}

	[Test]
	public void A_sensor_is_shown_in_the_unit_the_window_chose_for_it()
	{
		var sensor = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);

		var values = SensorReadingSource.ValuesOf(
			[new HardwareSensor(sensor, 50f)],
			new Dictionary<string, SensorUnit>(StringComparer.Ordinal)
			{
				[sensor.ResourceId] = SensorUnit.Fahrenheit,
			});

		// The hardware source knows nothing about the selection, so the window has to be given the unit
		// separately or the reading and the selector beside it would disagree.
		Assert.That(values[sensor.ResourceId], Is.EqualTo("122 °F"));
	}

	[Test]
	public void A_sensor_with_no_choice_of_its_own_follows_its_kind_s_default_unit()
	{
		var sensor = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);

		var values = SensorReadingSource.ValuesOf(
			[new HardwareSensor(sensor, 50f)],
			units: null,
			defaults: new Dictionary<SensorType, SensorUnit>
			{
				[SensorType.Temperature] = SensorUnit.Fahrenheit,
			});

		// The window opens before anything has been saved, so a reading and the selector beside it have
		// to agree on the same default.
		Assert.That(values[sensor.ResourceId], Is.EqualTo("122 °F"));
	}

	[Test]
	public void A_sensor_s_own_choice_wins_over_its_kind_s_default()
	{
		var sensor = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);

		var values = SensorReadingSource.ValuesOf(
			[new HardwareSensor(sensor, 50f)],
			new Dictionary<string, SensorUnit>(StringComparer.Ordinal)
			{
				[sensor.ResourceId] = SensorUnit.Celsius,
			},
			new Dictionary<SensorType, SensorUnit>
			{
				[SensorType.Temperature] = SensorUnit.Fahrenheit,
			});

		Assert.That(values[sensor.ResourceId], Is.EqualTo("50 °C"));
	}

	[Test]
	public void A_sensor_already_carrying_a_unit_is_not_overwritten_by_the_default()
	{
		var sensor = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature)
			with { DisplayUnit = SensorUnit.Celsius };

		var values = SensorReadingSource.ValuesOf(
			[new HardwareSensor(sensor, 50f)],
			defaults: new Dictionary<SensorType, SensorUnit>
			{
				[SensorType.Temperature] = SensorUnit.Fahrenheit,
			});

		Assert.That(values[sensor.ResourceId], Is.EqualTo("50 °C"));
	}

	[Test]
	public void The_units_a_selection_carries_are_keyed_the_way_the_window_names_a_sensor()
	{
		var sensor = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature)
			with { DisplayUnit = SensorUnit.Fahrenheit };
		var selection = new SensorSelection();
		selection.Replace([sensor]);

		var units = SensorReadingSource.UnitsOf(selection);

		Assert.That(units, Is.Not.Null);
		Assert.That(units![sensor.ResourceId], Is.EqualTo(SensorUnit.Fahrenheit));
	}

	[Test]
	public void A_selection_with_no_chosen_units_carries_none()
	{
		var sensor = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);
		var selection = new SensorSelection();
		selection.Replace([sensor]);

		Assert.That(SensorReadingSource.UnitsOf(selection), Is.Null);
		Assert.That(SensorReadingSource.UnitsOf(null), Is.Null);
	}

	[Test]
	public void Two_sensors_under_one_id_are_reported_once_the_same_way_the_tree_drops_them()
	{
		var sensor = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);
		var duplicate = sensor with { SensorName = "CPU Core #1 (second instance)" };

		var values = SensorReadingSource.ValuesOf(
			[new HardwareSensor(sensor, 46.6f), new HardwareSensor(duplicate, 90.4f)]);

		Assert.That(values, Has.Count.EqualTo(1));
		Assert.That(values[sensor.ResourceId], Is.EqualTo("47 °C"));
	}
}
