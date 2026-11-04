using System.Text.Json.Nodes;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Migration;
using LibreHardwareMonitor.Hardware;
using MacroDeck.Plugin.Testing.Fakes;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// Reading the migrated selection back out of the host configuration. This runs at every startup, so it
/// never throws: a configuration that cannot be read costs the user their migrated variables, not the
/// plugin.
/// </summary>
[TestFixture]
public sealed class MigratedSensorSelectionTests
{
	private static readonly SensorIdentifier CpuTotal =
		TestSensor.Create("CPU", "CPU Total", SensorType.Load);

	private static readonly SensorIdentifier GpuCore =
		TestSensor.Create("GPU", "GPU Core", SensorType.Temperature);

	[Test]
	public async Task The_stored_sensors_come_back_whole()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		MigrationTestData.SeedMigratedSensors(config, CpuTotal, GpuCore);

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.Sensors, Is.EqualTo(new[] { CpuTotal, GpuCore }));
	}

	[Test]
	public async Task The_same_sensor_in_two_entries_is_read_once()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		MigrationTestData.SeedMigratedSensors(config, CpuTotal);
		MigrationTestData.SeedMigratedSensors(config, CpuTotal);

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.Sensors, Is.EqualTo(new[] { CpuTotal }));
	}

	[Test]
	public async Task A_controllable_sensor_is_still_controllable_after_a_round_trip()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		MigrationTestData.SeedMigratedSensors(config, CpuTotal with { Controllable = true });

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		// The host asks what variables exist before anything is enumerated, so a fan that should be
		// writable has to say so from the stored document alone.
		Assert.That(restored.Sensors.Single().Controllable, Is.True);
	}

	[Test]
	public async Task A_document_from_before_control_was_recorded_reads_as_read_only()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var entry = MigrationTestData.SeedMigratedSensors(config, CpuTotal);

		var stored = new MigratedSensorSelection([CpuTotal], null).ToValues()[MigratedSensorSelection.SensorsKey].GetString()!;
		var document = JsonNode.Parse(stored)!.AsArray();
		document[0]!.AsObject().Remove(nameof(MigratedSensor.Controllable));
		config.SeedString(entry, MigratedSensorSelection.SensorsKey, document.ToJsonString());

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.Sensors.Single().Controllable, Is.False);
	}

	[Test]
	public async Task The_chosen_display_unit_survives_a_round_trip()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var fahrenheit = GpuCore with { DisplayUnit = SensorUnit.Fahrenheit };
		MigrationTestData.SeedMigratedSensors(config, fahrenheit);

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.Sensors.Single().DisplayUnit, Is.EqualTo(SensorUnit.Fahrenheit));
	}

	[Test]
	public async Task A_stored_unit_that_does_not_suit_the_sensor_is_dropped()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var entry = MigrationTestData.SeedMigratedSensors(config, CpuTotal);

		// CpuTotal is a load, so a size is not a unit it can be shown in. A hand-edited document, or one
		// written by a version that knew more choices, must not make the plugin convert a percentage as
		// though it were bytes.
		var stored = new MigratedSensorSelection([CpuTotal], null)
			.ToValues()[MigratedSensorSelection.SensorsKey]
			.GetString()!;
		var document = JsonNode.Parse(stored)!.AsArray();
		document[0]!.AsObject()[nameof(MigratedSensor.DisplayUnit)] = "Gigabytes";
		config.SeedString(entry, MigratedSensorSelection.SensorsKey, document.ToJsonString());

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.Sensors.Single().DisplayUnit, Is.Null);
	}

	[Test]
	public async Task The_polling_interval_is_read_back()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var entry = MigrationTestData.SeedMigratedSensors(config, CpuTotal);
		config.SeedString(entry, MigratedSensorSelection.PollingRateKey, "750");

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.PollingInterval, Is.EqualTo(TimeSpan.FromMilliseconds(750)));
	}

	[Test]
	public async Task A_document_that_is_not_json_is_dropped_rather_than_thrown_over()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var entry = config.AddEntry("Macro Deck 2 migration");
		config.SeedString(entry, MigratedSensorSelection.SensorsKey, "{not json");

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.Sensors, Is.Empty);
	}

	[Test]
	public async Task A_record_that_is_not_complete_is_dropped_rather_than_defaulted()
	{
		// The selection is deserialized from text the host has held across upgrades: a document missing a
		// field must be rejected, not turned into a sensor with invented values.
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var entry = config.AddEntry("Macro Deck 2 migration");
		config.SeedString(entry, MigratedSensorSelection.SensorsKey, """[{"Sensor":"/cpu/load/cpu_total"}]""");

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.Sensors, Is.Empty);
	}

	[Test]
	public async Task An_explicit_yes_to_startup_elevation_is_read_back()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var entry = MigrationTestData.SeedMigratedSensors(config, CpuTotal);
		MigrationTestData.SeedAutoElevate(config, entry, autoElevate: true);

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.AutoElevate, Is.True);
	}

	[Test]
	public async Task An_explicit_no_to_startup_elevation_is_read_back_as_false()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var entry = MigrationTestData.SeedMigratedSensors(config, CpuTotal);
		MigrationTestData.SeedAutoElevate(config, entry, autoElevate: false);

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		// False is not the same as unset: the user answered the question, and the issue's button must
		// not turn it back on.
		Assert.That(restored.AutoElevate, Is.False);
	}

	[Test]
	public async Task A_value_that_is_not_a_boolean_leaves_the_startup_choice_unset()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var entry = MigrationTestData.SeedMigratedSensors(config, CpuTotal);
		config.SeedString(entry, MigratedSensorSelection.AutoElevateKey, "sometimes");

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.AutoElevate, Is.Null);
	}

	[Test]
	public void An_explicit_startup_elevation_choice_is_stored_as_text()
	{
		Assert.That(
			new MigratedSensorSelection([CpuTotal], null, AutoElevate: true)
				.ToValues()[MigratedSensorSelection.AutoElevateKey]
				.GetString(),
			Is.EqualTo("true"));
		Assert.That(
			new MigratedSensorSelection([CpuTotal], null, AutoElevate: false)
				.ToValues()[MigratedSensorSelection.AutoElevateKey]
				.GetString(),
			Is.EqualTo("false"));
	}

	[Test]
	public async Task The_remote_machines_come_back_whole()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var entry = MigrationTestData.SeedMigratedSensors(config, CpuTotal);
		var remote = new RemoteSourceDefinition(
			"Living Room",
			"http://living-room-pc:8085/data.json",
			"living_room");

		MigrationTestData.SeedRemoteSources(config, entry, remote);

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.RemoteSources, Is.EqualTo(new[] { remote }));
	}

	[Test]
	public async Task A_document_without_remote_machines_reads_as_an_empty_list()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		MigrationTestData.SeedMigratedSensors(config, CpuTotal);

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.RemoteSources, Is.Empty);
	}

	[Test]
	public async Task The_same_remote_machine_in_two_entries_is_read_once()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var first = MigrationTestData.SeedMigratedSensors(config, CpuTotal);
		var second = MigrationTestData.SeedMigratedSensors(config, CpuTotal);

		MigrationTestData.SeedRemoteSources(
			config,
			first,
			new RemoteSourceDefinition("Living Room", "living-room-pc:8085", "living_room"));
		MigrationTestData.SeedRemoteSources(
			config,
			second,
			new RemoteSourceDefinition("Living Room", "http://living-room-pc:8085/data.json", "living_room"));

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.RemoteSources, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task A_remote_machine_with_an_unusable_address_is_dropped()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var entry = config.AddEntry("Macro Deck 2 migration");
		config.SeedString(
			entry,
			MigratedSensorSelection.RemoteSourcesKey,
			"""[{"Name":"Bad","Url":"file:///C:/sensors.json","Key":"bad"}]""");

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.RemoteSources, Is.Empty);
	}

	[Test]
	public void A_remote_list_is_stored_as_text_even_when_it_is_empty()
	{
		var values = new MigratedSensorSelection([CpuTotal], null, RemoteSources: []).ToValues();

		// An empty list is how removing the last remote is stored; leaving the key out would read back
		// as a document that predates remote monitoring.
		Assert.That(values.ContainsKey(MigratedSensorSelection.RemoteSourcesKey), Is.True);
		Assert.That(values[MigratedSensorSelection.RemoteSourcesKey].GetString(), Is.EqualTo("[]"));
	}

	[Test]
	public void An_unset_startup_elevation_choice_is_not_stored_at_all()
	{
		var values = new MigratedSensorSelection([CpuTotal], null).ToValues();

		// Only the settings window writes the preference, and it always writes an answer. A migration
		// leaves the key out, and a missing key reads back as off.
		Assert.That(values.ContainsKey(MigratedSensorSelection.AutoElevateKey), Is.False);
	}

	[Test]
	public async Task The_default_units_come_back_whole()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var entry = MigrationTestData.SeedMigratedSensors(config, CpuTotal);

		MigrationTestData.SeedUnitDefaults(
			config,
			entry,
			new Dictionary<SensorType, SensorUnit>
			{
				[SensorType.Temperature] = SensorUnit.Fahrenheit,
				[SensorType.Data] = SensorUnit.Gibibytes,
			});

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.UnitDefaults, Is.Not.Null);
		Assert.That(restored.UnitDefaults![SensorType.Temperature], Is.EqualTo(SensorUnit.Fahrenheit));
		Assert.That(restored.UnitDefaults[SensorType.Data], Is.EqualTo(SensorUnit.Gibibytes));

		// The map is complete on read, so a kind the document did not mention still has an answer.
		Assert.That(restored.UnitDefaults, Has.Count.EqualTo(SensorUnits.SupportedKinds.Count));
		Assert.That(restored.UnitDefaults[SensorType.SmallData], Is.EqualTo(SensorUnit.Megabytes));
	}

	[Test]
	public async Task A_document_without_default_units_reads_them_as_unset()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		MigrationTestData.SeedMigratedSensors(config, CpuTotal);

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		// Null means "nothing was ever chosen", which the selection turns into the library's own units.
		Assert.That(restored.UnitDefaults, Is.Null);
	}

	[Test]
	public async Task A_stored_default_that_does_not_suit_its_kind_is_dropped()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var entry = MigrationTestData.SeedMigratedSensors(config, CpuTotal);
		config.SeedString(
			entry,
			MigratedSensorSelection.UnitDefaultsKey,
			"""{"Temperature":"Gigabytes","Data":"Gibibytes"}""");

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.UnitDefaults, Is.Not.Null);
		Assert.That(restored.UnitDefaults![SensorType.Temperature], Is.EqualTo(SensorUnit.Celsius));
		Assert.That(restored.UnitDefaults[SensorType.Data], Is.EqualTo(SensorUnit.Gibibytes));
	}

	[Test]
	public async Task A_stored_default_this_version_does_not_offer_is_dropped()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var config = new FakeIntegrationConfig();
		var entry = MigrationTestData.SeedMigratedSensors(config, CpuTotal);
		config.SeedString(
			entry,
			MigratedSensorSelection.UnitDefaultsKey,
			"""{"Humidity":"Celsius","Temperature":"Fahrenheit"}""");

		var restored = await MigratedSensorSelection.ReadAsync(config, logger);

		Assert.That(restored.UnitDefaults, Is.Not.Null);
		Assert.That(restored.UnitDefaults![SensorType.Temperature], Is.EqualTo(SensorUnit.Fahrenheit));
		Assert.That(restored.UnitDefaults.ContainsKey(SensorType.Humidity), Is.False);
	}

	[Test]
	public void The_defaults_are_stored_as_text()
	{
		var values = new MigratedSensorSelection(
			[CpuTotal],
			null,
			UnitDefaults: new Dictionary<SensorType, SensorUnit>
			{
				[SensorType.Temperature] = SensorUnit.Fahrenheit,
			}).ToValues();

		Assert.That(values.ContainsKey(MigratedSensorSelection.UnitDefaultsKey), Is.True);
		Assert.That(
			values[MigratedSensorSelection.UnitDefaultsKey].GetString(),
			Is.EqualTo("""{"Temperature":"Fahrenheit"}"""));
	}
}
