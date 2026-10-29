using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// How the ids the plugin keys everything by are handed out when the hardware library reports the same
/// one twice.
/// </summary>
/// <remarks>
/// The case these are written around is a real one: an NVIDIA card reports <c>GPU Bus</c> and
/// <c>GPU Memory</c> under the same <c>/gpu-nvidia/0/load/3</c>, and the second of them - the memory load
/// percentage - used to be dropped from the sensor window because the id it would have been ticked under
/// was already taken.
/// </remarks>
[TestFixture]
public sealed class UniqueSensorIdsTests
{
	[Test]
	public void The_gpu_memory_load_sensor_keeps_an_id_of_its_own_beside_gpu_bus()
	{
		var ids = new UniqueSensorIds();

		Assert.That(ids.Apply(Load("/gpu-nvidia/0/load/3", "GPU Bus")).Id, Is.EqualTo("/gpu-nvidia/0/load/3"));
		Assert.That(
			ids.Apply(Load("/gpu-nvidia/0/load/3", "GPU Memory")).Id,
			Is.EqualTo("/gpu-nvidia/0/load/3-2"));
	}

	[Test]
	public void A_settled_id_changes_nothing_about_the_sensor_but_the_id()
	{
		var ids = new UniqueSensorIds();
		ids.Apply(Load("/gpu-nvidia/0/load/3", "GPU Bus"));

		var memory = Load("/gpu-nvidia/0/load/3", "GPU Memory");

		Assert.That(ids.Apply(memory), Is.EqualTo(memory with { Id = "/gpu-nvidia/0/load/3-2" }));
	}

	[Test]
	public void Further_repeats_take_the_next_suffix()
	{
		var ids = new UniqueSensorIds();

		Assert.That(ids.Apply(Load("/test/0/load/3", "First")).Id, Is.EqualTo("/test/0/load/3"));
		Assert.That(ids.Apply(Load("/test/0/load/3", "Second")).Id, Is.EqualTo("/test/0/load/3-2"));
		Assert.That(ids.Apply(Load("/test/0/load/3", "Third")).Id, Is.EqualTo("/test/0/load/3-3"));
	}

	[Test]
	public void Ids_the_library_already_made_unique_are_handed_on_untouched()
	{
		var ids = new UniqueSensorIds();
		var core = Load("/gpu-nvidia/0/load/0", "GPU Core");
		var controller = Load("/gpu-nvidia/0/load/1", "GPU Memory Controller");

		Assert.That(ids.Apply(core), Is.SameAs(core));
		Assert.That(ids.Apply(controller), Is.SameAs(controller));
	}

	private static SensorIdentifier Load(string id, string name) =>
		new(HardwareLocation.Root("/gpu-nvidia/0", "NVIDIA GeForce RTX 4080"), id, name, SensorType.Load);
}
