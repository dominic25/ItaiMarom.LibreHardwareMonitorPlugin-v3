using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Migration;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The parts that keep a fan from being left at a written speed: the restore-all request, the rescan
/// that would otherwise undo a written value, and the restore that runs when the plugin stops.
/// </summary>
[TestFixture]
public sealed class FanSafetyTests
{
	private static readonly HardwareMonitorOptions Options = new()
	{
		PollingInterval = TimeSpan.FromMilliseconds(50),
		StaleAfter = TimeSpan.FromSeconds(1),
	};

	[Test]
	public async Task Restoring_all_hands_every_control_back()
	{
		var fan = TestFanActions.Control("Fan #1") with { Controllable = true };
		var other = TestFanActions.Control("Fan #2") with { Controllable = true };
		var source = new FakeHardwareSensorSource().AddControl(fan).AddControl(other);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, Options, logger);

		var result = await monitor.ResetAllControlsAsync();

		Assert.That(result.Reset, Is.EqualTo(2));
		Assert.That(result.Failed, Is.Empty);
		Assert.That(source.LastResetSensorId, Is.AnyOf(fan.Id, other.Id));
	}

	[Test]
	public async Task A_rescan_reapplies_a_written_fan_speed()
	{
		var fan = TestFanActions.Control("Fan #1") with { Controllable = true };
		var source = new FakeHardwareSensorSource().AddControl(fan, value: 50f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, Options, logger);

		Assert.That(await monitor.SetControlAsync(fan.Id, 40), Is.True);

		// A rebuilt control starts in its default mode. Without the re-apply a rescan would quietly hand
		// the fan back to the motherboard.
		source.OnRescan = () => source.ResetControl(fan.Id);

		var result = await monitor.RescanAsync();

		Assert.That(source.RescanCount, Is.EqualTo(1));
		Assert.That(result.Sensors, Has.Count.EqualTo(1));
		Assert.That(source.LastSetSensorId, Is.EqualTo(fan.Id));
		Assert.That(source.LastSetValue, Is.EqualTo(40f));
	}

	[Test]
	public async Task The_loop_s_rate_is_handed_to_the_source()
	{
		var source = new FakeHardwareSensorSource();
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, Options, logger);

		monitor.SetOptions(Options with { PollingInterval = TimeSpan.FromMilliseconds(750) });

		Assert.That(source.LastPollingInterval, Is.EqualTo(TimeSpan.FromMilliseconds(750)));
	}

	[Test]
	public void The_restore_fans_preference_is_stored_with_the_selection()
	{
		var values = new MigratedSensorSelection([], null, RestoreFansOnExit: false).ToValues();

		Assert.That(
			values[MigratedSensorSelection.RestoreFansOnExitKey].GetString(),
			Is.EqualTo("false"));
	}

	[Test]
	public async Task Stopping_the_plugin_hands_the_fans_back_by_default()
	{
		var fan = TestFanActions.Control("Fan #1") with { Controllable = true };
		var source = new FakeHardwareSensorSource().AddControl(fan, value: 50f);
		using var logger = new LoggerConfiguration().CreateLogger();
		var integration = new LibreHardwareMonitorIntegration(
			logger,
			source,
			new Variables.SensorSelection(),
			FakeAccessDiagnosis.Reachable(),
			new RecordingCatalogNotifier());

		await integration.InitializeAsync(new MacroDeck.Plugin.Testing.Fakes.FakeIntegrationContext());
		await integration.DisposeAsync();

		Assert.That(source.LastResetSensorId, Is.EqualTo(fan.Id));
	}

	[Test]
	public async Task A_cleared_checkbox_leaves_the_fans_alone()
	{
		var fan = TestFanActions.Control("Fan #1") with { Controllable = true };
		var source = new FakeHardwareSensorSource().AddControl(fan, value: 50f);
		using var logger = new LoggerConfiguration().CreateLogger();
		var integration = new LibreHardwareMonitorIntegration(
			logger,
			source,
			new Variables.SensorSelection(),
			FakeAccessDiagnosis.Reachable(),
			new RecordingCatalogNotifier());

		var context = new MacroDeck.Plugin.Testing.Fakes.FakeIntegrationContext();
		var entry = MigrationTestData.SeedMigratedSensors(context.Config, fan);
		context.Config.SeedString(entry, MigratedSensorSelection.RestoreFansOnExitKey, "false");

		await integration.InitializeAsync(context);
		await integration.DisposeAsync();

		Assert.That(source.LastResetSensorId, Is.Null);
	}
}
