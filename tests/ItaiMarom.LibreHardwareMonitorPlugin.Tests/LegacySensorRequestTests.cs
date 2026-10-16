using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Migration;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// Reading the old plugin's <c>requestedSensors</c> setting and turning it back into sensors. Nothing
/// here touches hardware: a request is a display name, and matching it is string work.
/// </summary>
[TestFixture]
public sealed class LegacySensorRequestTests
{
	private static readonly SensorIdentifier CpuTotal =
		TestSensor.Create("CPU", "CPU Total", SensorType.Load);

	private static readonly SensorIdentifier GpuCore =
		TestSensor.Create("GPU", "GPU Core", SensorType.Temperature);

	[Test]
	public void The_newtonsoft_tuple_shape_is_parsed()
	{
		// The old plugin serialized a List<(string hardware, string type, string sensor)>, and
		// Newtonsoft.Json writes a value tuple's fields rather than its element names.
		var requests = LegacySensorRequest.Parse(
			"""[{"Item1":"CPU","Item2":"Load","Item3":"CPU Total_Load"}]""");

		Assert.That(requests, Has.Count.EqualTo(1));
		Assert.That(requests[0].Hardware, Is.EqualTo("CPU"));
		Assert.That(requests[0].SensorType, Is.EqualTo("Load"));
		Assert.That(requests[0].Sensor, Is.EqualTo("CPU Total_Load"));
	}

	[Test]
	public void The_named_and_plain_array_shapes_are_parsed()
	{
		var named = LegacySensorRequest.Parse(
			"""[{"hardware":"GPU","type":"Temperature","sensor":"GPU Core_Temperature"}]""");
		var array = LegacySensorRequest.Parse(
			"""[["GPU","Temperature","GPU Core_Temperature"]]""");

		Assert.That(named, Has.Count.EqualTo(1));
		Assert.That(named[0].Hardware, Is.EqualTo("GPU"));
		Assert.That(array, Has.Count.EqualTo(1));
		Assert.That(array[0].Sensor, Is.EqualTo("GPU Core_Temperature"));
	}

	[Test]
	public void A_value_stored_as_a_json_string_is_unwrapped()
	{
		// The old plugin read its setting back through a string-typed configuration API, so the list can
		// legitimately arrive as a JSON document inside a JSON string.
		var requests = LegacySensorRequest.Parse(
			"\"[{\\\"Item1\\\":\\\"CPU\\\",\\\"Item2\\\":\\\"Load\\\",\\\"Item3\\\":\\\"CPU Total_Load\\\"}]\"");

		Assert.That(requests, Has.Count.EqualTo(1));
		Assert.That(requests[0].Sensor, Is.EqualTo("CPU Total_Load"));
	}

	[Test]
	public void Anything_unusable_parses_to_nothing()
	{
		// A migration that throws on a malformed setting leaves the user with nothing, which is worse than
		// migrating the lines that did parse.
		Assert.That(LegacySensorRequest.Parse(null), Is.Empty);
		Assert.That(LegacySensorRequest.Parse(""), Is.Empty);
		Assert.That(LegacySensorRequest.Parse("not json"), Is.Empty);
		Assert.That(LegacySensorRequest.Parse("{}"), Is.Empty);
		Assert.That(LegacySensorRequest.Parse("""[{"Item1":"CPU"}]"""), Is.Empty);
	}

	[Test]
	public void A_request_names_its_hardware_and_no_other()
	{
		var request = new LegacySensorRequest("GPU", "Temperature", "GPU Core_Temperature");

		Assert.That(request.Matches(GpuCore), Is.True);
		Assert.That(request.Matches(CpuTotal), Is.False);
	}

	[Test]
	public void A_request_without_hardware_matches_the_token_anywhere()
	{
		// A settings file that never carried the device name still tells the migration which sensor the
		// user wanted, which is better than throwing the only record of the selection away.
		var request = new LegacySensorRequest(string.Empty, string.Empty, "CPU Total_Load");

		Assert.That(request.Matches(CpuTotal), Is.True);
		Assert.That(request.Matches(GpuCore), Is.False);
	}

	[Test]
	public void The_token_is_compared_reduced_the_way_the_old_plugin_reduced_it()
	{
		var request = new LegacySensorRequest("CPU", "Load", "CPU Total_Load");

		// The stored token is a display name: the old plugin compared the sanitised, lowercased form.
		Assert.That(request.CanonicalSensor, Is.EqualTo(CpuTotal.LegacyVariableName));
	}

	[Test]
	public void Every_sensor_a_request_names_is_kept()
	{
		// Two identical graphics cards present the same token on the same-named hardware. The old plugin
		// updated every match, so dropping one would silently lose a device the user was watching.
		var secondGpu = new SensorIdentifier(
			new HardwareLocation("/gpu/1", "GPU", "GPU", "/gpu/1", "GPU"),
			"/gpu/1/temperature/gpu_core",
			"GPU Core",
			SensorType.Temperature);
		var request = new LegacySensorRequest("GPU", "Temperature", "GPU Core_Temperature");

		var selected = LegacySensorSelection.Select([request], [GpuCore, secondGpu, CpuTotal]);

		Assert.That(selected, Is.EqualTo(new[] { GpuCore, secondGpu }));
	}

	[Test]
	public void A_sensor_named_by_two_requests_is_selected_once()
	{
		var requests = new[]
		{
			new LegacySensorRequest("CPU", "Load", "CPU Total_Load"),
			new LegacySensorRequest(string.Empty, string.Empty, "CPU Total_Load"),
		};

		var selected = LegacySensorSelection.Select(requests, [CpuTotal]);

		Assert.That(selected, Is.EqualTo(new[] { CpuTotal }));
	}
}
