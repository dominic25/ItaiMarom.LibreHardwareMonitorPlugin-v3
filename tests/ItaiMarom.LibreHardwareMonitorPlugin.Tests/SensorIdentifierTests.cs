using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

[TestFixture]
public sealed class SensorIdentifierTests
{
	[Test]
	public void The_legacy_token_is_what_the_old_configuration_stored()
	{
		var sensor = new SensorIdentifier("AMD Ryzen 7 5800X", "CPU Total", SensorType.Load);

		Assert.That(sensor.LegacyToken, Is.EqualTo("CPU Total_Load"));
	}

	[Test]
	public void A_legacy_name_keeps_its_runs_of_underscores()
	{
		var sensor = new SensorIdentifier("CPU", "CPU Core #1", SensorType.Temperature);

		// The old plugin replaced ' ' and '#' separately, so the name really does have two underscores,
		// and a Macro Deck 3 canonicalizer would collapse them. Keeping the old spelling is the difference
		// between an upgraded button working and not.
		Assert.That(sensor.LegacyVariableName, Is.EqualTo("cpu_core__1_temperature"));
		Assert.That(sensor.VariableName, Is.EqualTo("cpu_core__1_temperature"));
	}

	[Test]
	public void A_legacy_name_the_host_would_reject_falls_back_to_a_canonical_one()
	{
		// A canonical name has to start with a letter, and this one does not.
		var sensor = new SensorIdentifier("Motherboard", "3VSB", SensorType.Voltage);

		Assert.That(sensor.LegacyVariableName, Is.EqualTo("3vsb_voltage"));
		Assert.That(sensor.VariableName, Is.EqualTo("v_3vsb_voltage"));
	}

	[Test]
	public void The_root_hardware_is_the_first_segment_of_the_path()
	{
		var sensor = new SensorIdentifier("Motherboard/ITE IT8688E", "Fan #1", SensorType.Fan);

		Assert.That(sensor.RootHardwareName, Is.EqualTo("Motherboard"));
	}

	[Test]
	public void A_sensor_with_no_sub_hardware_is_its_own_root()
	{
		var sensor = new SensorIdentifier("NVIDIA GeForce RTX 3080", "GPU Core", SensorType.Temperature);

		Assert.That(sensor.RootHardwareName, Is.EqualTo("NVIDIA GeForce RTX 3080"));
	}
}
