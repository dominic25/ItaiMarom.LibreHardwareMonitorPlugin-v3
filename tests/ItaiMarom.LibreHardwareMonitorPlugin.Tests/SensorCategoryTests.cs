using ItaiMarom.LibreHardwareMonitorPlugin.Configuration;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// How the classic sensor window's tree is grouped: one branch per hardware and reading kind, in the
/// order the hardware reported them.
/// </summary>
/// <remarks>
/// These are the rules that turn the tree's three levels into the branches the window draws. The order
/// matters as much as the grouping does: the old dialog walked the hardware in the order the library
/// listed it, so a name-sorted tree is the difference a user who knows the old dialog would see first.
/// </remarks>
[TestFixture]
public sealed class SensorCategoryTests
{
	[Test]
	public void Branches_keep_the_order_the_hardware_reported_them_in()
	{
		var categories = SensorCategory.Build(
		[
			TestSensor.Create("GPU", "GPU Core", SensorType.Temperature),
			TestSensor.Create("CPU", "CPU Package", SensorType.Temperature),
			TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature),
			TestSensor.Create("CPU", "CPU Total", SensorType.Load),
		]);

		// First seen wins at both levels: the GPU is reported before the CPU, and the CPU's temperatures
		// before its load, so neither level comes out name-sorted.
		Assert.That(
			categories.Select(category => $"{category.RootName} › {category.TypeName}"),
			Is.EqualTo(new List<string>
			{
				"GPU › Temperature",
				"CPU › Temperature",
				"CPU › Load",
			}));

		// And the sensors inside a branch keep the order too - "CPU Package" is not the one that sorts
		// first, it is the one that was reported first.
		Assert.That(
			categories[1].Sensors.Select(sensor => sensor.SensorName),
			Is.EqualTo(new List<string> { "CPU Package", "CPU Core #1" }));
	}

	[Test]
	public void A_branch_holds_only_its_own_hardware_and_reading_kind()
	{
		var load = TestSensor.Create("CPU", "CPU Total", SensorType.Load);
		var temperature = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);
		var gpu = TestSensor.Create("GPU", "GPU Core", SensorType.Temperature);

		var categories = SensorCategory.Build([load, temperature, gpu]);

		Assert.That(categories[0].Sensors, Is.EqualTo(new[] { load }));
		Assert.That(categories[1].Sensors, Is.EqualTo(new[] { temperature }));
		Assert.That(categories[2].Sensors, Is.EqualTo(new[] { gpu }));
	}

	[Test]
	public void Sensors_on_a_sub_device_hang_off_the_hardware_they_belong_to()
	{
		var board = TestSensor.Create("Motherboard", "Board Temperature", SensorType.Temperature);
		var fan = TestSensor.Create("Motherboard", "Fan #1", SensorType.Fan, subHardwareName: "ITE IT8688E");

		var categories = SensorCategory.Build([board, fan]);

		// The old dialog only listed a device's own sensors, so the super-I/O chip behind the motherboard -
		// where the voltages and the fan speeds are - never appeared in it. Those sensors are still the
		// motherboard's, and they are still a leaf under their reading kind: the tree keeps the old
		// dialog's three levels rather than growing a fourth.
		Assert.That(categories.Select(category => category.RootName), Is.All.EqualTo("Motherboard"));
		Assert.That(categories[0].Sensors, Is.EqualTo(new[] { board }));
		Assert.That(categories[1].Sensors, Is.EqualTo(new[] { fan }));
	}

	[Test]
	public void A_reading_kind_is_named_the_way_the_hardware_library_names_it()
	{
		var categories = SensorCategory.Build(
		[
			TestSensor.Create("Disk", "Used Space", SensorType.SmallData),
		]);

		// The old dialog wrote the enum's own name into the branch, so "SmallData" - one word, no space -
		// is what someone who configured the old plugin will be looking for.
		Assert.That(categories[0].TypeName, Is.EqualTo("SmallData"));
	}

	[Test]
	public void A_machine_that_reports_no_sensors_has_no_branches() =>
		Assert.That(SensorCategory.Build([]), Is.Empty);

	[Test]
	public void Each_remote_machine_is_its_own_source_above_its_hardware()
	{
		var livingRoom = new RemoteSourceDefinition("Living Room", "living-room-pc:8085", "living_room");
		var office = new RemoteSourceDefinition("Office PC", "office-pc:8085", "office_pc");

		var categories = SensorCategory.Build(
		[
			TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature),
			TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature, remote: livingRoom),
			TestSensor.Create("GPU", "GPU Core", SensorType.Temperature, remote: office),
		],
		[livingRoom, office]);

		Assert.That(categories[0].Source, Is.Null);
		Assert.That(categories[0].RootName, Is.EqualTo("CPU"));

		// The machine is the container, and the hardware under it keeps its own name: the branch above
		// already says which PC it is, so qualifying every device would only add noise.
		Assert.That(categories[1].Source, Is.EqualTo("living_room"));
		Assert.That(categories[1].MachineName, Is.EqualTo("Living Room"));
		Assert.That(categories[1].RootName, Is.EqualTo("CPU"));
		Assert.That(categories[2].Source, Is.EqualTo("office_pc"));
		Assert.That(categories[2].MachineName, Is.EqualTo("Office PC"));
		Assert.That(categories[2].RootName, Is.EqualTo("GPU"));
	}

	[Test]
	public void Two_machines_with_the_same_hardware_name_do_not_share_a_branch()
	{
		var livingRoom = new RemoteSourceDefinition("Living Room", "living-room-pc:8085", "living_room");
		var office = new RemoteSourceDefinition("Office PC", "office-pc:8085", "office_pc");
		var first = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature, remote: livingRoom);
		var second = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature, remote: office);

		var categories = SensorCategory.Build([first, second], [livingRoom, office]);

		Assert.That(categories, Has.Count.EqualTo(2));
		Assert.That(categories[0].Sensors, Is.EqualTo(new[] { first }));
		Assert.That(categories[1].Sensors, Is.EqualTo(new[] { second }));
	}

	[Test]
	public void A_remote_machine_the_configuration_does_not_know_is_labelled_from_its_root()
	{
		var remote = new RemoteSourceDefinition("Living Room", "living-room-pc:8085", "living_room");
		var sensor = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature, remote: remote);

		// The list the window was opened with can lag the hardware, or an entry can be read from a
		// document this session did not write. A sensor still has to end up under a named machine.
		var categories = SensorCategory.Build([sensor]);

		Assert.That(categories.Single().MachineName, Is.EqualTo("Living Room"));
		Assert.That(categories.Single().RootName, Is.EqualTo("CPU"));
	}
}
