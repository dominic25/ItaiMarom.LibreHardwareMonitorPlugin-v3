using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The polling loop's lifecycle, driven through <see cref="FakeHardwareSensorSource"/> so nothing here
/// touches real hardware.
/// </summary>
[TestFixture]
public sealed class HardwareMonitorTests
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

	private static readonly SensorIdentifier CpuLoad = TestSensor.Create("CPU", "CPU Total", SensorType.Load);

	private static readonly string[] CpuHardwareOnly = ["CPU"];

	private static HardwareMonitorOptions FastOptions => new()
	{
		PollingInterval = TimeSpan.FromMilliseconds(10),
		StaleAfter = TimeSpan.FromMilliseconds(100),
	};

	[Test]
	public async Task The_loop_reads_a_watched_sensor_into_the_cache()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);

		monitor.SetWatchedSensors([CpuLoad]);
		monitor.Start();

		await WaitUntilAsync(() => monitor.TryGetReading(CpuLoad, out _));

		Assert.That(monitor.TryGetReading(CpuLoad, out var value), Is.True);
		Assert.That(value, Is.EqualTo(42d).Within(0.001));
		Assert.That(source.OpenCount, Is.EqualTo(1));
	}

	[Test]
	public async Task The_poll_runs_off_the_thread_that_started_it()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);

		var caller = Environment.CurrentManagedThreadId;
		int? poller = null;
		source.OnRead = () => poller = Environment.CurrentManagedThreadId;

		monitor.SetWatchedSensors([CpuLoad]);
		monitor.Start();

		await WaitUntilAsync(() => poller is not null);

		// If this ever fails the requirement it guards is gone: a slow sensor read is then a stalled
		// Macro Deck session.
		Assert.That(poller, Is.Not.EqualTo(caller));
	}

	[Test]
	public async Task Only_the_hardware_holding_a_watched_sensor_is_updated()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Total", SensorType.Load, 42f)
			.Add("GPU", "GPU Core", SensorType.Temperature, 60f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);

		monitor.SetWatchedSensors([CpuLoad]);
		monitor.Start();

		await WaitUntilAsync(() => source.LastFilter is not null);

		Assert.That(source.LastFilter, Is.EquivalentTo(CpuHardwareOnly));
	}

	[Test]
	public async Task A_sensor_that_is_not_watched_is_never_cached()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Total", SensorType.Load, 42f)
			.Add("GPU", "GPU Core", SensorType.Temperature, 60f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);

		monitor.SetWatchedSensors([CpuLoad]);
		monitor.Start();

		var gpu = TestSensor.Create("GPU", "GPU Core", SensorType.Temperature);
		await WaitUntilAsync(() => monitor.TryGetReading(CpuLoad, out _));

		Assert.That(monitor.TryGetReading(gpu, out _), Is.False);
		Assert.That(monitor.Snapshot().Keys, Is.EquivalentTo(new[] { CpuLoad }));
	}

	[Test]
	public async Task Unwatching_a_sensor_drops_the_value_it_had()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);

		monitor.SetWatchedSensors([CpuLoad]);
		monitor.Start();
		await WaitUntilAsync(() => monitor.TryGetReading(CpuLoad, out _));

		monitor.SetWatchedSensors([]);

		Assert.That(monitor.TryGetReading(CpuLoad, out _), Is.False);
	}

	[Test]
	public async Task A_reading_that_stops_arriving_goes_stale()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);

		monitor.SetWatchedSensors([CpuLoad]);
		monitor.Start();
		await WaitUntilAsync(() => monitor.TryGetReading(CpuLoad, out _));

		source.Set(CpuLoad, null);

		// A device that really went away must stop reporting a value that is no longer true.
		await WaitUntilAsync(() => !monitor.TryGetReading(CpuLoad, out _));
	}

	[Test]
	public async Task A_poll_that_throws_does_not_take_the_loop_down()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);

		var reads = 0;
		source.OnRead = () =>
		{
			if (++reads == 1)
			{
				throw new InvalidOperationException("a device vanished mid-read");
			}
		};

		monitor.SetWatchedSensors([CpuLoad]);
		monitor.Start();

		await WaitUntilAsync(() => monitor.TryGetReading(CpuLoad, out _));
	}

	[Test]
	public async Task Start_and_stop_are_idempotent_and_reversible()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);

		monitor.SetWatchedSensors([CpuLoad]);
		monitor.Start();
		monitor.Start();

		await WaitUntilAsync(() => monitor.TryGetReading(CpuLoad, out _));

		Assert.That(source.OpenCount, Is.EqualTo(1));
		Assert.That(monitor.IsRunning, Is.True);

		await monitor.StopAsync();
		await monitor.StopAsync();

		Assert.That(source.CloseCount, Is.EqualTo(1));
		Assert.That(monitor.IsRunning, Is.False);

		var readsWhenStopped = source.ReadCount;
		await Task.Delay(100);
		Assert.That(source.ReadCount, Is.EqualTo(readsWhenStopped));

		// An integration is initialized again after a reconnect, so stopping has to be undoable.
		monitor.Start();
		await WaitUntilAsync(() => source.ReadCount > readsWhenStopped);

		Assert.That(source.OpenCount, Is.EqualTo(2));
	}

	[Test]
	public async Task Enumerating_reads_every_sensor_even_when_the_loop_is_stopped()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Total", SensorType.Load, 42f)
			.Add("GPU", "GPU Core", SensorType.Temperature, 60f);
		using var logger = new LoggerConfiguration().CreateLogger();
		await using var monitor = new HardwareMonitor(source, FastOptions, logger);

		var sensors = await monitor.EnumerateSensorsAsync();

		Assert.That(sensors, Has.Count.EqualTo(2));
		Assert.That(source.LastFilter, Is.Null);
		Assert.That(source.OpenCount, Is.EqualTo(1));
	}

	[Test]
	public async Task Disposing_stops_the_loop_and_closes_the_hardware()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		var monitor = new HardwareMonitor(source, FastOptions, logger);

		monitor.SetWatchedSensors([CpuLoad]);
		monitor.Start();
		await WaitUntilAsync(() => monitor.TryGetReading(CpuLoad, out _));

		await monitor.DisposeAsync();
		await monitor.DisposeAsync();

		Assert.That(source.CloseCount, Is.GreaterThanOrEqualTo(1));
		Assert.That(monitor.IsRunning, Is.False);
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow + Patience;
		while (DateTime.UtcNow < deadline)
		{
			if (condition())
			{
				return;
			}

			await Task.Delay(10);
		}

		Assert.Fail($"The condition was still false after {Patience}.");
	}
}
