using System.Text.Json;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Migration;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using LibreHardwareMonitor.Hardware;
using MacroDeck.Plugin.Protocol.Capabilities.Migration;
using MacroDeck.Plugin.Testing;
using MacroDeck.Plugin.Testing.Fakes;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The migration as the host drives it: the <c>describe</c>/<c>migrate-configuration</c>/
/// <c>migrate-action</c> operations the WebSocket carries, invoked through the in-process harness
/// against the same integration the plugin registers.
/// </summary>
/// <remarks>
/// The responses are asserted as JSON rather than through the protocol DTOs on purpose: this is the wire
/// shape the host parses, and asserting it is what catches a migration that is wired to the wrong
/// capability, or that answers with nothing.
/// </remarks>
[TestFixture]
public sealed class MigrationProtocolTests
{
	private static readonly SensorIdentifier CpuTotal =
		TestSensor.Create("CPU", "CPU Total", SensorType.Load);

	[Test]
	public async Task The_host_is_told_which_macro_deck_2_settings_this_plugin_claims()
	{
		await using var harness = CreateHarness(new FakeHardwareSensorSource());
		await harness.InitializeIntegrationsAsync();

		// The capability is declared by implementing IMigrationProvider; nothing beyond the integration
		// itself has to be registered for it.
		var declared = harness.Declared.Single(capability => capability.Kind == "migration");
		Assert.That(declared.LocalId, Is.EqualTo("provider"));

		var described = Response(await harness.InvokeAsync("migration", declared.LocalId, "describe"));
		var migration = described.GetProperty("migrations")[0];

		Assert.That(migration.GetProperty("source").GetString(), Is.EqualTo("MacroDeck2"));
		Assert.That(migration.GetProperty("claimedActionSources").GetArrayLength(), Is.Zero);
		Assert.That(
			migration.GetProperty("claimedSettingsSources").EnumerateArray().Select(value => value.GetString()),
			Is.EqualTo(new[] { MacroDeck2Migration.SettingsSource }));
	}

	[Test]
	public async Task The_requested_sensors_come_back_as_one_readable_configuration_entry()
	{
		var source = new FakeHardwareSensorSource().Add(CpuTotal, 42f);
		await using var harness = CreateHarness(source);
		await harness.InitializeIntegrationsAsync();

		var request = new MigrateConfigurationRequestPayload
		{
			Source = "MacroDeck2",
			SettingsSource = MacroDeck2Migration.SettingsSource,
			Settings = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["requestedSensors"] =
					"""[{"Item1":"CPU","Item2":"Load","Item3":"CPU Total_Load"}]""",
				["pollingRate"] = "2500",
			},
			Credentials = [],
			Actions = [],
		};

		var configurations = Response(await harness.InvokeAsync("migration", "provider", "migrate-configuration", request))
			.GetProperty("configurations");

		Assert.That(configurations.GetArrayLength(), Is.EqualTo(1));

		var entry = configurations[0];
		Assert.That(entry.GetProperty("integrationId").GetString(), Is.EqualTo(MacroDeck2Migration.IntegrationId));
		Assert.That(entry.GetProperty("title").GetString(), Is.EqualTo(MacroDeck2Migration.ConfigurationTitle));
		Assert.That(entry.GetProperty("secrets").EnumerateObject(), Is.Empty);

		// The entry is what the host will later hand back, so it is read back the way the integration reads
		// it rather than inspected field by field.
		using var logger = new LoggerConfiguration().CreateLogger();
		var restored = await RestoreAsync(entry.GetProperty("values"), logger);

		Assert.That(restored.Sensors, Is.EqualTo(new[] { CpuTotal }));
		Assert.That(restored.PollingInterval, Is.EqualTo(TimeSpan.FromMilliseconds(2500)));
	}

	[Test]
	public async Task A_button_action_is_left_for_the_host_to_place()
	{
		await using var harness = CreateHarness(new FakeHardwareSensorSource());
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.InvokeAsync("migration", "provider", "migrate-action", new MigrateActionRequestPayload
		{
			Source = "MacroDeck2",
			TypeName = "Some.Plugin.DoThing",
			ActionSource = "some.other.plugin",
			DisplayName = "Do a thing",
			Configuration = "{}",
		});

		// The old plugin had no actions, so it translates none: every button belonging to another plugin is
		// left as the placeholder the host keeps for it.
		Assert.That(Response(outcome).GetProperty("translated").GetBoolean(), Is.False);
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

	/// <summary>Puts a configuration entry's values into a host configuration and reads it back.</summary>
	private static async Task<MigratedSensorSelection> RestoreAsync(JsonElement values, ILogger logger)
	{
		var config = new FakeIntegrationConfig();
		var entry = config.AddEntry(MacroDeck2Migration.ConfigurationTitle);

		foreach (var value in values.EnumerateObject())
		{
			config.SeedString(entry, value.Name, value.Value.GetString()!);
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
