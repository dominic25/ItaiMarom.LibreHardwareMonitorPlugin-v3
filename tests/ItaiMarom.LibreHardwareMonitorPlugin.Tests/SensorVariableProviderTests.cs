using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using LibreHardwareMonitor.Hardware;
using MacroDeck.Sdk.Variables;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The variable surface: what is declared, what a read answers, and what the catalog offers. Driven
/// against fake hardware so nothing here needs a machine with sensors on it.
/// </summary>
[TestFixture]
public sealed class SensorVariableProviderTests
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

	[Test]
	public async Task Nothing_is_declared_until_something_is_selected()
	{
		await using var fixture = new Fixture();

		// The old plugin's variables were whatever the user had configured, so there is nothing to declare
		// until a configuration - or a migration - has produced a selection. Saying so is what tells the
		// host to ask again after a configuration change.
		Assert.That(fixture.Provider.Variables, Is.Empty);
		Assert.That(fixture.Provider.VariablesDependOnConfiguration, Is.True);
		Assert.That(fixture.Provider.SupportsCatalog, Is.True);
		Assert.That(fixture.Provider.SupportsSearch, Is.True);
	}

	[Test]
	public async Task A_selected_sensor_is_declared_under_the_name_the_old_plugin_used()
	{
		await using var fixture = new Fixture();
		var sensor = fixture.Add("CPU", "CPU Core #1", SensorType.Temperature);
		fixture.Select(sensor);

		var definition = fixture.Provider.Variables.Single();

		Assert.That(definition.Name, Is.EqualTo("cpu_core__1_temperature"));
		Assert.That(definition.Materialization, Is.EqualTo(VariableMaterialization.Eager));
		Assert.That(definition.Type, Is.EqualTo(VariableType.Numeric));

		// The host reads a declared variable by its resolved id, and the id it derives from a name is empty
		// for a name with an underscore in it - which is every name the old plugin produced. Setting it
		// explicitly is the whole reason this works.
		Assert.That(definition.ResolvedId, Is.Not.Empty);
		Assert.That(definition.ResolvedId, Is.EqualTo(sensor.VariableId));
	}

	[Test]
	public async Task A_declared_sensor_carries_its_kind_as_a_precision_and_a_unit()
	{
		await using var fixture = new Fixture();
		var load = fixture.Add("CPU", "CPU Total", SensorType.Load);
		var voltage = fixture.Add("Motherboard", "3VSB", SensorType.Voltage);
		fixture.Select(load, voltage);

		var definitions = fixture.Provider.Variables;

		// The old plugin stored a load as an integer and a voltage with its fraction; that difference is
		// kept as a display precision rather than the value being truncated.
		var loadDefinition = definitions.Single(definition => definition.Name == "cpu_total_load");
		Assert.That(loadDefinition.DecimalPlaces, Is.EqualTo(0));
		Assert.That(loadDefinition.Unit, Is.EqualTo("%"));
		Assert.That(loadDefinition.SemanticKind, Is.EqualTo(VariableSemanticKinds.Percentage));

		var voltageDefinition = definitions.Single(definition => definition.Name == "v_3vsb_voltage");
		Assert.That(voltageDefinition.DecimalPlaces, Is.EqualTo(1));
		Assert.That(voltageDefinition.Unit, Is.EqualTo("V"));
	}

	[Test]
	public async Task A_sensor_that_is_no_longer_selected_stops_being_declared()
	{
		await using var fixture = new Fixture();
		var cpu = fixture.Add("CPU", "CPU Total", SensorType.Load);
		var gpu = fixture.Add("GPU", "GPU Core", SensorType.Temperature);

		fixture.Select(cpu, gpu);
		Assert.That(fixture.Provider.Variables, Has.Count.EqualTo(2));

		fixture.Select(cpu);

		Assert.That(fixture.Provider.Variables.Select(definition => definition.Name), Is.EqualTo(new List<string> { "cpu_total_load" }));
	}

	[Test]
	public async Task A_sensor_the_hardware_gave_a_control_is_declared_writable()
	{
		await using var fixture = new Fixture();
		var fan = fixture.AddControl("Fan #1");
		fixture.Select(fan);

		var definition = fixture.Provider.Variables.Single();

		Assert.That(definition.CanWrite, Is.True);
		Assert.That(definition.Write, Is.Not.Null);
		Assert.That(definition.Write!.CommitOnRelease, Is.True);
	}

	[Test]
	public async Task A_sensor_without_a_control_is_declared_read_only()
	{
		await using var fixture = new Fixture();
		var load = fixture.Add("CPU", "CPU Total", SensorType.Load, 42f);
		fixture.Select(load);

		var definition = fixture.Provider.Variables.Single();

		Assert.That(definition.CanWrite, Is.False);
		Assert.That(definition.Write, Is.Null);
	}

	[Test]
	public async Task A_writable_sensor_reads_with_the_range_its_control_reports()
	{
		await using var fixture = new Fixture();
		var fan = fixture.AddControl("Fan #1", min: 20f, max: 80f, value: 50f);
		fixture.Select(fan);
		fixture.Watch();
		fixture.Start();
		await WaitUntilAsync(() => fixture.Monitor.TryGetReading(fan, out _));

		// A bound Slider takes its track from the reading, so the chip's own range has to travel with the
		// value rather than being assumed to be 0-100.
		Assert.That(
			await fixture.Provider.ReadAsync(fan.VariableId),
			Is.EqualTo(VariableReading.Of(50d, 20d, 80d, 1d)));
	}

	[Test]
	public async Task Writing_a_writable_sensor_moves_the_hardware()
	{
		await using var fixture = new Fixture();
		var fan = fixture.AddControl("Fan #1", value: 50f);
		fixture.Select(fan);
		fixture.Watch();
		fixture.Start();
		await WaitUntilAsync(() => fixture.Monitor.TryGetReading(fan, out _));

		var result = await fixture.Provider.SetValueAsync(fan.VariableId, 40d);

		Assert.That(result.Status, Is.EqualTo(VariableWriteStatus.Applied));
		Assert.That(fixture.Source.LastSetSensorId, Is.EqualTo(fan.Id));
		Assert.That(fixture.Source.LastSetValue, Is.EqualTo(40f).Within(0.001f));

		// The write is followed by a read of the hardware, not by an echo of what was written.
		await WaitUntilAsync(async () => await fixture.Provider.ReadAsync(fan.VariableId) == VariableReading.Of(40d, 0d, 100d, 1d));
	}

	[Test]
	public async Task A_write_is_held_to_the_control_s_own_range()
	{
		await using var fixture = new Fixture();
		var fan = fixture.AddControl("Fan #1", min: 20f, max: 80f, value: 50f);
		fixture.Select(fan);
		fixture.Watch();
		fixture.Start();
		await WaitUntilAsync(() => fixture.Monitor.TryGetReading(fan, out _));

		var result = await fixture.Provider.SetValueAsync(fan.VariableId, 5d);

		Assert.That(result.Status, Is.EqualTo(VariableWriteStatus.Applied));
		Assert.That(fixture.Source.LastSetValue, Is.EqualTo(20f).Within(0.001f));
		await WaitUntilAsync(async () => await fixture.Provider.ReadAsync(fan.VariableId) == VariableReading.Of(20d, 20d, 80d, 1d));
	}

	[Test]
	public async Task A_value_that_is_not_a_number_is_refused_without_writing()
	{
		await using var fixture = new Fixture();
		var fan = fixture.AddControl("Fan #1");
		fixture.Select(fan);

		var result = await fixture.Provider.SetValueAsync(fan.VariableId, "full");

		Assert.That(result.Status, Is.EqualTo(VariableWriteStatus.InvalidValue));
		Assert.That(fixture.Source.LastSetSensorId, Is.Null);
	}

	[Test]
	public async Task Writing_an_id_nothing_knows_about_answers_not_found()
	{
		await using var fixture = new Fixture();

		var result = await fixture.Provider.SetValueAsync("not_a_variable", 40d);

		Assert.That(result.Status, Is.EqualTo(VariableWriteStatus.NotFound));
	}

	[Test]
	public async Task Writing_a_read_only_sensor_is_refused_without_writing()
	{
		await using var fixture = new Fixture();
		var load = fixture.Add("CPU", "CPU Total", SensorType.Load, 42f);
		fixture.Select(load);

		var result = await fixture.Provider.SetValueAsync(load.VariableId, 40d);

		Assert.That(result.Status, Is.EqualTo(VariableWriteStatus.Unavailable));
		Assert.That(fixture.Source.LastSetSensorId, Is.Null);
	}

	[Test]
	public async Task Writing_a_sensor_whose_control_went_away_answers_unavailable()
	{
		await using var fixture = new Fixture();

		// The selection remembers the capability, but this process found no control to write through.
		var fan = TestFanActions.Control("Fan #1") with { Controllable = true };
		fixture.Source.Add(fan, 50f);
		fixture.Select(fan);

		var result = await fixture.Provider.SetValueAsync(fan.VariableId, 40d);

		Assert.That(result.Status, Is.EqualTo(VariableWriteStatus.Unavailable));
		Assert.That(fixture.Source.LastSetSensorId, Is.Null);
	}

	[Test]
	public async Task The_catalog_marks_the_sensors_it_can_write()
	{
		await using var fixture = new Fixture();
		var fan = fixture.AddControl("Fan #1");
		fixture.Add("Motherboard", "3VSB", SensorType.Voltage);

		var roots = await fixture.Provider.DiscoverAsync(new VariableCatalogQuery());
		var motherboard = roots.Items.Single(item => item.Name == "motherboard");
		var page = await fixture.Provider.DiscoverAsync(new VariableCatalogQuery { ParentId = motherboard.Id });

		var fanDefinition = page.Items.Single(item => item.ResolvedId == fan.ResourceId);
		var voltageDefinition = page.Items.Single(item => item.Name == "v_3vsb_voltage");

		Assert.That(fanDefinition.CanWrite, Is.True);
		Assert.That(fanDefinition.Write!.CommitOnRelease, Is.True);
		Assert.That(voltageDefinition.CanWrite, Is.False);
		Assert.That(voltageDefinition.Write, Is.Null);
	}

	[Test]
	public async Task A_declared_sensor_reads_the_value_the_loop_cached()
	{
		await using var fixture = new Fixture();
		var sensor = fixture.Add("CPU", "CPU Total", SensorType.Load, 42f);
		fixture.Select(sensor);

		fixture.Watch();
		fixture.Start();
		await WaitUntilAsync(() => fixture.Monitor.TryGetReading(sensor, out _));

		Assert.That(await fixture.Provider.ReadAsync(sensor.VariableId), Is.EqualTo(VariableReading.Of(42d)));
	}

	[Test]
	public async Task A_read_never_touches_the_hardware_library()
	{
		await using var fixture = new Fixture();
		var gpu = fixture.Add("GPU", "GPU Core", SensorType.Temperature, 60f);

		// Nothing has enumerated anything yet. If a read were allowed to enumerate, one variable read could
		// block on a disk; the answer has to be "unavailable" instead.
		var reading = await fixture.Provider.ReadAsync(gpu.ResourceId);

		Assert.That(reading, Is.EqualTo(VariableReading.Unavailable));
		Assert.That(fixture.Source.ReadCount, Is.EqualTo(0));
		Assert.That(fixture.Source.OpenCount, Is.EqualTo(0));
	}

	[Test]
	public async Task An_id_nothing_knows_about_reads_as_unavailable()
	{
		await using var fixture = new Fixture();

		Assert.That(await fixture.Provider.ReadAsync("not_a_variable"), Is.EqualTo(VariableReading.Unavailable));
	}

	[Test]
	public async Task A_sensor_that_stopped_reporting_reads_as_unavailable()
	{
		await using var fixture = new Fixture();
		var sensor = fixture.Add("CPU", "CPU Total", SensorType.Load, 42f);
		fixture.Select(sensor);
		fixture.Watch();
		fixture.Start();
		await WaitUntilAsync(() => fixture.Monitor.TryGetReading(sensor, out _));

		fixture.Source.Set(sensor, null);

		await WaitUntilAsync(async () => await fixture.Provider.ReadAsync(sensor.VariableId) == VariableReading.Unavailable);
	}

	[Test]
	public async Task Resolving_a_declared_variable_answers_with_the_definition_it_was_declared_under()
	{
		await using var fixture = new Fixture();
		var sensor = fixture.Add("CPU", "CPU Total", SensorType.Load);
		fixture.Select(sensor);

		var definition = await fixture.Provider.ResolveAsync(sensor.VariableId);

		Assert.That(definition, Is.Not.Null);
		Assert.That(definition!.Name, Is.EqualTo("cpu_total_load"));
		Assert.That(definition.Materialization, Is.EqualTo(VariableMaterialization.Eager));
	}

	[Test]
	public async Task Resolving_a_sensor_that_is_installed_answers_with_its_description()
	{
		await using var fixture = new Fixture();
		var sensor = fixture.Add("Motherboard", "3VSB", SensorType.Voltage);

		// Browsing first is what makes the description available; a restore is expected to happen after the
		// user has been in the catalog, or after the plugin has enumerated on its own.
		await fixture.Provider.DiscoverAsync(new VariableCatalogQuery());

		var definition = await fixture.Provider.ResolveAsync(sensor.ResourceId);

		Assert.That(definition, Is.Not.Null);
		Assert.That(definition!.Materialization, Is.EqualTo(VariableMaterialization.OnDemand));
		Assert.That(definition.Name, Is.EqualTo("v_3vsb_voltage"));
		Assert.That(definition.Unit, Is.EqualTo("V"));
		Assert.That(definition.DecimalPlaces, Is.EqualTo(1));
	}

	[Test]
	public async Task Resolving_a_sensor_that_is_not_installed_still_answers()
	{
		await using var fixture = new Fixture();

		// A profile can be opened on a machine without that hardware, or months after the device was sold.
		// The binding is kept and reads as unavailable rather than being reported as broken.
		var definition = await fixture.Provider.ResolveAsync("/gpu-nvidia/0/temperature/0");

		Assert.That(definition, Is.Not.Null);
		Assert.That(definition!.Materialization, Is.EqualTo(VariableMaterialization.OnDemand));
		Assert.That(definition.Type, Is.EqualTo(VariableType.Numeric));
	}

	[TestCase("a::b")]
	[TestCase("has whitespace")]
	[TestCase("")]
	[TestCase(null)]
	public async Task Resolving_text_that_is_not_a_resource_id_is_refused(string? id)
	{
		await using var fixture = new Fixture();

		Assert.That(await fixture.Provider.ResolveAsync(id!), Is.Null);
	}

	[Test]
	public async Task Subscribing_watches_the_selection_and_the_named_resources()
	{
		await using var fixture = new Fixture();
		var cpu = fixture.Add("CPU", "CPU Total", SensorType.Load, 42f);
		var gpu = fixture.Add("GPU", "GPU Core", SensorType.Temperature, 60f);
		fixture.Select(cpu);

		// A declared variable is read without ever being subscribed to, so the selection is watched
		// regardless; the graphics card is only watched because it was named.
		await fixture.Provider.SubscribeAsync([gpu.ResourceId]);

		fixture.Start();

		await WaitUntilAsync(() => fixture.Source.LastFilter is not null);
		Assert.That(fixture.Source.LastFilter, Is.EquivalentTo(new List<string> { "CPU", "GPU" }));
	}

	[Test]
	public async Task Subscribing_again_stops_watching_what_is_no_longer_named()
	{
		await using var fixture = new Fixture();
		var cpu = fixture.Add("CPU", "CPU Total", SensorType.Load, 42f);
		var gpu = fixture.Add("GPU", "GPU Core", SensorType.Temperature, 60f);
		fixture.Select(cpu);

		await fixture.Provider.SubscribeAsync([gpu.ResourceId]);
		fixture.Start();
		await WaitUntilAsync(() => fixture.Source.LastFilter?.Count == 2);

		// The user unbound the graphics card: it must stop costing a read rather than quietly going stale.
		await fixture.Provider.SubscribeAsync([]);

		await WaitUntilAsync(() => fixture.Source.LastFilter?.Count == 1);
		Assert.That(fixture.Source.LastFilter, Is.EquivalentTo(new List<string> { "CPU" }));
	}

	[Test]
	public async Task The_catalog_opens_on_the_hardware()
	{
		await using var fixture = new Fixture();
		fixture.Add("CPU", "CPU Total", SensorType.Load);
		fixture.Add("Motherboard", "3VSB", SensorType.Voltage);

		var page = await fixture.Provider.DiscoverAsync(new VariableCatalogQuery());

		Assert.That(page.Items.Select(item => item.Name), Is.EqualTo(new List<string> { "cpu", "motherboard" }));
		Assert.That(page.Items, Has.All.Matches<VariableDefinition>(item => item.IsContainer));
		Assert.That(page.Items, Has.All.Matches<VariableDefinition>(item => !item.IsBindable));
		Assert.That(page.ContinuationToken, Is.Null);
	}

	[Test]
	public async Task The_catalog_lists_the_sensors_under_the_hardware_that_exposes_them()
	{
		await using var fixture = new Fixture();
		var onBoard = fixture.Add("Motherboard", "3VSB", SensorType.Voltage);
		fixture.Add("Motherboard", "Fan #1", SensorType.Fan, subHardwareName: "ITE IT8688E");
		fixture.Add("CPU", "CPU Total", SensorType.Load);

		var roots = await fixture.Provider.DiscoverAsync(new VariableCatalogQuery());
		var motherboard = roots.Items.Single(item => item.Name == "motherboard");

		var page = await fixture.Provider.DiscoverAsync(new VariableCatalogQuery { ParentId = motherboard.Id });

		Assert.That(page.Items, Has.Count.EqualTo(2));
		Assert.That(
			page.Items.Select(item => item.Name),
			Is.EqualTo(new List<string> { "v_3vsb_voltage", "fan__1_fan" }));
		Assert.That(page.Items, Has.All.Matches<VariableDefinition>(item => item.ParentId == motherboard.Id));
		Assert.That(page.Items, Has.All.Matches<VariableDefinition>(item => item.IsBindable));

		// The id a binding is stored under is the sensor's library identifier, not its name: the name can
		// move, the identifier does not.
		Assert.That(page.Items.Select(item => item.ResolvedId), Contains.Item(onBoard.ResourceId));
	}

	[Test]
	public async Task A_parent_id_nothing_knows_about_offers_nothing()
	{
		await using var fixture = new Fixture();
		fixture.Add("CPU", "CPU Total", SensorType.Load);

		var page = await fixture.Provider.DiscoverAsync(new VariableCatalogQuery { ParentId = "/not/hardware" });

		Assert.That(page.Items, Is.Empty);
		Assert.That(page.ContinuationToken, Is.Null);
	}

	[Test]
	public async Task Searching_looks_across_the_whole_tree()
	{
		await using var fixture = new Fixture();
		fixture.Add("CPU", "CPU Core #1", SensorType.Temperature);
		fixture.Add("CPU", "CPU Total", SensorType.Load);
		fixture.Add("NVIDIA GeForce RTX 3080", "GPU Core", SensorType.Temperature);

		var page = await fixture.Provider.DiscoverAsync(new VariableCatalogQuery { Search = "core" });

		// Inside a folder or not, a search that finds one processor's cores and not the other's would be a
		// strange search.
		Assert.That(page.Items.Select(item => item.Name), Is.EqualTo(new List<string> { "cpu_core__1_temperature", "gpu_core_temperature" }));
		Assert.That(page.Items, Has.All.Matches<VariableDefinition>(item => !item.IsContainer));
	}

	[Test]
	public async Task Searching_matches_the_device_a_sensor_is_on()
	{
		await using var fixture = new Fixture();
		fixture.Add("NVIDIA GeForce RTX 3080", "GPU Core", SensorType.Temperature);
		fixture.Add("CPU", "CPU Total", SensorType.Load);

		var page = await fixture.Provider.DiscoverAsync(new VariableCatalogQuery { Search = "nvidia" });

		Assert.That(page.Items.Select(item => item.Name), Is.EqualTo(new List<string> { "gpu_core_temperature" }));
	}

	[Test]
	public async Task A_catalog_page_can_be_walked_with_the_token_it_hands_back()
	{
		await using var fixture = new Fixture();
		fixture.Add("CPU", "Core #1", SensorType.Temperature);
		fixture.Add("CPU", "Core #2", SensorType.Temperature);
		fixture.Add("CPU", "Core #3", SensorType.Temperature);

		var roots = await fixture.Provider.DiscoverAsync(new VariableCatalogQuery());
		var cpu = roots.Items.Single();

		var first = await fixture.Provider.DiscoverAsync(new VariableCatalogQuery { ParentId = cpu.Id, PageSize = 2 });
		Assert.That(first.Items, Has.Count.EqualTo(2));

		var second = await fixture.Provider.DiscoverAsync(new VariableCatalogQuery
		{
			ParentId = cpu.Id,
			PageSize = 2,
			ContinuationToken = first.ContinuationToken,
		});

		Assert.That(second.Items, Has.Count.EqualTo(1));
		Assert.That(second.ContinuationToken, Is.Null);
		Assert.That(
			first.Items.Concat(second.Items).Select(item => item.Name).Distinct().ToArray(),
			Has.Length.EqualTo(3));
	}

	[Test]
	public async Task A_catalog_page_is_capped_however_much_is_asked_for()
	{
		await using var fixture = new Fixture();
		for (var index = 0; index < 250; index++)
		{
			fixture.Add("CPU", $"Core {index}", SensorType.Temperature);
		}

		var roots = await fixture.Provider.DiscoverAsync(new VariableCatalogQuery());
		var cpu = roots.Items.Single();

		// A host is free to ask for everything at once; a catalog that answered would be a very large frame.
		var page = await fixture.Provider.DiscoverAsync(new VariableCatalogQuery { ParentId = cpu.Id, PageSize = 10_000 });

		Assert.That(page.Items, Has.Count.EqualTo(200));
		Assert.That(page.ContinuationToken, Is.Not.Null);
	}

	[Test]
	public async Task An_enumeration_that_fails_offers_an_empty_catalog()
	{
		await using var fixture = new Fixture();
		fixture.Add("CPU", "CPU Total", SensorType.Load, 42f);
		fixture.Source.OnRead = () => throw new InvalidOperationException("the driver refused to answer");

		var page = await fixture.Provider.DiscoverAsync(new VariableCatalogQuery());

		Assert.That(page.Items, Is.Empty);
		Assert.That(page.ContinuationToken, Is.Null);
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow + Patience;
		while (DateTime.UtcNow < deadline)
		{
			if (condition())
			{
				return;
			}

			await Task.Delay(10);
		}

		Assert.Fail($"The condition was still false after {Patience}.");
	}

	private static async Task WaitUntilAsync(Func<Task<bool>> condition)
	{
		var deadline = DateTime.UtcNow + Patience;
		while (DateTime.UtcNow < deadline)
		{
			if (await condition())
			{
				return;
			}

			await Task.Delay(10);
		}

		Assert.Fail($"The condition was still false after {Patience}.");
	}

	/// <summary>Fake hardware, a real polling loop and the provider wired to both.</summary>
	private sealed class Fixture : IAsyncDisposable
	{
		private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

		public Fixture()
		{
			Monitor = new HardwareMonitor(
				Source,
				new HardwareMonitorOptions
				{
					PollingInterval = TimeSpan.FromMilliseconds(10),
					StaleAfter = TimeSpan.FromMilliseconds(100),
				},
				_logger);

			Provider = new SensorVariableProvider(Monitor, Selection);
		}

		public FakeHardwareSensorSource Source { get; } = new();

		public SensorSelection Selection { get; } = new();

		public HardwareMonitor Monitor { get; }

		public SensorVariableProvider Provider { get; }

		public SensorIdentifier Add(
			string hardwareName,
			string sensorName,
			SensorType sensorType,
			float? value = null,
			string? subHardwareName = null)
		{
			var sensor = TestSensor.Create(hardwareName, sensorName, sensorType, subHardwareName);
			Source.Add(sensor, value);
			return sensor;
		}

		/// <summary>Adds a fan or pump with a software control, marked the way the live walk marks it.</summary>
		public SensorIdentifier AddControl(
			string sensorName = "Fan #1",
			float min = 0f,
			float max = 100f,
			float value = 0f)
		{
			var sensor = TestFanActions.Control(sensorName) with { Controllable = true };
			Source.AddControl(sensor, min, max, value);
			return sensor;
		}

		public void Select(params SensorIdentifier[] sensors) => Selection.Replace(sensors);

		/// <summary>Watches the selection, the way binding an eager variable does.</summary>
		public void Watch() => Monitor.SetWatchedSensors(Selection.Sensors);

		public void Start() => Monitor.Start();

		public async ValueTask DisposeAsync()
		{
			Provider.Dispose();
			await Monitor.DisposeAsync();

			// Not owning the logger would be tidier, but the monitor only takes an ILogger and there is
			// nothing else to hand a test one from.
			(_logger as IDisposable)?.Dispose();
		}
	}
}
