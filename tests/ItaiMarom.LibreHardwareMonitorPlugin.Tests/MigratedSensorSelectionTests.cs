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
}
