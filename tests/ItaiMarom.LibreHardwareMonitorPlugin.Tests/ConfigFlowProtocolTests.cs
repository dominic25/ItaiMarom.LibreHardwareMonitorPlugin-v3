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
/// The responses are asserted as JSON for the same reason the migration's are: this is the wire shape the
/// host parses, so a flow wired to the wrong capability, or one whose multi-select does not carry its
/// options or its default, fails here rather than in front of the user.
/// </remarks>
[TestFixture]
public sealed class ConfigFlowProtocolTests
{
	[Test]
	public async Task The_settings_screen_offers_every_sensor_and_preselects_the_configured_ones()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Total", SensorType.Load, 42f)
			.Add("GPU", "GPU Core", SensorType.Temperature, 60f);
		var cpu = source.Sensors[0];
		var gpu = source.Sensors[1];

		await using var harness = CreateHarness(source);

		// The preselection comes from the selection the integration already read at startup, which for a
		// taken-over Macro Deck 2 installation is what the migration left behind.
		MigrationTestData.SeedMigratedSensors(harness.Context.Config, cpu);
		await harness.InitializeIntegrationsAsync();

		var result = await OpenAsync(harness, "session");

		var step = result.GetProperty("nextStep");
		Assert.That(step.GetProperty("stepId").GetString(), Is.EqualTo(LibreHardwareMonitorConfigFlow.StepId));

		var fields = step.GetProperty("fields").EnumerateArray().ToArray();
		Assert.That(
			fields.Select(field => field.GetProperty("name").GetString()),
			Is.EqualTo(new List<string>
			{
				LibreHardwareMonitorConfigFlow.SensorsFieldName,
				LibreHardwareMonitorConfigFlow.PollingFieldName,
			}));

		// The tree the old dialog showed, flattened: one option per sensor, labelled with its hardware, its
		// kind and its name.
		var sensors = fields[0];
		Assert.That(sensors.GetProperty("type").GetString(), Is.EqualTo("MultiSelect"));

		var options = sensors.GetProperty("options").EnumerateArray().ToArray();
		Assert.That(
			options.Select(option => option.GetProperty("value").GetString()),
			Is.EquivalentTo(new List<string> { cpu.ResourceId, gpu.ResourceId }));
		Assert.That(
			options.Select(option => option.GetProperty("label").GetString()),
			Is.EquivalentTo(new List<string> { "CPU › Load › CPU Total", "GPU › Temperature › GPU Core" }));

		Assert.That(
			sensors.GetProperty("defaultValue").EnumerateArray().Select(value => value.GetString()),
			Is.EqualTo(new List<string> { cpu.ResourceId }));

		// The old slider, as a bounded number field defaulting to the 1000 ms the old plugin hardcoded.
		var polling = fields[1];
		Assert.That(polling.GetProperty("type").GetString(), Is.EqualTo("Number"));
		Assert.That(polling.GetProperty("min").GetDouble(), Is.EqualTo(LibreHardwareMonitorConfigFlow.MinimumPollingMilliseconds));
		Assert.That(polling.GetProperty("max").GetDouble(), Is.EqualTo(LibreHardwareMonitorConfigFlow.MaximumPollingMilliseconds));
		Assert.That(polling.GetProperty("step").GetDouble(), Is.EqualTo(1));
		Assert.That(polling.GetProperty("defaultValue").GetDouble(), Is.EqualTo(1000));
	}

	[Test]
	public async Task Saving_the_screen_stores_the_picked_sensors_and_the_polling_rate()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Total", SensorType.Load, 42f)
			.Add("GPU", "GPU Core", SensorType.Temperature, 60f);
		var cpu = source.Sensors[0];
		var gpu = source.Sensors[1];

		await using var harness = CreateHarness(source);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, "session");

		var result = Response(await harness.ConfigFlow.SubmitAsync(new FlowSubmitArguments
		{
			SessionId = "session",
			OAuth = HostOAuth(),
			StepId = LibreHardwareMonitorConfigFlow.StepId,
			Input = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[LibreHardwareMonitorConfigFlow.SensorsFieldName] =
					JsonSerializer.SerializeToElement(new List<string> { cpu.ResourceId, gpu.ResourceId }),
				[LibreHardwareMonitorConfigFlow.PollingFieldName] = JsonSerializer.SerializeToElement(2500),
			},
		}));

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
	public async Task A_picked_sensor_that_is_not_present_is_dropped()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		var cpu = source.Sensors[0];

		await using var harness = CreateHarness(source);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, "session");

		// Picked on a machine that had one, saved on a machine that does not: the selection has to keep the
		// rest rather than be lost whole.
		var removed = TestSensor.Create("GPU", "GPU Core", SensorType.Temperature);

		var result = Response(await harness.ConfigFlow.SubmitAsync(new FlowSubmitArguments
		{
			SessionId = "session",
			OAuth = HostOAuth(),
			StepId = LibreHardwareMonitorConfigFlow.StepId,
			Input = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[LibreHardwareMonitorConfigFlow.SensorsFieldName] =
					JsonSerializer.SerializeToElement(new List<string> { cpu.ResourceId, removed.ResourceId }),
			},
		}));

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		Assert.That(restored.Sensors, Is.EqualTo(new List<SensorIdentifier> { cpu }));
		Assert.That(restored.PollingInterval, Is.Null);
	}

	[Test]
	public async Task Clearing_the_selection_declares_no_variables()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);

		await using var harness = CreateHarness(source);
		MigrationTestData.SeedMigratedSensors(harness.Context.Config, source.Sensors[0]);
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, "session");

		var result = Response(await harness.ConfigFlow.SubmitAsync(new FlowSubmitArguments
		{
			SessionId = "session",
			OAuth = HostOAuth(),
			StepId = LibreHardwareMonitorConfigFlow.StepId,
			Input = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[LibreHardwareMonitorConfigFlow.SensorsFieldName] =
					JsonSerializer.SerializeToElement(Array.Empty<string>()),
			},
		}));

		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(result.GetProperty("values"), logger);

		// Deselecting everything is the old dialog's "delete all variables" button: nothing stays declared.
		Assert.That(restored.Sensors, Is.Empty);
	}

	[Test]
	public async Task Submitting_a_step_that_is_not_the_screen_is_an_error()
	{
		await using var harness = CreateHarness(new FakeHardwareSensorSource());
		await harness.InitializeIntegrationsAsync();
		await OpenAsync(harness, "session");

		var result = Response(await harness.ConfigFlow.SubmitAsync(new FlowSubmitArguments
		{
			SessionId = "session",
			OAuth = HostOAuth(),
			StepId = "not-the-screen",
			Input = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
		}));

		Assert.That(result.GetProperty("kind").GetString(), Is.EqualTo("Error"));
	}

	/// <summary>The OAuth context the host always sends; the flow ignores it, but it is required.</summary>
	private static ConfigFlowOAuthContextDto HostOAuth() => new()
	{
		RedirectUri = "https://localhost/callback",
		State = "state",
	};

	/// <summary>
	/// Shows the screen, the way a host does before the user can save it - a submit without a live session
	/// is refused, exactly as the real host would refuse it.
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

	private static PluginTestHarness CreateHarness(FakeHardwareSensorSource source) =>
		PluginTestHarness.Create(builder => builder
			.ConfigureServices((_, services) =>
			{
				services.AddSingleton<IHardwareSensorSource>(source);
				services.AddSingleton<SensorSelection>();
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
