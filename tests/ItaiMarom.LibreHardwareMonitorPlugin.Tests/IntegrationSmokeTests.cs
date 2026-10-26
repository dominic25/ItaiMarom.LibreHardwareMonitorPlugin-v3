using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using LibreHardwareMonitor.Hardware;
using MacroDeck.Plugin.Testing.Fakes;
using MacroDeck.Sdk.Variables;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// What the integration is, as opposed to what the provider it delegates to does.
/// </summary>
[TestFixture]
public sealed class IntegrationSmokeTests
{
	[Test]
	public async Task The_integration_exposes_the_fan_control_actions()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource();
		await using var integration = new LibreHardwareMonitorIntegration(
			logger,
			source,
			new SensorSelection(),
			FakeAccessDiagnosis.Reachable(),
			new RecordingCatalogNotifier());

		// Building the action list must not probe hardware: the host reads it before it initializes the
		// integration, and a driver query here would delay describing the plugin.
		Assert.That(source.OpenCount, Is.Zero);
		Assert.That(source.ControlReadCount, Is.Zero);

		Assert.That(
			integration.Actions.Select(action => action.Id),
			Is.EqualTo(TestFanActions.Ids));
	}

	[Test]
	public async Task The_integration_serves_the_variable_surface()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		var selection = new SensorSelection();
		await using var integration = new LibreHardwareMonitorIntegration(
			logger,
			source,
			selection,
			FakeAccessDiagnosis.Reachable(),
			new RecordingCatalogNotifier());

		// The host asks a registered-but-not-yet-initialized integration what it has, so this has to answer
		// without touching the hardware.
		Assert.That(integration.Variables, Is.Empty);
		Assert.That(integration.VariablesDependOnConfiguration, Is.True);
		Assert.That(integration.SupportsCatalog, Is.True);
		Assert.That(integration.SupportsSearch, Is.True);

		var sensor = source.Sensors[0];
		selection.Replace([sensor]);

		var definition = integration.Variables.Single();
		Assert.That(definition.Name, Is.EqualTo("cpu_total_load"));
		Assert.That(definition.Materialization, Is.EqualTo(VariableMaterialization.Eager));

		// Not yet polled, but the id is addressable: the integration answers rather than throwing.
		Assert.That(await integration.ReadAsync(definition.ResolvedId!), Is.EqualTo(VariableReading.Unavailable));
	}

	[Test]
	public async Task A_written_variable_is_announced_to_the_host()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var fan = TestFanActions.Control("Fan #1") with { Controllable = true };
		var source = new FakeHardwareSensorSource().AddControl(fan, value: 50f);
		await using var integration = new LibreHardwareMonitorIntegration(
			logger,
			source,
			new SensorSelection(),
			FakeAccessDiagnosis.Reachable(),
			new RecordingCatalogNotifier());

		var context = new FakeIntegrationContext();
		MigrationTestData.SeedMigratedSensors(context.Config, fan);
		await integration.InitializeAsync(context);

		var definition = integration.Variables.Single();
		var result = await integration.SetValueAsync(definition.ResolvedId!, 40d);

		Assert.That(result.Status, Is.EqualTo(VariableWriteStatus.Applied));

		// The host does not echo a written value back, so the integration has to tell it that the
		// declaration's last value moved; otherwise a bound Slider stays where it was dragged to.
		Assert.That(
			context.Variables.DefinitionRefreshRequests,
			Has.Some.EquivalentTo(new[] { definition.ResolvedId! }));
	}

	[Test]
	public async Task Initializing_twice_and_shutting_down_twice_is_safe()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		var notifier = new RecordingCatalogNotifier();
		await using var integration = new LibreHardwareMonitorIntegration(
			logger,
			source,
			new SensorSelection(),
			FakeAccessDiagnosis.Reachable(),
			notifier);

		// The host initializes an integration again after a non-resume reconnect, and stops it before doing
		// so. Having been initialized before must not stand in the way of that, and stopping twice must not
		// close the hardware twice.
		var context = new FakeIntegrationContext();
		MigrationTestData.SeedMigratedSensors(context.Config, source.Sensors[0]);

		await integration.InitializeAsync(context);
		await integration.InitializeAsync(context);
		await WaitUntilAsync(() => source.OpenCount == 1);
		await integration.ShutdownAsync();
		await integration.ShutdownAsync();

		Assert.That(source.OpenCount, Is.EqualTo(1));
		Assert.That(source.CloseCount, Is.EqualTo(1));

		// The host only learns what a saved configuration changed by being told: it describes the plugin
		// once and never asks again on its own, so an initialize that does not announce leaves the new
		// selection out of the variable surface until the next reconnect.
		Assert.That(notifier.Announcements, Is.Not.Empty);
		Assert.That(notifier.Announcements, Has.All.Matches<(string Kind, string? LocalId, string? Reason)>(
			a => a.Kind == "variables"));
	}

	/// <summary>
	/// Waits for the loop to reach the hardware. Stopping before it has opened is a race the assertion
	/// about the open count cannot describe, so the test waits for the open it is about to check.
	/// </summary>
	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
		while (DateTime.UtcNow < deadline)
		{
			if (condition())
			{
				return;
			}

			await Task.Delay(10);
		}

		Assert.Fail("The condition was still false after 5 seconds.");
	}
}
