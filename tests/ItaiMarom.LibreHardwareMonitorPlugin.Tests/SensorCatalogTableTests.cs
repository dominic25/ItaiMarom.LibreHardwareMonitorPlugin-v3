using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The flat view of the hardware tree that the browsable catalog serves.
/// </summary>
[TestFixture]
public sealed class SensorCatalogTableTests
{
	[Test]
	public void The_tree_is_one_node_per_top_level_device()
	{
		var cpu = TestSensor.Create("CPU", "CPU Total", SensorType.Load);
		var board = TestSensor.Create("Motherboard", "3VSB", SensorType.Voltage);

		var table = SensorCatalogTable.Build([cpu, board]);

		Assert.That(table.Hardware.Select(node => node.Name), Is.EqualTo(new List<string> { "CPU", "Motherboard" }));
	}

	[Test]
	public void Sub_hardware_is_folded_into_the_device_it_belongs_to()
	{
		// A motherboard's voltages and fan speeds come from a super-I/O chip the user has never heard of.
		// The old plugin never listed them at all; they belong under "Motherboard", not under the chip.
		var onBoard = TestSensor.Create("Motherboard", "3VSB", SensorType.Voltage);
		var onChip = TestSensor.Create("Motherboard", "Fan #1", SensorType.Fan, subHardwareName: "ITE IT8688E");

		var table = SensorCatalogTable.Build([onBoard, onChip]);

		Assert.That(table.Hardware, Has.Count.EqualTo(1));
		Assert.That(table.Hardware[0].Entries, Has.Count.EqualTo(2));
		Assert.That(
			table.Hardware[0].Entries.Select(entry => entry.Sensor.SensorName),
			Is.EqualTo(new List<string> { "3VSB", "Fan #1" }));
		Assert.That(table.Sensors, Has.Count.EqualTo(2));
	}

	[Test]
	public void A_sensor_is_found_by_the_resource_id_it_was_offered_under()
	{
		var cpu = TestSensor.Create("CPU", "CPU Total", SensorType.Load);
		var board = TestSensor.Create("Motherboard", "3VSB", SensorType.Voltage);

		var table = SensorCatalogTable.Build([cpu, board]);

		Assert.That(table.TryGetSensor(cpu.ResourceId, out var found), Is.True);
		Assert.That(found, Is.EqualTo(cpu));
		Assert.That(table.TryGetSensor("/not/a/sensor", out _), Is.False);
	}

	[Test]
	public void Hardware_is_found_by_its_node_id()
	{
		var cpu = TestSensor.Create("CPU", "CPU Total", SensorType.Load);

		var table = SensorCatalogTable.Build([cpu]);

		Assert.That(table.TryGetHardware(VariableNaming.ToResourceId("/test/cpu"), out var node), Is.True);
		Assert.That(node.Name, Is.EqualTo("CPU"));
		Assert.That(table.TryGetHardware("/test/gpu", out _), Is.False);
	}

	[Test]
	public void Every_entry_knows_which_node_it_hangs_under()
	{
		var cpu = TestSensor.Create("CPU", "CPU Total", SensorType.Load);

		var table = SensorCatalogTable.Build([cpu]);

		Assert.That(table.Sensors[0].ParentId, Is.EqualTo(table.Hardware[0].Id));
	}

	[Test]
	public void No_sensor_is_reachable_at_a_hardware_nodes_id()
	{
		// Node ids and sensor ids are handed out from one pool, so a browser asking for a node can never be
		// handed a sensor - or the other way round.
		var cpu = TestSensor.Create("CPU", "CPU Total", SensorType.Load);
		var board = TestSensor.Create("Motherboard", "3VSB", SensorType.Voltage);

		var table = SensorCatalogTable.Build([cpu, board]);
		var ids = table.Hardware.Select(node => node.Id)
			.Concat(table.Sensors.Select(entry => entry.Id))
			.ToArray();

		Assert.That(ids, Has.All.Matches<string>(id => VariableNaming.IsValidResourceId(id)));
		Assert.That(ids.Distinct().ToArray(), Has.Length.EqualTo(ids.Length));
	}

	[Test]
	public void Two_sensors_offered_the_same_resource_id_are_separated()
	{
		// Real hardware identifiers are unique, but a library that reported the same one twice would
		// otherwise make one sensor permanently unreachable.
		var first = new SensorIdentifier(
			HardwareLocation.Root("/test/gpu/0", "GPU 0"),
			"/test/gpu/temperature/core",
			"GPU Core",
			SensorType.Temperature);
		var second = new SensorIdentifier(
			HardwareLocation.Root("/test/gpu/1", "GPU 1"),
			"/test/gpu/temperature/core",
			"GPU Core",
			SensorType.Temperature);

		var table = SensorCatalogTable.Build([first, second]);

		Assert.That(table.Sensors.Select(entry => entry.Id).Distinct().ToArray(), Has.Length.EqualTo(2));
		Assert.That(
			table.Sensors.Select(entry => entry.Id).Count(id => id == first.ResourceId),
			Is.EqualTo(1));
		Assert.That(
			table.Sensors
				.Select(entry => (entry.Id, entry.Sensor))
				.All(entry => table.TryGetSensor(entry.Id, out var found) && found == entry.Sensor),
			Is.True);
	}

	[Test]
	public void An_empty_tree_builds_an_empty_table()
	{
		var table = SensorCatalogTable.Build([]);

		Assert.That(table.Hardware, Is.Empty);
		Assert.That(table.Sensors, Is.Empty);
		Assert.That(table.TryGetSensor("/anything", out _), Is.False);
	}

	[Test]
	public void A_stable_order_is_kept_across_builds()
	{
		var sensors = new[]
		{
			TestSensor.Create("Motherboard", "3VSB", SensorType.Voltage),
			TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature),
			TestSensor.Create("CPU", "CPU Total", SensorType.Load),
		};

		var first = SensorCatalogTable.Build(sensors);
		var second = SensorCatalogTable.Build(sensors.Reverse().ToArray());

		Assert.That(
			first.Hardware.Select(node => node.Name),
			Is.EqualTo(second.Hardware.Select(node => node.Name)));
		Assert.That(
			first.Sensors.Select(entry => entry.Sensor),
			Is.EqualTo(second.Sensors.Select(entry => entry.Sensor)));
	}
}
