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
		var sensor = TestSensor.Create("AMD Ryzen 7 5800X", "CPU Total", SensorType.Load);

		Assert.That(sensor.LegacyToken, Is.EqualTo("CPU Total_Load"));
	}

	[Test]
	public void A_legacy_name_keeps_its_runs_of_underscores()
	{
		var sensor = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);

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
		var sensor = TestSensor.Create("Motherboard", "3VSB", SensorType.Voltage);

		Assert.That(sensor.LegacyVariableName, Is.EqualTo("3vsb_voltage"));
		Assert.That(sensor.VariableName, Is.EqualTo("v_3vsb_voltage"));
	}

	[Test]
	public void The_root_hardware_is_the_top_of_the_branch_a_sub_device_hangs_off()
	{
		var sensor = TestSensor.Create("Motherboard", "Fan #1", SensorType.Fan, subHardwareName: "ITE IT8688E");

		Assert.That(sensor.Hardware.Path, Is.EqualTo("Motherboard/ITE IT8688E"));
		Assert.That(sensor.RootHardwareName, Is.EqualTo("Motherboard"));
	}

	[Test]
	public void A_sensor_with_no_sub_hardware_is_its_own_root()
	{
		var sensor = TestSensor.Create("NVIDIA GeForce RTX 3080", "GPU Core", SensorType.Temperature);

		Assert.That(sensor.RootHardwareName, Is.EqualTo("NVIDIA GeForce RTX 3080"));
	}

	[Test]
	public void A_variable_id_is_derived_from_the_library_identifier_not_the_name()
	{
		var sensor = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);

		// The name would give "cpu-core-1-temperature" - and so would the second core of a second, identical
		// processor. The library identifier is what tells them apart.
		Assert.That(sensor.VariableId, Is.EqualTo("test-cpu-temperature-cpu-core-1"));
	}

	[Test]
	public void A_resource_id_keeps_the_library_identifier_readable()
	{
		var sensor = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);

		// Catalog ids may carry '/', so a user reading a resource id can still see where the sensor lives.
		Assert.That(sensor.ResourceId, Is.EqualTo("/test/cpu/temperature/cpu-core-1"));
		Assert.That(VariableNaming.IsValidResourceId(sensor.ResourceId), Is.True);
	}

	[Test]
	public void Whether_a_control_was_found_is_not_part_of_a_sensor_s_identity()
	{
		var sensor = TestSensor.Create("Motherboard", "Fan #1", SensorType.Control, subHardwareName: "Nuvoton NCT6798D");
		var controllable = sensor with { Controllable = true };

		// A dictionary keyed by identity must not split when a control appears or goes away, and a stored
		// selection must still match the live sensor after the driver is or is not loaded.
		Assert.That(controllable, Is.EqualTo(sensor));
		Assert.That(controllable.GetHashCode(), Is.EqualTo(sensor.GetHashCode()));
	}

	[Test]
	public void The_display_name_carries_the_kind_because_the_name_alone_is_ambiguous()
	{
		var sensor = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);

		Assert.That(sensor.DisplayName, Is.EqualTo("CPU Core #1 (Temperature)"));
	}
}
