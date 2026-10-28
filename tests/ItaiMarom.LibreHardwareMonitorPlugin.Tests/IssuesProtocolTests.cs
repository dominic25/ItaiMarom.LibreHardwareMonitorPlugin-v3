using System.Text.Json;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Issues;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using LibreHardwareMonitor.Hardware;
using MacroDeck.Plugin.Protocol.Capabilities.Issues;
using MacroDeck.Plugin.Testing;
using MacroDeck.Plugin.Testing.Fakes;
using MacroDeck.Sdk.Notifications;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The warning about the readings this process cannot reach, as the host drives it: the <c>list</c> and
/// <c>resolve</c> operations the WebSocket carries, and the notification raised when the plugin starts.
/// </summary>
/// <remarks>
/// The responses are asserted as JSON rather than through the protocol DTOs on purpose, matching
/// <see cref="MigrationProtocolTests"/>: this is the wire shape the host parses, and asserting it is what
/// catches a warning wired to the wrong capability, or one that answers with nothing.
/// </remarks>
[TestFixture]
public sealed class IssuesProtocolTests
{
	[Test]
	public async Task The_host_is_told_this_plugin_can_report_issues()
	{
		await using var harness = CreateHarness(FakeAccessDiagnosis.Reachable());

		// The capability is declared by implementing IIntegrationIssueProvider; nothing beyond the
		// integration itself has to be registered for it.
		var declared = harness.Declared.Single(capability => capability.Kind == "issues");
		Assert.That(declared.LocalId, Is.EqualTo("provider"));
	}

	[Test]
	public async Task The_host_sees_no_issue_when_this_process_can_read_everything()
	{
		await using var harness = CreateHarness(FakeAccessDiagnosis.Reachable());
		await harness.InitializeIntegrationsAsync();

		var listed = Response(await harness.Issues.GetIssuesAsync());

		// An empty list rather than a placeholder: the warning is about this machine, and this machine is
		// fine.
		Assert.That(listed.GetProperty("issues").EnumerateArray(), Is.Empty);
	}

	[Test]
	public async Task The_host_sees_the_warning_when_this_process_was_refused_the_driver()
	{
		await using var harness = CreateHarness(FakeAccessDiagnosis.Unreachable());
		await harness.InitializeIntegrationsAsync();

		var listed = Response(await harness.Issues.GetIssuesAsync());
		var issue = listed.GetProperty("issues").EnumerateArray().Single();

		Assert.That(issue.GetProperty("id").GetString(), Is.EqualTo(MissingReadingsIssueProvider.IssueId));
		Assert.That(issue.GetProperty("title").ToString(), Is.EqualTo("Some readings are missing"));
		Assert.That(issue.GetProperty("severity").GetString(), Is.EqualTo("Warning"));
		Assert.That(issue.GetProperty("actionLabel").ToString(), Is.EqualTo("Open the sensor window"));

		// The description is the sentence the sensor window prints, carried verbatim so the two surfaces
		// cannot drift apart.
		Assert.That(issue.GetProperty("description").ToString(), Is.EqualTo(FakeAccessDiagnosis.NotElevated));
	}

	[Test]
	public async Task Pressing_the_issue_opens_the_sensor_window()
	{
		await using var harness = CreateHarness(FakeAccessDiagnosis.Unreachable());
		await harness.InitializeIntegrationsAsync();

		var resolved = Response(await harness.Issues.ResolveAsync(new IssueResolveArguments
		{
			IssueId = MissingReadingsIssueProvider.IssueId,
		}));

		// The follow-up is what actually opens the settings screen; the message tells the user what is
		// about to happen rather than claiming their readings are back.
		Assert.That(resolved.GetProperty("success").GetBoolean(), Is.True);
		Assert.That(resolved.GetProperty("followUp").GetString(), Is.EqualTo("StartConfigFlow"));
		Assert.That(resolved.GetProperty("message").ToString(), Is.EqualTo("Opening the sensor window."));
	}

	[Test]
	public async Task An_issue_this_plugin_does_not_know_is_refused_before_it_reaches_the_plugin()
	{
		await using var harness = CreateHarness(FakeAccessDiagnosis.Unreachable());
		await harness.InitializeIntegrationsAsync();

		// The host only offers an action on an issue it is showing, so an id the plugin is not currently
		// reporting never gets as far as ResolveIssueAsync. The plugin still answers one by name - it has
		// no way to know what the host will ask it - which MissingReadingsIssueProviderTests covers; here
		// what matters is that the refusal is the host's, and that it is a refusal rather than a crash.
		var outcome = await harness.Issues.ResolveAsync(new IssueResolveArguments
		{
			IssueId = "somebody-elses-issue",
		});

		Assert.That(outcome.Succeeded, Is.False);
		Assert.That(outcome.Error?.Message, Does.Contain("somebody-elses-issue"));
	}

	[Test]
	public async Task The_warning_offers_elevation_when_the_source_can_start_a_helper()
	{
		var source = new FakeElevatableSource();
		await using var harness = CreateHarness(FakeAccessDiagnosis.Unreachable(), source);
		SeedSensors(harness, source.Hardware);
		await harness.InitializeIntegrationsAsync();

		var listed = Response(await harness.Issues.GetIssuesAsync());
		var issue = listed.GetProperty("issues").EnumerateArray().Single();

		// The button is the fix now, not an explanation of one: one UAC prompt from here rather than a
		// restart of Macro Deck as an administrator.
		Assert.That(issue.GetProperty("actionLabel").ToString(), Is.EqualTo("Enable elevated readings"));
	}

	[Test]
	public async Task Pressing_the_issue_starts_the_helper_and_dismisses_the_warning()
	{
		var source = new FakeElevatableSource();
		await using var harness = CreateHarness(FakeAccessDiagnosis.Unreachable(), source);
		SeedSensors(harness, source.Hardware);
		await harness.InitializeIntegrationsAsync();

		Assert.That(harness.Context.Notifications.Current, Has.Count.EqualTo(1));

		var resolved = Response(await harness.Issues.ResolveAsync(new IssueResolveArguments
		{
			IssueId = MissingReadingsIssueProvider.IssueId,
		}));

		// The issue's button is the only place a helper is started, and a helper that took over makes
		// the warning it answered obsolete.
		Assert.That(source.EnableCount, Is.EqualTo(1));
		Assert.That(source.IsElevated, Is.True);
		Assert.That(resolved.GetProperty("success").GetBoolean(), Is.True);
		Assert.That(resolved.GetProperty("followUp").GetString(), Is.EqualTo("None"));
		Assert.That(resolved.GetProperty("message").ToString(), Is.EqualTo("Elevated readings are on."));
		Assert.That(harness.Context.Notifications.Current, Is.Empty);
	}

	[Test]
	public async Task A_dismissed_prompt_is_reported_as_a_failure_rather_than_a_silent_success()
	{
		var source = new FakeElevatableSource
		{
			NextResult = new ElevationResult(ElevationOutcome.Cancelled),
		};

		await using var harness = CreateHarness(FakeAccessDiagnosis.Unreachable(), source);
		SeedSensors(harness, source.Hardware);
		await harness.InitializeIntegrationsAsync();

		var resolved = Response(await harness.Issues.ResolveAsync(new IssueResolveArguments
		{
			IssueId = MissingReadingsIssueProvider.IssueId,
		}));

		// The user said no at the UAC prompt. The plugin keeps the readings it already has, and the
		// issue stays up because the readings it describes are still missing.
		Assert.That(resolved.GetProperty("success").GetBoolean(), Is.False);
		Assert.That(resolved.GetProperty("message").ToString(), Does.Contain("dismissed"));
		Assert.That(harness.Context.Notifications.Current, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task The_warning_is_raised_as_a_notification_when_the_plugin_starts()
	{
		await using var harness = CreateHarness(FakeAccessDiagnosis.Unreachable());
		SeedSensors(harness);
		await harness.InitializeIntegrationsAsync();

		var notification = harness.Context.Notifications.Current.Single();

		// The issue waits in a list to be looked up, and the failure it describes was silent: the user
		// whose processor temperatures stopped moving never opened that list.
		Assert.That(notification.Key, Is.EqualTo(MissingReadingsIssueProvider.IssueId));
		Assert.That(notification.Title, Is.EqualTo("Some readings are missing"));
		Assert.That(notification.Message, Is.EqualTo(FakeAccessDiagnosis.NotElevated));
		Assert.That(notification.Level, Is.EqualTo(UserNotificationLevel.Warning));
	}

	[Test]
	public async Task No_notification_is_raised_when_this_process_can_read_everything()
	{
		await using var harness = CreateHarness(FakeAccessDiagnosis.Reachable());
		SeedSensors(harness);
		await harness.InitializeIntegrationsAsync();

		Assert.That(harness.Context.Notifications.Current, Is.Empty);
	}

	[Test]
	public async Task No_notification_is_raised_before_anything_is_configured()
	{
		await using var harness = CreateHarness(FakeAccessDiagnosis.Unreachable());
		await harness.InitializeIntegrationsAsync();

		// A warning about readings nothing depends on yet would be noise at every start-up, and the
		// settings screen says the same thing the moment the user opens it.
		Assert.That(harness.Context.Notifications.Current, Is.Empty);
	}

	[Test]
	public async Task Initializing_again_replaces_the_notification_rather_than_piling_up()
	{
		await using var harness = CreateHarness(FakeAccessDiagnosis.Unreachable());
		SeedSensors(harness);

		await harness.InitializeIntegrationsAsync();
		await harness.InitializeIntegrationsAsync();

		// A reconnect re-initializes the integration, and the host has no reason to show the same warning
		// twice for it.
		Assert.That(harness.Context.Notifications.Current, Has.Count.EqualTo(1));
	}

	/// <summary>Gives the plugin something to serve, so a warning about readings is about something.</summary>
	private static void SeedSensors(PluginTestHarness harness, FakeHardwareSensorSource? source = null)
	{
		source ??= (FakeHardwareSensorSource)harness.Services.GetRequiredService<IHardwareSensorSource>();
		source.Add("CPU", "CPU Total", SensorType.Load, 42f);

		// Through the configuration, because that is the only road a selection has into the integration:
		// initialize reads it back and replaces whatever the test set by hand.
		MigrationTestData.SeedMigratedSensors(harness.Context.Config, source.Sensors[0]);
	}

	private static PluginTestHarness CreateHarness(
		IAccessDiagnosis diagnosis,
		IHardwareSensorSource? source = null) =>
		PluginTestHarness.Create(builder => builder

			// Registered here the same way Program.cs registers it, because the wire carries the resolved
			// words: without it the host has nothing to translate the issue's keys with.
			.UseLocalization(Strings.LocalizationCatalog)
			.ConfigureServices((_, services) =>
			{
				services.AddSingleton(source ?? new FakeHardwareSensorSource());
				services.AddSingleton<SensorSelection>();
				services.AddSingleton(diagnosis);
			})
			.RegisterIntegration<LibreHardwareMonitorIntegration>());

	/// <summary>The JSON the host received, which every operation here is expected to have answered.</summary>
	private static JsonElement Response(CapabilityInvocationOutcome outcome)
	{
		Assert.That(outcome.Succeeded, Is.True, outcome.Error?.Message);
		Assert.That(outcome.Data, Is.Not.Null);

		return outcome.Data!.Value;
	}
}
