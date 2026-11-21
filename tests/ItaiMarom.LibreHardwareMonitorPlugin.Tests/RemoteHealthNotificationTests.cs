using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Migration;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using LibreHardwareMonitor.Hardware;
using MacroDeck.Plugin.Testing.Fakes;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The remote-health notification: one on the online-to-offline transition, dismissed on recovery, and
/// none at startup for a machine that has never answered in this session.
/// </summary>
[TestFixture]
public sealed class RemoteHealthNotificationTests
{
	private static readonly RemoteSourceDefinition LivingRoom =
		new("Living Room", "http://living-room-pc:8085/data.json", "living_room");

	[Test]
	public async Task An_online_to_offline_transition_notifies_and_recovery_dismisses_it()
	{
		await using var fixture = await Fixture.CreateAsync();

		var successAt = DateTimeOffset.UtcNow;
		fixture.Remote.Set(Online(successAt));
		Assert.That(fixture.Context.Notifications.Current, Is.Empty);

		fixture.Remote.Set(Offline(successAt));
		Assert.That(fixture.Context.Notifications.Current, Has.Count.EqualTo(1));

		fixture.Remote.Set(Online(DateTimeOffset.UtcNow));
		Assert.That(fixture.Context.Notifications.Current, Is.Empty);
	}

	[Test]
	public async Task A_machine_that_was_never_online_does_not_notify_at_startup()
	{
		await using var fixture = await Fixture.CreateAsync();

		// Three failures have made it Offline, but it has no LastSuccessAt: it was already asleep when
		// the plugin started, so this is information for the issue list, not an event.
		fixture.Remote.Set(Offline(lastSuccessAt: null));

		Assert.That(fixture.Context.Notifications.Current, Is.Empty);
	}

	private static RemoteSourceStatus Online(DateTimeOffset successAt) =>
		new(
			LivingRoom.Key,
			LivingRoom.Name,
			LivingRoom.Url,
			new RemoteSourceHealth(RemoteSourceState.Online, successAt, null, 0));

	private static RemoteSourceStatus Offline(DateTimeOffset? lastSuccessAt) =>
		new(
			LivingRoom.Key,
			LivingRoom.Name,
			LivingRoom.Url,
			new RemoteSourceHealth(
				RemoteSourceState.Offline,
				lastSuccessAt,
				"connection refused",
				3));

	private sealed class Fixture : IAsyncDisposable
	{
		private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
		private readonly AggregateHardwareSource _aggregate;
		private readonly LibreHardwareMonitorIntegration _integration;

		private Fixture()
		{
			Remote = new FakeRemoteHealthSource();
			_aggregate = new AggregateHardwareSource(
				new FakeHardwareSensorSource(),
				_logger,
				_ => Remote);
			_integration = new LibreHardwareMonitorIntegration(
				_logger,
				_aggregate,
				new SensorSelection(),
				FakeAccessDiagnosis.Reachable(),
				new RecordingCatalogNotifier());
		}

		public FakeRemoteHealthSource Remote { get; }

		public FakeIntegrationContext Context { get; } = new();

		public static async Task<Fixture> CreateAsync()
		{
			var fixture = new Fixture();
			var entry = MigrationTestData.SeedMigratedSensors(fixture.Context.Config);
			MigrationTestData.SeedRemoteSources(fixture.Context.Config, entry, LivingRoom);
			await fixture._integration.InitializeAsync(fixture.Context);
			return fixture;
		}

		public async ValueTask DisposeAsync()
		{
			await _integration.DisposeAsync();
			_aggregate.Dispose();
			(_logger as IDisposable)?.Dispose();
		}
	}

	private sealed class FakeRemoteHealthSource : IHardwareSensorSource, IRemoteHealthSource
	{
		private readonly List<RemoteSourceStatus> _statuses = [];

		public IReadOnlyList<RemoteSourceStatus> RemoteStatuses => _statuses;

		public event EventHandler<RemoteHealthChangedEventArgs>? RemoteHealthChanged;

		public bool TryRetryRemote(string key) => true;

		public void Set(RemoteSourceStatus status)
		{
			_statuses.Clear();
			_statuses.Add(status);
			RemoteHealthChanged?.Invoke(this, new RemoteHealthChangedEventArgs(status));
		}

		public void Open()
		{
		}

		public void Close()
		{
		}

		public IReadOnlyList<HardwareSensor> Read(IReadOnlyCollection<string>? hardwareFilter) => [];

		public IReadOnlyList<SensorIdentifier> ReadControls() => [];

		public bool SetControl(string sensorId, float value) => false;

		public bool ResetControl(string sensorId) => false;

		public void Dispose()
		{
		}
	}
}
