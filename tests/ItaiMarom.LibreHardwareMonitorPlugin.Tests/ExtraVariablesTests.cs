using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Issues;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using MacroDeck.Sdk.Variables;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The variables that are not sensors - a remote machine's health and the elevation flag - and the issue
/// that says why a machine's readings stopped.
/// </summary>
[TestFixture]
public sealed class ExtraVariablesTests
{
	private static readonly RemoteSourceDefinition LivingRoom =
		new("Living Room", "http://living-room-pc:8085/data.json", "living_room");

	[Test]
	public async Task Each_remote_declares_an_online_flag_and_a_status_text()
	{
		await using var fixture = new Fixture();
		fixture.Remotes.Replace([LivingRoom]);
		fixture.Health.Statuses.Add(Online());

		var online = fixture.Provider.Variables.Single(variable => variable.Name == "living_room_online");
		var status = fixture.Provider.Variables.Single(variable => variable.Name == "living_room_status");

		Assert.That(online.Type, Is.EqualTo(VariableType.Boolean));
		Assert.That(status.Type, Is.EqualTo(VariableType.Text));
		Assert.That((bool?)(await fixture.Provider.ReadAsync(online.ResolvedId!)).Value, Is.True);
		Assert.That((string?)(await fixture.Provider.ReadAsync(status.ResolvedId!)).Value, Is.EqualTo("Online"));
	}

	[Test]
	public async Task An_offline_remote_reads_false_and_says_offline()
	{
		await using var fixture = new Fixture();
		fixture.Remotes.Replace([LivingRoom]);
		fixture.Health.Statuses.Add(Offline("connection refused"));

		var online = fixture.Provider.Variables.Single(variable => variable.Name == "living_room_online");
		var status = fixture.Provider.Variables.Single(variable => variable.Name == "living_room_status");

		Assert.That((bool?)(await fixture.Provider.ReadAsync(online.ResolvedId!)).Value, Is.False);
		Assert.That((string?)(await fixture.Provider.ReadAsync(status.ResolvedId!)).Value, Is.EqualTo("Offline"));
	}

	[Test]
	public async Task The_elevated_helper_is_a_boolean_variable()
	{
		await using var fixture = new Fixture(elevated: true);

		var variable = fixture.Provider.Variables.Single(item => item.Name == "elevated_readings");

		Assert.That(variable.Type, Is.EqualTo(VariableType.Boolean));
		Assert.That((bool?)(await fixture.Provider.ReadAsync(variable.ResolvedId!)).Value, Is.True);
	}

	[Test]
	public void An_offline_remote_becomes_an_issue_with_a_retry()
	{
		var health = new StubHealth();
		health.Statuses.Add(Offline("connection refused"));
		var issues = new RemoteHealthIssueProvider(health);

		var issue = issues.Issues().Single();

		Assert.That(issue.Id, Is.EqualTo("remote-offline-living-room"));
		Assert.That(RemoteHealthIssueProvider.Owns(issue.Id), Is.True);
		Assert.That(issue.Description.Literal, Does.Contain("connection refused"));

		var resolution = issues.Retry(issue.Id);
		Assert.That(health.RetryCount, Is.EqualTo(1));
		Assert.That(resolution.FollowUp, Is.EqualTo(MacroDeck.Sdk.Issues.IssueResolutionFollowUp.None));
	}

	[Test]
	public void A_healthy_remote_raises_no_issue()
	{
		var health = new StubHealth();
		health.Statuses.Add(Online());

		Assert.That(new RemoteHealthIssueProvider(health).Issues(), Is.Empty);
	}

	private static RemoteSourceStatus Online() =>
		new(
			LivingRoom.Key,
			LivingRoom.Name,
			LivingRoom.Url,
			new RemoteSourceHealth(RemoteSourceState.Online, DateTimeOffset.UtcNow, null, 0));

	private static RemoteSourceStatus Offline(string error) =>
		new(
			LivingRoom.Key,
			LivingRoom.Name,
			LivingRoom.Url,
			new RemoteSourceHealth(RemoteSourceState.Offline, DateTimeOffset.UtcNow, error, 3));

	private sealed class Fixture : IAsyncDisposable
	{
		private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

		public Fixture(bool elevated = false)
		{
			Monitor = new HardwareMonitor(
				Source,
				new HardwareMonitorOptions
				{
					PollingInterval = TimeSpan.FromMilliseconds(10),
					StaleAfter = TimeSpan.FromMilliseconds(100),
				},
				_logger);

			Elevation = new FakeElevatableSource(elevated: elevated);
			Provider = new SensorVariableProvider(
				Monitor,
				Selection,
				Remotes,
				Health,
				Elevation);
		}

		public FakeHardwareSensorSource Source { get; } = new();

		public SensorSelection Selection { get; } = new();

		public RemoteSourceSelection Remotes { get; } = new();

		public StubHealth Health { get; } = new();

		public FakeElevatableSource Elevation { get; }

		public HardwareMonitor Monitor { get; }

		public SensorVariableProvider Provider { get; }

		public async ValueTask DisposeAsync()
		{
			Provider.Dispose();
			await Monitor.DisposeAsync();
			Elevation.Dispose();
			(_logger as IDisposable)?.Dispose();
		}
	}

	private sealed class StubHealth : IRemoteHealthSource
	{
		public List<RemoteSourceStatus> Statuses { get; } = [];

		public int RetryCount { get; private set; }

		public IReadOnlyList<RemoteSourceStatus> RemoteStatuses => Statuses;

		public event EventHandler<RemoteHealthChangedEventArgs>? RemoteHealthChanged
		{
			add
			{
			}
			remove
			{
			}
		}

		public bool TryRetryRemote(string key)
		{
			RetryCount++;
			return true;
		}
	}
}
