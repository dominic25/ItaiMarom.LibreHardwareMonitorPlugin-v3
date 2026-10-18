using ItaiMarom.LibreHardwareMonitorPlugin.Configuration;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
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
