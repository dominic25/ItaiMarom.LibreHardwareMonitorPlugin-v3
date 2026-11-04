using System.Net;
using System.Text.Json;
using ItaiMarom.LibreHardwareMonitorPlugin.Configuration;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Migration;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using LibreHardwareMonitor.Hardware;
using MacroDeck.Plugin.Protocol.Capabilities.ConfigFlow;
using MacroDeck.Plugin.Testing;
using MacroDeck.Plugin.Testing.Fakes;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The settings screen as the host drives it: the <c>start</c>/<c>submit</c> operations the WebSocket
/// carries, invoked through the in-process harness against the same integration the plugin registers.
/// </summary>
/// <remarks>
/// <para>
/// The responses are asserted as JSON for the same reason the migration's are: this is the wire shape the
/// host parses, so a flow wired to the wrong capability, or one that answers a step the host cannot render,
/// fails here rather than in front of the user.
/// </para>
/// <para>
/// The screen declares nothing the user fills in. What it declares is the step whose one button opens the
/// classic sensor window, so saving is a round trip through a real browser in production and one here
/// too: the flow answers <c>Continue</c> with the address of the window, the window is read and posted to
/// over loopback, and the flow is resumed on the key the host's callback would have carried. Nothing about
/// that path is stubbed except the browser.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ConfigFlowProtocolTests
{
	private const string Session = "session";

	[Test]
	public async Task The_settings_screen_declares_no_fields_of_its_own()
	{
		await using var harness = CreateHarness(new FakeHardwareSensorSource());
		await harness.InitializeIntegrationsAsync();

		var result = await OpenAsync(harness, Session);

		var step = result.GetProperty("nextStep");
		Assert.That(step.GetProperty("stepId").GetString(), Is.EqualTo(LibreHardwareMonitorConfigFlow.StepId));

		// No picker stands in front of the old dialog: a tree cannot be declared as a field, and a flat
		// form of grouped multi-selects is not the dialog the user is asking for. The step carries the
		// title, the line saying where the sensors are chosen, and the button that opens the window.
		Assert.That(step.GetProperty("fields").EnumerateArray(), Is.Empty);
	}

	[Test]
	public async Task Continuing_hands_the_browser_the_classic_window_opened_on_the_configured_sensors()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Total", SensorType.Load, 42f)
			.Add("GPU", "GPU Core", SensorType.Temperature, 60f);
		var cpu = source.Sensors[0];
		var gpu = source.Sensors[1];

		await using var harness = CreateHarness(source);

		// What the window opens on is the selection the integration already read at startup, which for a
		// taken-over Macro Deck 2 installation is what the migration left behind. Continuing is never a way
		// to lose a pick.
		MigrationTestData.SeedMigratedSensors(harness.Context.Config, cpu);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var window = await ContinueAsync(harness, Session);

		var page = await WindowClient.ReadPageAsync(window);

		Assert.That(page, Does.Contain($"value=\"{cpu.ResourceId}\" checked"));
		Assert.That(page, Does.Contain($"value=\"{gpu.ResourceId}\">"));
		Assert.That(page, Does.Contain("<span class=\"name root\">CPU</span>"));

		// The polling control opens where the loop is running, so pressing Save without touching it cannot
		// change the interval by accident.
		Assert.That(page, Does.Contain("value=\"1000\""));
	}

	[Test]
	public async Task Saving_the_window_stores_the_ticked_sensors_and_the_polling_rate()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Total", SensorType.Load, 42f)
			.Add("GPU", "GPU Core", SensorType.Temperature, 60f);
		var cpu = source.Sensors[0];
		var gpu = source.Sensors[1];

		await using var harness = CreateHarness(source);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var result = await SaveWindowAsync(harness, Session, [cpu.ResourceId, gpu.ResourceId], 2500);

		Assert.That(result.GetProperty("kind").GetString(), Is.EqualTo("Complete"));
		Assert.That(result.GetProperty("entryTitle").GetString(), Is.EqualTo("Libre Hardware Monitor"));

		// The entry the host would store is read back the way the integration reads it, so the two halves
		// of the round trip are tested against each other rather than against a hand-written document.
		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.Sensors, Is.EqualTo(new List<SensorIdentifier> { cpu, gpu }));
		Assert.That(restored.PollingInterval, Is.EqualTo(TimeSpan.FromMilliseconds(2500)));
	}

	[Test]
	public async Task Saving_the_window_stores_the_startup_elevation_choice()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		var cpu = source.Sensors[0];

		await using var harness = CreateHarness(source);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var result = await SaveWindowAsync(harness, Session, [cpu.ResourceId], 1000, autoElevate: true);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.AutoElevate, Is.True);
	}

	[Test]
	public async Task The_window_opens_on_the_stored_startup_elevation_choice()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		var cpu = source.Sensors[0];

		await using var harness = CreateHarness(source);
		var entry = MigrationTestData.SeedMigratedSensors(harness.Context.Config, cpu);
		MigrationTestData.SeedAutoElevate(harness.Context.Config, entry, autoElevate: true);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var page = await WindowClient.ReadPageAsync(await ContinueAsync(harness, Session));

		Assert.That(page, Does.Contain("name=\"auto_elevate\" value=\"1\" checked>"));
	}

	[Test]
	public async Task Unticking_the_startup_elevation_box_stores_an_explicit_no()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		var cpu = source.Sensors[0];

		await using var harness = CreateHarness(source);
		var entry = MigrationTestData.SeedMigratedSensors(harness.Context.Config, cpu);
		MigrationTestData.SeedAutoElevate(harness.Context.Config, entry, autoElevate: true);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		// The window opened with the box ticked; the user cleared it and saved. From here on the plugin
		// has a preference rather than an unset one, so the issue's button cannot turn it back on.
		var result = await SaveWindowAsync(harness, Session, [cpu.ResourceId], 1000);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.AutoElevate, Is.False);
	}

	[Test]
	public async Task What_the_window_was_ticked_with_is_what_is_stored()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Total", SensorType.Load, 42f)
			.Add("GPU", "GPU Core", SensorType.Temperature, 60f);
		var cpu = source.Sensors[0];
		var gpu = source.Sensors[1];

		await using var harness = CreateHarness(source);
		MigrationTestData.SeedMigratedSensors(harness.Context.Config, cpu);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		// The window is the screen the user is actually working in, so it is what counts: it opened with
		// the CPU ticked and the user swapped that for the GPU there.
		var result = await SaveWindowAsync(harness, Session, [gpu.ResourceId], 1000);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.Sensors, Is.EqualTo(new List<SensorIdentifier> { gpu }));
	}

	[Test]
	public async Task A_ticked_sensor_that_is_not_present_is_dropped()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		var cpu = source.Sensors[0];

		await using var harness = CreateHarness(source);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		// Ticked on a machine that had it, saved on a machine that does not: the selection has to keep the
		// rest rather than be lost whole.
		var removed = TestSensor.Create("GPU", "GPU Core", SensorType.Temperature);

		var result = await SaveWindowAsync(harness, Session, [cpu.ResourceId, removed.ResourceId], 1000);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.Sensors, Is.EqualTo(new List<SensorIdentifier> { cpu }));
	}

	[Test]
	public async Task Clearing_the_selection_declares_no_variables()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);

		await using var harness = CreateHarness(source);
		MigrationTestData.SeedMigratedSensors(harness.Context.Config, source.Sensors[0]);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		// Nothing ticked is the old dialog's "delete all variables": the window opens with the configured
		// sensor already ticked, and unticking it and pressing Save is how it goes away. The rate the user
		// set stays, because clearing the sensors is not clearing the window.
		var result = await SaveWindowAsync(harness, Session, [], 2000);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.Sensors, Is.Empty);
		Assert.That(restored.PollingInterval, Is.EqualTo(TimeSpan.FromMilliseconds(2000)));
	}

	[Test]
	public async Task The_interval_a_window_sends_is_clamped_into_the_plugin_s_own_bounds()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		var cpu = source.Sensors[0];

		await using var harness = CreateHarness(source);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		// The page bounds its own field, but what arrives is a form post and only the plugin can insist.
		var result = await SaveWindowAsync(
			harness,
			Session,
			[cpu.ResourceId],
			LibreHardwareMonitorConfigFlow.MaximumPollingMilliseconds * 10);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(
			restored.PollingInterval,
			Is.EqualTo(TimeSpan.FromMilliseconds(LibreHardwareMonitorConfigFlow.MaximumPollingMilliseconds)));
	}

	[Test]
	public async Task A_window_that_sends_no_interval_keeps_the_one_the_loop_runs_at()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		var cpu = source.Sensors[0];

		await using var harness = CreateHarness(source);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var result = await SaveWindowAsync(harness, Session, [cpu.ResourceId]);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.PollingInterval, Is.EqualTo(TimeSpan.FromMilliseconds(1000)));
	}

	[Test]
	public async Task Saving_the_window_stores_the_remote_machines()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		var cpu = source.Sensors[0];

		await using var harness = CreateHarness(source);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var result = await SaveWindowAsync(
			harness,
			Session,
			[cpu.ResourceId],
			1000,
			remotes: [("Living Room", "living-room-pc:8085", "", null, null)]);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.RemoteSources, Has.Count.EqualTo(1));
		Assert.That(restored.RemoteSources![0].Name, Is.EqualTo("Living Room"));
		Assert.That(restored.RemoteSources[0].Url, Is.EqualTo("http://living-room-pc:8085/data.json"));
		Assert.That(restored.RemoteSources[0].Key, Is.EqualTo("living_room"));
	}

	[Test]
	public async Task The_window_reopens_on_the_stored_remote_machines()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		var cpu = source.Sensors[0];
		var remote = new RemoteSourceDefinition(
			"Living Room",
			"http://living-room-pc:8085/data.json",
			"living_room");

		await using var harness = CreateHarness(source);
		var entry = MigrationTestData.SeedMigratedSensors(harness.Context.Config, cpu);
		MigrationTestData.SeedRemoteSources(harness.Context.Config, entry, remote);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var page = await WindowClient.ReadPageAsync(await ContinueAsync(harness, Session));

		Assert.That(page, Does.Contain("id=\"remotes\" open>"));
		Assert.That(page, Does.Contain("name=\"remote_name\""));
		Assert.That(page, Does.Contain("value=\"Living Room\""));
		Assert.That(page, Does.Contain("name=\"remote_key\" value=\"living_room\""));
	}

	[Test]
	public async Task Renaming_a_remote_machine_keeps_the_key_its_variables_use()
	{
		var remote = new RemoteSourceDefinition(
			"Living Room",
			"http://living-room-pc:8085/data.json",
			"living_room");

		await using var harness = CreateHarness(new FakeHardwareSensorSource());
		var entry = MigrationTestData.SeedMigratedSensors(harness.Context.Config);
		MigrationTestData.SeedRemoteSources(harness.Context.Config, entry, remote);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var result = await SaveWindowAsync(
			harness,
			Session,
			[],
			1000,
			remotes: [("The Living Room", "http://living-room-pc:8085/data.json", "living_room", null, null)]);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.RemoteSources![0].Name, Is.EqualTo("The Living Room"));
		Assert.That(restored.RemoteSources[0].Key, Is.EqualTo("living_room"));
	}

	[Test]
	public async Task A_remote_row_that_is_not_an_address_or_names_the_same_machine_is_dropped()
	{
		await using var harness = CreateHarness(new FakeHardwareSensorSource());
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var result = await SaveWindowAsync(
			harness,
			Session,
			[],
			1000,
			remotes:
			[
				("Bad", "file:///C:/sensors.json", "", null, null),
				("Living Room", "living-room-pc:8085", "", null, null),
				("Duplicate", "http://living-room-pc:8085/data.json", "", null, null),
			]);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.RemoteSources, Has.Count.EqualTo(1));
		Assert.That(restored.RemoteSources![0].Name, Is.EqualTo("Living Room"));
	}

	[Test]
	public async Task Removing_the_last_remote_machine_stores_an_empty_list()
	{
		var remote = new RemoteSourceDefinition(
			"Living Room",
			"http://living-room-pc:8085/data.json",
			"living_room");

		await using var harness = CreateHarness(new FakeHardwareSensorSource());
		var entry = MigrationTestData.SeedMigratedSensors(harness.Context.Config);
		MigrationTestData.SeedRemoteSources(harness.Context.Config, entry, remote);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var result = await SaveWindowAsync(harness, Session, [], 1000);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.RemoteSources, Is.Empty);
	}

	[Test]
	public async Task Saving_the_window_stores_the_unit_each_sensor_was_chosen_in()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Core #1", SensorType.Temperature, 50f);
		var cpu = source.Sensors[0];

		await using var harness = CreateHarness(source);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var result = await SaveWindowAsync(
			harness,
			Session,
			[cpu.ResourceId],
			1000,
			units: [(cpu.ResourceId, SensorUnit.Fahrenheit)]);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.Sensors.Single().DisplayUnit, Is.EqualTo(SensorUnit.Fahrenheit));
	}

	[Test]
	public async Task The_window_reopens_on_the_stored_unit()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Core #1", SensorType.Temperature, 50f);
		var cpu = source.Sensors[0] with { DisplayUnit = SensorUnit.Fahrenheit };

		await using var harness = CreateHarness(source);
		MigrationTestData.SeedMigratedSensors(harness.Context.Config, cpu);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var page = await WindowClient.ReadPageAsync(await ContinueAsync(harness, Session));

		// Pressing Save without touching the selector has to keep the unit, so what opens must already
		// be the stored choice - which also means the default option must not claim it.
		var selector = WindowClient.UnitSelectorOf(page, cpu);
		Assert.That(selector, Does.Contain("<option value=\"Fahrenheit\" selected>"));
		Assert.That(selector, Does.Not.Contain("<option value=\"Celsius\" selected>"));
		Assert.That(selector, Does.Not.Contain("<option value=\"\" selected>"));
	}

	[Test]
	public async Task A_unit_that_does_not_suit_the_sensor_is_not_stored()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Total", SensorType.Load, 42f);
		var cpu = source.Sensors[0];

		await using var harness = CreateHarness(source);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		// A stale or hand-edited form can carry any unit next to any sensor. A load is a percentage,
		// not a size, so the choice is dropped rather than making the variable nonsense.
		var result = await SaveWindowAsync(
			harness,
			Session,
			[cpu.ResourceId],
			1000,
			units: [(cpu.ResourceId, SensorUnit.Gigabytes)]);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.Sensors.Single().DisplayUnit, Is.Null);
	}

	[Test]
	public async Task A_sensor_changed_back_to_default_clears_its_explicit_unit()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Core #1", SensorType.Temperature, 50f);
		var cpu = source.Sensors[0];

		await using var harness = CreateHarness(source);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		// First save: set the sensor explicitly to Fahrenheit
		var result = await SaveWindowAsync(
			harness,
			Session,
			[cpu.ResourceId],
			1000,
			units: [(cpu.ResourceId, SensorUnit.Fahrenheit)]);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.Sensors.Single().DisplayUnit, Is.EqualTo(SensorUnit.Fahrenheit));

		// Reopen and change the sensor back to Default
		await OpenAsync(harness, Session);

		// Post with empty unit_value for this sensor (user chose Default)
		result = await SaveWindowAsync(
			harness,
			Session,
			[cpu.ResourceId],
			1000,
			units: [(cpu.ResourceId, (SensorUnit?)null)]);

		restored = await RestoreAsync(result.GetProperty("values"), logger);

		// The explicit unit should be cleared, so the sensor follows the default (Celsius)
		Assert.That(restored.Sensors.Single().DisplayUnit, Is.Null);
	}

	[Test]
	public async Task Saving_the_window_stores_the_default_units()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Core #1", SensorType.Temperature, 50f);
		var cpu = source.Sensors[0];

		await using var harness = CreateHarness(source);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var result = await SaveWindowAsync(
			harness,
			Session,
			[cpu.ResourceId],
			1000,
			defaults:
			[
				(SensorType.Temperature, SensorUnit.Fahrenheit),
				(SensorType.Data, SensorUnit.Gibibytes),
			]);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.UnitDefaults, Is.Not.Null);
		Assert.That(restored.UnitDefaults![SensorType.Temperature], Is.EqualTo(SensorUnit.Fahrenheit));
		Assert.That(restored.UnitDefaults[SensorType.Data], Is.EqualTo(SensorUnit.Gibibytes));

		// What the window posted was partial; what is stored is complete, so every kind that offers a
		// choice has an answer on the next start.
		Assert.That(restored.UnitDefaults, Has.Count.EqualTo(SensorUnits.SupportedKinds.Count));
		Assert.That(restored.UnitDefaults[SensorType.SmallData], Is.EqualTo(SensorUnit.Megabytes));
	}

	[Test]
	public async Task The_window_reopens_on_the_stored_default_units()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Core #1", SensorType.Temperature, 50f);
		var cpu = source.Sensors[0];

		await using var harness = CreateHarness(source);
		var entry = MigrationTestData.SeedMigratedSensors(harness.Context.Config, cpu);
		MigrationTestData.SeedUnitDefaults(
			harness.Context.Config,
			entry,
			new Dictionary<SensorType, SensorUnit>
			{
				[SensorType.Temperature] = SensorUnit.Fahrenheit,
			});
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var page = await WindowClient.ReadPageAsync(await ContinueAsync(harness, Session));

		var selector = WindowClient.DefaultSelectorOf(page, SensorType.Temperature);
		Assert.That(selector, Does.Contain("<option value=\"Fahrenheit\" selected>"));
		Assert.That(selector, Does.Not.Contain("<option value=\"Celsius\" selected>"));

		// A sensor that never made a choice follows that default, and its selector says so instead of
		// pretending the current default is an explicit choice.
		var sensorSelector = WindowClient.UnitSelectorOf(page, cpu);
		Assert.That(
			sensorSelector,
			Does.Contain($"<option value=\"\" selected>Default ({WebUtility.HtmlEncode("°F")})</option>"));
		Assert.That(page, Does.Contain("122 " + WebUtility.HtmlEncode("°F")));
	}

	[Test]
	public async Task A_sensor_left_on_default_is_not_frozen_to_it()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Core #1", SensorType.Temperature, 50f);
		var cpu = source.Sensors[0];

		await using var harness = CreateHarness(source);
		var entry = MigrationTestData.SeedMigratedSensors(harness.Context.Config, cpu);
		MigrationTestData.SeedUnitDefaults(
			harness.Context.Config,
			entry,
			new Dictionary<SensorType, SensorUnit>
			{
				[SensorType.Temperature] = SensorUnit.Fahrenheit,
			});
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		// The page posts the empty option for a sensor that follows the default, and a window that
		// predates defaults posts nothing at all. Either way the sensor must keep following.
		var result = await SaveWindowAsync(harness, Session, [cpu.ResourceId], 1000);

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.Sensors.Single().DisplayUnit, Is.Null);
		Assert.That(restored.UnitDefaults, Is.Not.Null);
		Assert.That(restored.UnitDefaults![SensorType.Temperature], Is.EqualTo(SensorUnit.Fahrenheit));
	}

	[Test]
	public async Task A_window_that_was_never_saved_stores_nothing()
	{
		await using var harness = CreateHarness(new FakeHardwareSensorSource());
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		// A callback for a window this plugin is not holding - a stale tab, or a key someone guessed -
		// has to come back as an error rather than as "nothing was ticked", which would quietly wipe the
		// configuration.
		var result = await ResumeAsync(harness, Session, $"https://localhost/callback?state=state&code={new string('0', 32)}");

		Assert.That(result.GetProperty("kind").GetString(), Is.EqualTo("Error"));
		Assert.That(result.TryGetProperty("values", out _), Is.False);
	}

	[Test]
	public async Task Submitting_a_step_that_is_not_the_screen_is_an_error()
	{
		await using var harness = CreateHarness(new FakeHardwareSensorSource());
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, Session);

		var result = Response(await harness.ConfigFlow.SubmitAsync(new FlowSubmitArguments
		{
			SessionId = Session,
			OAuth = HostOAuth(),
			StepId = "not-the-screen",
			Input = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
		}));

		Assert.That(result.GetProperty("kind").GetString(), Is.EqualTo("Error"));
	}

	/// <summary>The OAuth context the host always sends, carrying the code once its callback has fired.</summary>
	private static ConfigFlowOAuthContextDto HostOAuth(string? authorizationCode = null) => new()
	{
		RedirectUri = "https://localhost/callback",
		State = "state",
		AuthorizationCode = authorizationCode,
	};

	/// <summary>
	/// Shows the screen, the way a host does before the user can press its button - a submit without a live
	/// session is refused, exactly as the real host would refuse it.
	/// </summary>
	private static async Task<JsonElement> OpenAsync(PluginTestHarness harness, string sessionId)
	{
		var result = Response(await harness.ConfigFlow.StartAsync(new FlowStartArguments
		{
			SessionId = sessionId,
			OAuth = HostOAuth(),
		}));

		Assert.That(result.GetProperty("kind").GetString(), Is.EqualTo("Step"));

		return result;
	}

	/// <summary>
	/// Continue, which is what opens the classic window, and what a host that never opens one would get
	/// instead of a saved configuration.
	/// </summary>
	private static async Task<Uri> ContinueAsync(PluginTestHarness harness, string sessionId)
	{
		var result = Response(await harness.ConfigFlow.SubmitAsync(new FlowSubmitArguments
		{
			SessionId = sessionId,
			OAuth = HostOAuth(),
			StepId = LibreHardwareMonitorConfigFlow.StepId,
			Input = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
		}));

		Assert.That(result.GetProperty("kind").GetString(), Is.EqualTo("External"));
		Assert.That(
			result.GetProperty("resumeStepId").GetString(),
			Is.EqualTo(LibreHardwareMonitorConfigFlow.ResumeStepId));

		// The window is reached over loopback, because the desktop shell only hands http and https to the
		// system browser and refuses everything else without a word.
		var window = new Uri(result.GetProperty("externalUrl").GetString()!);
		Assert.That(window.IsLoopback, Is.True, $"The window was served from {window}.");

		return window;
	}

	/// <summary>The host resuming the flow on the code its OAuth callback carried.</summary>
	private static async Task<JsonElement> ResumeAsync(
		PluginTestHarness harness,
		string sessionId,
		string callbackUri) =>
		Response(await harness.ConfigFlow.SubmitAsync(new FlowSubmitArguments
		{
			SessionId = sessionId,

			// The dialog resubmits an external step with nothing in it, so everything the flow has to go
			// on is the context.
			OAuth = HostOAuth(WindowClient.CodeOf(callbackUri)),
			StepId = LibreHardwareMonitorConfigFlow.ResumeStepId,
			Input = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
		}));

	/// <summary>The whole trip a user makes: open the window, tick in it, save it.</summary>
	private static async Task<JsonElement> SaveWindowAsync(
		PluginTestHarness harness,
		string sessionId,
		IReadOnlyCollection<string> ticked,
		double? polling = null,
		bool autoElevate = false,
		IEnumerable<(string? Name, string? Url, string? Key, string? UserName, string? Password)>? remotes = null,
		IEnumerable<(string Sensor, SensorUnit? Unit)>? units = null,
		IEnumerable<(SensorType Kind, SensorUnit Unit)>? defaults = null)
	{
		var window = await ContinueAsync(harness, sessionId);
		var callback = await WindowClient.SaveAsync(window, ticked, polling, autoElevate, remotes, units, defaults);

		return await ResumeAsync(harness, sessionId, callback);
	}

	private static PluginTestHarness CreateHarness(FakeHardwareSensorSource source) =>
		PluginTestHarness.Create(builder => builder
			.ConfigureServices((_, services) =>
			{
				services.AddSingleton<IHardwareSensorSource>(source);
				services.AddSingleton<SensorSelection>();
				services.AddSingleton<IAccessDiagnosis>(FakeAccessDiagnosis.Reachable());
			})
			.RegisterIntegration<LibreHardwareMonitorIntegration>());

	/// <summary>Puts the values a flow completed with into a host configuration and reads it back.</summary>
	private static async Task<MigratedSensorSelection> RestoreAsync(JsonElement values, ILogger logger)
	{
		var config = new FakeIntegrationConfig();
		var entry = config.AddEntry("Libre Hardware Monitor");

		foreach (var value in values.EnumerateObject())
		{
			config.SeedString(entry, value.Name, value.Value.GetProperty("value").GetString()!);
		}

		return await MigratedSensorSelection.ReadAsync(config, logger);
	}

	/// <summary>The JSON the host received, which every operation here is expected to have answered.</summary>
	private static JsonElement Response(CapabilityInvocationOutcome outcome)
	{
		Assert.That(outcome.Succeeded, Is.True, outcome.Error?.Message);
		Assert.That(outcome.Data, Is.Not.Null);

		return outcome.Data!.Value;
	}
}
