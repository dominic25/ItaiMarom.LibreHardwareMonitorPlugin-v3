using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The source the polling loop sees when remote machines are configured: this machine's hardware plus
/// every remote one, with a failing remote unable to take the rest down.
/// </summary>
[TestFixture]
public sealed class AggregateHardwareSourceTests
{
	private static readonly RemoteSourceDefinition LivingRoom =
		new("Living Room", "http://living-room-pc:8085/data.json", "living_room");

	private static readonly RemoteSourceDefinition Office =
		new("Office PC", "http://office-pc:8085/data.json", "office_pc");

	private static readonly SensorIdentifier RemoteSensor = Remote("living_room", "CPU", "CPU Total");

	[Test]
	public void Reads_come_back_from_the_local_machine_and_every_remote()
	{
		var local = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		using var aggregate = new AggregateHardwareSource(
			local,
			logger,
			_ => new StubRemote(new HardwareSensor(RemoteSensor, 55f)));

		aggregate.ReplaceRemoteSources([LivingRoom]);

		var sensors = aggregate.Read(null);

		Assert.That(sensors.Select(sensor => sensor.Sensor), Does.Contain(local.Sensors[0]));
		Assert.That(sensors.Select(sensor => sensor.Sensor), Does.Contain(RemoteSensor));
	}

	[Test]
	public void A_failing_remote_does_not_remove_the_local_readings()
	{
		var local = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		using var aggregate = new AggregateHardwareSource(
			local,
			logger,
			_ => new StubRemote { Failure = new HttpRequestException("unreachable") });

		aggregate.ReplaceRemoteSources([LivingRoom]);

		var sensors = aggregate.Read(null);

		Assert.That(sensors.Select(sensor => sensor.Sensor), Is.EqualTo(local.Sensors));
	}

	[Test]
	public void One_failing_remote_does_not_remove_the_other()
	{
		var local = new FakeHardwareSensorSource();
		var working = new StubRemote(new HardwareSensor(RemoteSensor, 55f));
		using var logger = new LoggerConfiguration().CreateLogger();
		using var aggregate = new AggregateHardwareSource(
			local,
			logger,
			definition => definition == LivingRoom
				? new StubRemote { Failure = new InvalidOperationException("boom") }
				: working);

		aggregate.ReplaceRemoteSources([LivingRoom, Office]);

		Assert.That(aggregate.Read(null).Select(sensor => sensor.Sensor), Does.Contain(RemoteSensor));
		Assert.That(working.ReadCount, Is.EqualTo(1));
	}

	[Test]
	public void A_remote_that_cannot_even_be_constructed_is_skipped()
	{
		var local = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		using var logger = new LoggerConfiguration().CreateLogger();
		using var aggregate = new AggregateHardwareSource(
			local,
			logger,
			_ => throw new InvalidOperationException("bad address"));

		aggregate.ReplaceRemoteSources([LivingRoom]);

		Assert.That(aggregate.Read(null).Select(sensor => sensor.Sensor), Is.EqualTo(local.Sensors));
	}

	[Test]
	public void Replacing_the_remote_list_disposes_the_ones_that_went_away()
	{
		var local = new FakeHardwareSensorSource();
		var old = new StubRemote();
		var replacement = new StubRemote();
		var current = old;
		using var logger = new LoggerConfiguration().CreateLogger();
		using var aggregate = new AggregateHardwareSource(local, logger, _ => current);

		aggregate.ReplaceRemoteSources([LivingRoom]);
		current = replacement;
		aggregate.ReplaceRemoteSources([Office]);

		Assert.That(old.Disposed, Is.True);
		Assert.That(replacement.Disposed, Is.False);
	}

	[Test]
	public void Controls_are_this_machine_s_business_only()
	{
		var fan = TestFanActions.Control("Fan #1") with { Controllable = true };
		var local = new FakeHardwareSensorSource().AddControl(fan, value: 50f);
		var remote = new StubRemote { Controls = [RemoteSensor] };
		using var logger = new LoggerConfiguration().CreateLogger();
		using var aggregate = new AggregateHardwareSource(local, logger, _ => remote);

		aggregate.ReplaceRemoteSources([LivingRoom]);

		Assert.That(aggregate.ReadControls(), Is.EqualTo(new[] { fan }));
		Assert.That(aggregate.SetControl(fan.Id, 40), Is.True);
		Assert.That(local.LastSetSensorId, Is.EqualTo(fan.Id));
		Assert.That(aggregate.ResetControl(fan.Id), Is.True);
	}

	[Test]
	public void The_local_access_note_is_forwarded()
	{
		var local = new FakeHardwareSensorSource { AccessNote = "some readings are missing" };
		using var logger = new LoggerConfiguration().CreateLogger();
		using var aggregate = new AggregateHardwareSource(local, logger);

		Assert.That(aggregate.AccessNote, Is.EqualTo("some readings are missing"));
	}

	private static SensorIdentifier Remote(string source, string hardware, string sensor) =>
		new(
			HardwareLocation.Root($"/remote/{source}/{VariableNaming.ToDeclaredId(hardware)}", $"{source} · {hardware}", source)
				.Child($"/remote/{source}/{VariableNaming.ToDeclaredId(hardware)}/0", hardware),
			$"/remote/{source}/{VariableNaming.ToDeclaredId(hardware)}/load/0",
			sensor,
			SensorType.Load);

	private sealed class StubRemote : IHardwareSensorSource
	{
		private readonly IReadOnlyList<HardwareSensor> _sensors;

		public StubRemote(params HardwareSensor[] sensors) => _sensors = sensors;

		public Exception? Failure { get; init; }

		public IReadOnlyList<SensorIdentifier> Controls { get; init; } = [];

		public int ReadCount { get; private set; }

		public bool Disposed { get; private set; }

		public void Open()
		{
		}

		public void Close()
		{
		}

		public IReadOnlyList<HardwareSensor> Read(IReadOnlyCollection<string>? hardwareFilter)
		{
			ReadCount++;
			if (Failure is { } failure)
			{
				throw failure;
			}

			return _sensors;
		}

		public IReadOnlyList<SensorIdentifier> ReadControls() => Controls;

		public bool SetControl(string sensorId, float value) => false;

		public bool ResetControl(string sensorId) => false;

		public void Dispose() => Disposed = true;
	}
}
