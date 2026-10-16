using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// How a set of selected sensors is turned into names. Names are the compatibility contract: a Macro Deck
/// 2 layout refers to a sensor by the name the old plugin gave it, so this is where an upgrade either
/// keeps working or does not.
/// </summary>
[TestFixture]
public sealed class SensorVariableNamingTests
{
	[Test]
	public void Sensors_that_do_not_collide_keep_the_legacy_name()
	{
		var cpu = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);
		var gpu = TestSensor.Create("NVIDIA GeForce RTX 3080", "GPU Core", SensorType.Temperature);

		var variables = SensorVariableNaming.Assign([cpu, gpu]);

		Assert.That(ById(variables, cpu).Name, Is.EqualTo("cpu_core__1_temperature"));
		Assert.That(ById(variables, gpu).Name, Is.EqualTo("gpu_core_temperature"));
	}

	[Test]
	public void A_variable_id_is_the_sensors_stable_identifier()
	{
		var cpu = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);

		var variables = SensorVariableNaming.Assign([cpu]);

		Assert.That(ById(variables, cpu).Id, Is.EqualTo(cpu.VariableId));
	}

	[Test]
	public void The_first_of_two_colliding_sensors_keeps_the_legacy_name_and_the_rest_are_qualified()
	{
		// Two graphics cards report the same sensor name. The old plugin only ever looked at the sensor
		// name, so it produced the same variable twice and one silently shadowed the other. The user's
		// choice is to keep the old spelling for one of them and to say which device the other is on.
		var older = TestSensor.Create("NVIDIA GeForce RTX 3080", "GPU Core", SensorType.Temperature);
		var newer = TestSensor.Create("NVIDIA GeForce RTX 3090", "GPU Core", SensorType.Temperature);

		var variables = SensorVariableNaming.Assign([newer, older]);

		Assert.That(ById(variables, older).Name, Is.EqualTo("gpu_core_temperature"));
		Assert.That(
			ById(variables, newer).Name,
			Is.EqualTo("nvidia_geforce_rtx_3090_gpu_core_temperature"));
	}

	[Test]
	public void Which_sensor_wins_a_collision_does_not_depend_on_the_order_it_was_found()
	{
		var older = TestSensor.Create("NVIDIA GeForce RTX 3080", "GPU Core", SensorType.Temperature);
		var newer = TestSensor.Create("NVIDIA GeForce RTX 3090", "GPU Core", SensorType.Temperature);

		// A restart can enumerate the hardware in a different order; if that renumbered the variables, a
		// user's buttons would point at the other card after every boot.
		Assert.That(
			SensorVariableNaming.Assign([older, newer]).Select(variable => variable.Name),
			Is.EqualTo(SensorVariableNaming.Assign([newer, older]).Select(variable => variable.Name)));
	}

	[Test]
	public void A_sensor_on_a_sub_device_is_qualified_with_the_device_it_is_on()
	{
		// The motherboard's own sensor and a super-I/O chip's sensor of the same name: the same collision,
		// but one level deeper, where the top-level name is not enough to tell them apart.
		var onBoard = TestSensor.Create("Motherboard", "Voltage", SensorType.Voltage);
		var onChip = TestSensor.Create("Motherboard", "Voltage", SensorType.Voltage, subHardwareName: "ITE IT8688E");

		var variables = SensorVariableNaming.Assign([onChip, onBoard]);

		Assert.That(ById(variables, onBoard).Name, Is.EqualTo("voltage_voltage"));
		Assert.That(ById(variables, onChip).Name, Is.EqualTo("motherboard_voltage_voltage"));
	}

	[Test]
	public void Every_assigned_name_is_a_legal_variable_name()
	{
		var variables = SensorVariableNaming.Assign(
		[
			TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature),
			TestSensor.Create("Motherboard", "3VSB", SensorType.Voltage),
			TestSensor.Create("Motherboard", "3VSB", SensorType.Voltage, subHardwareName: "ITE IT8688E"),
			TestSensor.Create("Generic Hard Drive", "", SensorType.Temperature),
		]);

		Assert.That(variables, Is.Not.Empty);
		Assert.That(variables, Has.All.Matches<SensorVariable>(variable => VariableNaming.IsValid(variable.Name)));
	}

	[Test]
	public void Sensors_whose_identifiers_derive_the_same_id_still_get_distinct_ids()
	{
		// Two sensor names the library reports differently but that slugify alike - so the derived ids
		// collide even though the sensors do not. Ids only have to be unique and stable, so the second one
		// is numbered.
		var hash = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);
		var plain = TestSensor.Create("CPU", "CPU Core 1", SensorType.Temperature);

		var variables = SensorVariableNaming.Assign([hash, plain]);
		var ids = variables.Select(variable => variable.Id).ToArray();

		Assert.That(ids.Distinct().ToArray(), Has.Length.EqualTo(2));
		Assert.That(ids.Count(id => id == hash.VariableId), Is.EqualTo(1));
	}

	[Test]
	public void A_device_whose_name_sanitises_to_nothing_still_gets_a_usable_name()
	{
		// Nothing stops the hardware library from naming a device something that is neither a letter nor a
		// digit. An empty variable name would be rejected by the host, so it has to fall through.
		var sensor = new SensorIdentifier(
			HardwareLocation.Root("/test/\u00a0", "\u00a0"),
			"/test/0/1/2",
			"\u00a0",
			SensorType.Temperature);

		var variable = SensorVariableNaming.Assign([sensor]).Single();

		Assert.That(VariableNaming.IsValid(variable.Name), Is.True);
		Assert.That(variable.Id, Is.EqualTo("test-0-1-2"));
	}

	private static SensorVariable ById(IReadOnlyList<SensorVariable> variables, SensorIdentifier sensor) =>
		variables.Single(variable => variable.Sensor == sensor);
}
