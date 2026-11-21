using System.Text.Json;
using ItaiMarom.LibreHardwareMonitorPlugin.Events;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;
using MacroDeck.Sdk.Events;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The events the plugin offers: a threshold held for a while, a sensor that went quiet, a remote
/// machine crossing between online and offline, and the elevated helper starting.
/// </summary>
[TestFixture]
public sealed class EventEvaluatorTests
{
	[Test]
	public void The_plugin_offers_the_documented_events()
	{
		Assert.That(
			HardwareEvents.Definitions.Select(definition => definition.Id),
			Is.EquivalentTo(new[]
			{
				HardwareEvents.ThresholdId,
				HardwareEvents.StoppedId,
				HardwareEvents.RemoteOfflineId,
				HardwareEvents.RemoteOnlineId,
				HardwareEvents.ElevationChangedId,
			}));

		Assert.That(
			HardwareEvents.Definitions,
			Has.All.Matches<EventDefinition>(definition => definition.DeliveryKind == EventDeliveryKind.Push));
	}

	[Test]
	public async Task A_threshold_fires_after_its_dwell_and_not_again_until_it_clears()
	{
		await using var fixture = new Fixture();
		await fixture.StartAsync();
		fixture.Publisher.Bindings.Add(Threshold(fixture.Sensor, threshold: 80, dwell: 10, hysteresis: 2));

		await fixture.SetValueAsync(90);
		fixture.Evaluator.Evaluate();
		Assert.That(fixture.Publisher.Published, Is.Empty);

		fixture.Now = fixture.Now.AddSeconds(11);
		fixture.Evaluator.Evaluate();
		Assert.That(fixture.Publisher.Published, Has.Count.EqualTo(1));
		Assert.That(fixture.Publisher.Published[0].EventId, Is.EqualTo(HardwareEvents.ThresholdId));
		Assert.That(fixture.Publisher.Published[0].Parameters[HardwareEvents.PayloadValue], Is.EqualTo(90d));
		Assert.That(
			fixture.Publisher.Published[0].Parameters[HardwareEvents.PayloadSensorName],
			Is.EqualTo(fixture.Sensor.DisplayName));
		Assert.That(
			fixture.Publisher.Published[0].Parameters[HardwareEvents.PayloadSensorId],
			Is.EqualTo(fixture.Sensor.ResourceId));

		// Still above, so the event does not repeat.
		fixture.Evaluator.Evaluate();
		Assert.That(fixture.Publisher.Published, Has.Count.EqualTo(1));

		// Back to 79 is inside the hysteresis band: still active.
		await fixture.SetValueAsync(79);
		fixture.Evaluator.Evaluate();
		Assert.That(fixture.Publisher.Published, Has.Count.EqualTo(1));

		// Below 78 clears it.
		await fixture.SetValueAsync(77);
		fixture.Evaluator.Evaluate();

		await fixture.SetValueAsync(90);
		fixture.Evaluator.Evaluate();
		fixture.Now = fixture.Now.AddSeconds(11);
		fixture.Evaluator.Evaluate();

		Assert.That(fixture.Publisher.Published, Has.Count.EqualTo(2));
	}

	[Test]
	public async Task The_poll_loop_drives_the_evaluator()
	{
		await using var fixture = new Fixture();
		fixture.Publisher.Bindings.Add(Threshold(fixture.Sensor, threshold: 40, dwell: 0, hysteresis: 0));
		fixture.Monitor.Polled += (_, _) => fixture.Evaluator.Evaluate();

		await fixture.StartAsync();

		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
		while (fixture.Publisher.Published.Count == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(10);
		}

		Assert.That(fixture.Publisher.Published, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task A_below_threshold_uses_the_other_direction()
	{
		await using var fixture = new Fixture();
		await fixture.StartAsync();
		fixture.Publisher.Bindings.Add(Threshold(
			fixture.Sensor,
			direction: HardwareEvents.DirectionBelow,
			threshold: 10,
			dwell: 0,
			hysteresis: 1));

		await fixture.SetValueAsync(5);
		fixture.Evaluator.Evaluate();

		Assert.That(fixture.Publisher.Published, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task A_stopped_sensor_fires_after_its_grace()
	{
		await using var fixture = new Fixture();
		await fixture.StartAsync();
		fixture.Publisher.Bindings.Add(Stopped(fixture.Sensor, grace: 30));

		fixture.Evaluator.Evaluate();
		Assert.That(fixture.Publisher.Published, Is.Empty);

		await fixture.SetValueAsync(null);
		fixture.Now = fixture.Now.AddSeconds(31);

		// The cache still holds the last sample: the event has to count from that sample, not wait for
		// StaleAfter to drop it and then start the grace window.
		Assert.That(fixture.Monitor.TryGetReading(fixture.Sensor, out _), Is.True);

		fixture.Evaluator.Evaluate();

		Assert.That(fixture.Publisher.Published, Has.Count.EqualTo(1));
		Assert.That(fixture.Publisher.Published[0].EventId, Is.EqualTo(HardwareEvents.StoppedId));
	}

	[Test]
	public async Task A_remote_machine_fires_on_the_offline_and_online_transitions()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var health = new StubHealth();
		var status = Online();
		health.Statuses.Add(status);
		await using var monitor = new HardwareMonitor(
			new FakeHardwareSensorSource(),
			new HardwareMonitorOptions { PollingInterval = TimeSpan.FromMilliseconds(10) },
			logger);
		var publisher = new FakePublisher();
		var evaluator = new SensorEventEvaluator(monitor, health, elevation: null, logger);
		evaluator.Attach(publisher);

		publisher.Bindings.Add(Remote(HardwareEvents.RemoteOfflineId, status.Key));
		publisher.Bindings.Add(Remote(HardwareEvents.RemoteOnlineId, status.Key));

		// The first observation is the starting point, not a transition.
		evaluator.Evaluate();
		Assert.That(publisher.Published, Is.Empty);

		health.Statuses[0] = status with
		{
			Health = new RemoteSourceHealth(RemoteSourceState.Offline, DateTimeOffset.UtcNow, "unreachable", 3),
		};
		evaluator.Evaluate();
		Assert.That(publisher.Published, Has.Count.EqualTo(1));
		Assert.That(publisher.Published[0].EventId, Is.EqualTo(HardwareEvents.RemoteOfflineId));
		Assert.That(
			publisher.Published[0].Parameters[HardwareEvents.PayloadMachineName],
			Is.EqualTo(status.Name));

		health.Statuses[0] = status;
		evaluator.Evaluate();
		Assert.That(publisher.Published, Has.Count.EqualTo(2));
		Assert.That(publisher.Published[1].EventId, Is.EqualTo(HardwareEvents.RemoteOnlineId));
	}

	[Test]
	public async Task The_elevation_event_fires_when_the_helper_starts()
	{
		await using var fixture = new Fixture();
		fixture.Publisher.Bindings.Add(new EventBinding
		{
			EventId = HardwareEvents.ElevationChangedId,
			Parameters = new Dictionary<string, EventBindingValue>(StringComparer.Ordinal),
		});

		fixture.Evaluator.Evaluate();
		Assert.That(fixture.Publisher.Published, Is.Empty);

		await fixture.Elevation.EnableAsync();
		fixture.Evaluator.Evaluate();

		Assert.That(fixture.Publisher.Published, Has.Count.EqualTo(1));
		Assert.That(fixture.Publisher.Published[0].Parameters[HardwareEvents.PayloadElevated], Is.True);
	}

	private static RemoteSourceStatus Online() =>
		new(
			"living_room",
			"Living Room",
			"http://living-room-pc:8085/data.json",
			new RemoteSourceHealth(RemoteSourceState.Online, DateTimeOffset.UtcNow, null, 0));

	private static EventBinding Threshold(
		SensorIdentifier sensor,
		string direction = HardwareEvents.DirectionAbove,
		double threshold = 80,
		double dwell = 0,
		double hysteresis = 2) =>
		new()
		{
			EventId = HardwareEvents.ThresholdId,
			Parameters = new Dictionary<string, EventBindingValue>(StringComparer.Ordinal)
			{
				[HardwareEvents.SensorParameter] = Value(sensor.ResourceId),
				[HardwareEvents.DirectionParameter] = Value(direction),
				[HardwareEvents.ThresholdParameter] = Value(threshold),
				[HardwareEvents.DwellParameter] = Value(dwell),
				[HardwareEvents.HysteresisParameter] = Value(hysteresis),
			},
		};

	private static EventBinding Stopped(SensorIdentifier sensor, double grace) =>
		new()
		{
			EventId = HardwareEvents.StoppedId,
			Parameters = new Dictionary<string, EventBindingValue>(StringComparer.Ordinal)
			{
				[HardwareEvents.SensorParameter] = Value(sensor.ResourceId),
				[HardwareEvents.GraceParameter] = Value(grace),
			},
		};

	private static EventBinding Remote(string eventId, string key) =>
		new()
		{
			EventId = eventId,
			Parameters = new Dictionary<string, EventBindingValue>(StringComparer.Ordinal)
			{
				[HardwareEvents.MachineParameter] = Value(key),
			},
		};

	private static EventBindingValue Value(object value) =>
		new(JsonSerializer.SerializeToElement(value), string.Empty);

	private sealed class Fixture : IAsyncDisposable
	{
		private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

		public Fixture()
		{
			Sensor = TestSensor.Create("CPU", "CPU Package", SensorType.Temperature);
			Source.Add(Sensor, 50f);
			Monitor = new HardwareMonitor(
				Source,
				new HardwareMonitorOptions
				{
					PollingInterval = TimeSpan.FromMilliseconds(10),
					// Long enough that the stopped-sensor test still has the last sample in the cache
					// when it advances its clock past the grace.
					StaleAfter = TimeSpan.FromSeconds(1),
				},
				_logger);
			Elevation = new FakeElevatableSource();
			Evaluator = new SensorEventEvaluator(Monitor, Health, Elevation, _logger)
			{
				Clock = () => Now,
				BindingRefresh = TimeSpan.Zero,
			};
			Evaluator.Attach(Publisher);
		}

		public FakeHardwareSensorSource Source { get; } = new();

		public HardwareMonitor Monitor { get; }

		public FakeElevatableSource Elevation { get; }

		public StubHealth Health { get; } = new();

		public FakePublisher Publisher { get; } = new();

		public SensorEventEvaluator Evaluator { get; }

		public SensorIdentifier Sensor { get; }

		public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

		public async Task StartAsync()
		{
			Monitor.SetWatchedSensors([Sensor]);
			Monitor.Start();
			await WaitUntilAsync(() => Monitor.TryGetReading(Sensor, out _));

			// Align the fake clock with the sample so a stopped-sensor grace measures from that sample.
			Now = DateTimeOffset.UtcNow;
		}

		public async Task SetValueAsync(float? value)
		{
			Source.Set(Sensor, value);

			if (value is null)
			{
				return;
			}

			await WaitUntilAsync(() =>
				Monitor.TryGetReading(Sensor, out var current)
				&& Math.Abs(current - value.Value) < 0.001);
		}

		public async ValueTask DisposeAsync()
		{
			Evaluator.Attach(null);
			await Monitor.DisposeAsync();
			Elevation.Dispose();
			(_logger as IDisposable)?.Dispose();
		}
	}

	private sealed class FakePublisher : IEventPublisher
	{
		public List<EventBinding> Bindings { get; } = [];

		public List<PublishedEvent> Published { get; } = [];

		public IReadOnlyList<EventBinding> GetBindings() => Bindings;

		public void Publish(string eventId, IReadOnlyDictionary<string, object?>? parameters) =>
			Published.Add(new PublishedEvent(eventId, parameters ?? new Dictionary<string, object?>()));
	}

	private sealed record PublishedEvent(
		string EventId,
		IReadOnlyDictionary<string, object?> Parameters);

	private sealed class StubHealth : IRemoteHealthSource
	{
		public List<RemoteSourceStatus> Statuses { get; } = [];

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

		public bool TryRetryRemote(string key) => true;
	}

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
