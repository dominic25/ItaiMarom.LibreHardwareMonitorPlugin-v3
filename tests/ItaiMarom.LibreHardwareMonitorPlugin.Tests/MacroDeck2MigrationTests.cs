using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Migration;
using LibreHardwareMonitor.Hardware;
using MacroDeck.Plugin.Testing.Fakes;
using MacroDeck.Sdk.Migration;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// What taking over a Macro Deck 2 installation of the old plugin does: the requested sensors become the
/// sensors the plugin declares, through one configuration entry the integration reads back at startup.
/// </summary>
[TestFixture]
public sealed class MacroDeck2MigrationTests
{
	private static readonly SensorIdentifier CpuTotal =
		TestSensor.Create("CPU", "CPU Total", SensorType.Load);

	private static readonly HardwareMonitorOptions FastOptions = new()
	{
		PollingInterval = TimeSpan.FromMilliseconds(10),
		StaleAfter = TimeSpan.FromMilliseconds(100),
	};

	[Test]
	public async Task The_requested_sensors_become_one_configuration_entry()
	{
		var source = new FakeHardwareSensorSource().Add(CpuTotal, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);
		var migration = new MacroDeck2Migration(monitor, logger);

		var configurations = await migration.MigrateConfigurationAsync(Settings(
			("requestedSensors", """[{"Item1":"CPU","Item2":"Load","Item3":"CPU Total_Load"}]"""),
			("pollingRate", "2500")));

		Assert.That(configurations, Has.Count.EqualTo(1));
		Assert.That(configurations[0].IntegrationId, Is.EqualTo(MacroDeck2Migration.IntegrationId));
		Assert.That(configurations[0].Title, Is.EqualTo(MacroDeck2Migration.ConfigurationTitle));
		Assert.That(configurations[0].Secrets, Is.Empty);

		var restored = await RestoreAsync(configurations[0], logger);

		Assert.That(restored.Sensors, Is.EqualTo(new[] { CpuTotal }));
		Assert.That(restored.PollingInterval, Is.EqualTo(TimeSpan.FromMilliseconds(2500)));
	}

	[Test]
	public async Task The_setting_key_is_matched_without_regard_to_case()
	{
		// The file was written by another application; the exact casing of a key is not worth betting the
		// migration on.
		var source = new FakeHardwareSensorSource().Add(CpuTotal, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);
		var migration = new MacroDeck2Migration(monitor, logger);

		var configurations = await migration.MigrateConfigurationAsync(Settings(
			("RequestedSensors", """[{"Item1":"CPU","Item2":"Load","Item3":"CPU Total_Load"}]""")));

		Assert.That(configurations, Has.Count.EqualTo(1));
		Assert.That((await RestoreAsync(configurations[0], logger)).Sensors, Is.EqualTo(new[] { CpuTotal }));
	}

	[Test]
	public async Task A_selection_the_machine_no_longer_has_migrates_nothing()
	{
		var source = new FakeHardwareSensorSource().Add(CpuTotal, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);
		var migration = new MacroDeck2Migration(monitor, logger);

		var configurations = await migration.MigrateConfigurationAsync(Settings(
			("requestedSensors", """[{"Item1":"GPU","Item2":"Temperature","Item3":"GPU Core_Temperature"}]""")));

		Assert.That(configurations, Is.Empty);
	}

	[Test]
	public async Task Nothing_requested_is_not_a_reason_to_probe_the_hardware()
	{
		var source = new FakeHardwareSensorSource().Add(CpuTotal, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);
		var migration = new MacroDeck2Migration(monitor, logger);

		Assert.That(await migration.MigrateConfigurationAsync(Settings()), Is.Empty);
		Assert.That(await migration.MigrateConfigurationAsync(Settings(("requestedSensors", " "))), Is.Empty);
		Assert.That(source.OpenCount, Is.Zero);
	}

	[Test]
	public async Task A_polling_rate_that_makes_no_sense_is_not_stored()
	{
		var source = new FakeHardwareSensorSource().Add(CpuTotal, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);
		var migration = new MacroDeck2Migration(monitor, logger);

		foreach (var rate in new[] { "0", "-5", "not a number" })
		{
			var configurations = await migration.MigrateConfigurationAsync(Settings(
				("requestedSensors", """[{"Item1":"CPU","Item2":"Load","Item3":"CPU Total_Load"}]"""),
				("pollingRate", rate)));

			Assert.That(configurations, Has.Count.EqualTo(1));
			Assert.That(configurations[0].Values.ContainsKey(MigratedSensorSelection.PollingRateKey), Is.False);
			Assert.That((await RestoreAsync(configurations[0], logger)).PollingInterval, Is.Null);
		}
	}

	[Test]
	public async Task The_migration_claims_the_settings_but_no_actions()
	{
		var source = new FakeHardwareSensorSource().Add(CpuTotal, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);
		var migration = new MacroDeck2Migration(monitor, logger);

		Assert.That(migration.Source, Is.EqualTo(MigrationSource.MacroDeck2));
		Assert.That(migration.ClaimedActionSources, Is.Empty);
		Assert.That(migration.ClaimedSettingsSources, Is.EqualTo(new[] { MacroDeck2Migration.SettingsSource }));

		// The old plugin had no actions, so there is no action of its own to translate, and a button it
		// never owned is not this migration's to rewrite.
		var action = new ForeignAction("type", "some.other.plugin", "Button", "{}", "summary");
		Assert.That(await migration.MigrateActionAsync(action), Is.Null);
	}

	private static ForeignPluginSettings Settings(params (string Key, string Value)[] settings) =>
		new(
			MacroDeck2Migration.SettingsSource,
			settings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
			[],
			[]);

	/// <summary>Reads a migration's output back the way the integration does at startup.</summary>
	private static async Task<MigratedSensorSelection> RestoreAsync(MigratedConfiguration configuration, ILogger logger)
	{
		var config = new FakeIntegrationConfig();
		var entry = config.AddEntry(configuration.Title);
		foreach (var pair in configuration.Values)
		{
			config.SeedString(entry, pair.Key, pair.Value.GetString()!);
		}

		return await MigratedSensorSelection.ReadAsync(config, logger);
	}
}
