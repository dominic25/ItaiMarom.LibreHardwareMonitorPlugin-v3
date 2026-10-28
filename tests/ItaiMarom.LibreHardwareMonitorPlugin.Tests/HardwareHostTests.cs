using System.Buffers.Binary;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The elevated helper's contract without the helper: the command line it is started with, the framing on
/// its pipe, and the requests it carries out against a hardware source.
/// </summary>
/// <remarks>
/// A test cannot start an elevated process, and it should not need to: the part that can be wrong in a way
/// a user would notice is the protocol and the handler behind it, not the Windows call that raises UAC.
/// </remarks>
[TestFixture]
public sealed class HardwareHostTests
{
	private static readonly string[] CpuAndGpu = ["CPU", "GPU"];

	[Test]
	public void The_helper_arguments_round_trip()
	{
		var args = new[]
		{
			HardwareHostOptions.Flag,
			"--pipe", "libre-hardware-monitor-abc",
			"--token", "0123456789ABCDEF",
			"--parent", "4242",
		};

		Assert.That(HardwareHostOptions.TryParse(args, out var options), Is.True);
		Assert.That(options, Is.Not.Null);
		Assert.That(options!.PipeName, Is.EqualTo("libre-hardware-monitor-abc"));
		Assert.That(options.Token, Is.EqualTo("0123456789ABCDEF"));
		Assert.That(options.ParentProcessId, Is.EqualTo(4242));
	}

	[Test]
	public void The_plugin_does_not_claim_arguments_that_are_not_its_own()
	{
		Assert.That(HardwareHostOptions.IsRequested(["--some-plugin-flag"]), Is.False);
		Assert.That(HardwareHostOptions.TryParse(["--some-plugin-flag"], out var options), Is.False);
		Assert.That(options, Is.Null);
	}

	[Test]
	public void A_helper_without_its_arguments_is_refused_rather_than_listening_somewhere_unexpected()
	{
		Assert.That(HardwareHostOptions.TryParse([HardwareHostOptions.Flag], out var options), Is.False);
		Assert.That(options, Is.Null);

		Assert.That(
			HardwareHostOptions.TryParse(
				[HardwareHostOptions.Flag, "--pipe", "p", "--token", "t", "--parent", "not-a-pid"],
				out options),
			Is.False);
		Assert.That(options, Is.Null);
	}

	[Test]
	public async Task A_request_round_trips_through_the_pipe_framing()
	{
		using var stream = new MemoryStream();
		var request = new HardwareHostRequest
		{
			Type = "read",
			Filter = CpuAndGpu,
		};

		await HardwareHostProtocol.WriteAsync(stream, request, CancellationToken.None);
		stream.Position = 0;

		var read = await HardwareHostProtocol.ReadAsync<HardwareHostRequest>(
			stream,
			CancellationToken.None);

		Assert.That(read.Type, Is.EqualTo("read"));
		Assert.That(read.Filter, Is.EqualTo(CpuAndGpu));
	}

	[Test]
	public async Task A_response_round_trips_with_its_sensors_and_controls()
	{
		var sensor = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);
		var response = new HardwareHostResponse
		{
			Ok = true,
			Sensors =
			[
				new HardwareSensorDto(
					SensorIdentifierDto.From(sensor),
					54.5f,
					new ControlRangeDto(0f, 100f)),
			],
			Controls = [SensorIdentifierDto.From(sensor with { Controllable = true })],
		};

		using var stream = new MemoryStream();
		await HardwareHostProtocol.WriteAsync(stream, response, CancellationToken.None);
		stream.Position = 0;

		var read = await HardwareHostProtocol.ReadAsync<HardwareHostResponse>(
			stream,
			CancellationToken.None);

		Assert.That(read.Ok, Is.True);
		Assert.That(read.Sensors, Has.Length.EqualTo(1));
		Assert.That(read.Sensors![0].Value, Is.EqualTo(54.5f));
		Assert.That(read.Sensors[0].Control, Is.EqualTo(new ControlRangeDto(0f, 100f)));
		Assert.That(read.Sensors[0].ToHardwareSensor().Sensor.SensorType, Is.EqualTo(SensorType.Temperature));
		Assert.That(read.Controls, Has.Length.EqualTo(1));
		Assert.That(read.Controls![0].Controllable, Is.True);
	}

	[TestCase(float.NaN)]
	[TestCase(float.PositiveInfinity)]
	[TestCase(float.NegativeInfinity)]
	public void A_non_finite_reading_travels_as_no_reading(float value)
	{
		var sensor = TestSensor.Create("CPU", "CPU Package", SensorType.Temperature);

		var dto = HardwareSensorDto.From(new HardwareSensor(sensor, value));

		// The library reports NaN for a sensor with nothing to say, which is what a null sample value
		// already means here - and a non-finite number cannot be written as JSON at all.
		Assert.That(dto.Value, Is.Null);
		Assert.That(dto.ToHardwareSensor().Value, Is.Null);
	}

	[Test]
	public async Task A_reading_the_library_could_not_describe_is_answered_rather_than_killing_the_helper()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Package", SensorType.Temperature, float.NaN);
		using var handler = new HardwareHostHandler(source, logger);

		var read = handler.Handle(new HardwareHostRequest { Type = "read" });

		// This is the shape of the bug that dropped the elevated helper whenever the sensor window was
		// opened: a full read reached a sensor the library described as NaN, serialising the answer threw,
		// and the helper exited - so the plugin fell back to the readings it cannot reach.
		using var stream = new MemoryStream();
		await HardwareHostProtocol.WriteAsync(stream, read, CancellationToken.None);
		stream.Position = 0;

		var answered = await HardwareHostProtocol.ReadAsync<HardwareHostResponse>(
			stream,
			CancellationToken.None);

		Assert.That(answered.Ok, Is.True);
		Assert.That(answered.Sensors, Has.Length.EqualTo(1));
		Assert.That(answered.Sensors![0].Value, Is.Null);
	}

	[Test]
	public void A_message_length_that_cannot_be_a_message_is_a_protocol_failure()
	{
		using var stream = new MemoryStream();
		var length = new byte[sizeof(int)];
		BinaryPrimitives.WriteInt32LittleEndian(length, -1);
		stream.Write(length);
		stream.Position = 0;

		Assert.That(
			async () => await HardwareHostProtocol.ReadAsync<HardwareHostRequest>(
				stream,
				CancellationToken.None),
			Throws.TypeOf<InvalidDataException>());
	}

	[Test]
	public void The_handler_opens_reads_and_answers_with_the_access_note()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		using var handler = new HardwareHostHandler(source, logger, () => "still missing");

		var opened = handler.Handle(new HardwareHostRequest { Type = "open" });
		var read = handler.Handle(new HardwareHostRequest { Type = "read" });

		Assert.That(opened.Ok, Is.True);
		Assert.That(opened.AccessNote, Is.EqualTo("still missing"));
		Assert.That(read.Ok, Is.True);
		Assert.That(read.Sensors, Has.Length.EqualTo(1));
		Assert.That(read.Sensors![0].Sensor.SensorName, Is.EqualTo("CPU Total"));
		Assert.That(read.Sensors[0].Value, Is.EqualTo(42f));
	}

	[Test]
	public void The_handler_writes_and_restores_a_control()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var fan = TestFanActions.Control("Fan #1") with { Controllable = true };
		var source = new FakeHardwareSensorSource().AddControl(fan, value: 25f);
		using var handler = new HardwareHostHandler(source, logger);

		var set = handler.Handle(new HardwareHostRequest
		{
			Type = "set",
			SensorId = fan.Id,
			Value = 60f,
		});
		var reset = handler.Handle(new HardwareHostRequest
		{
			Type = "reset",
			SensorId = fan.Id,
		});
		var controls = handler.Handle(new HardwareHostRequest { Type = "controls" });

		Assert.That(set.Ok, Is.True);
		Assert.That(set.Result, Is.True);
		Assert.That(source.LastSetValue, Is.EqualTo(60f));

		Assert.That(reset.Ok, Is.True);
		Assert.That(reset.Result, Is.True);
		Assert.That(source.LastResetSensorId, Is.EqualTo(fan.Id));

		Assert.That(controls.Controls, Has.Length.EqualTo(1));
	}

	[Test]
	public void A_refused_request_is_an_answer_rather_than_a_broken_pipe()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource();
		using var handler = new HardwareHostHandler(source, logger);

		var unknown = handler.Handle(new HardwareHostRequest { Type = "somebody-elses-request" });
		var noSensor = handler.Handle(new HardwareHostRequest { Type = "set", Value = 50f });

		// The helper stays alive: one request the plugin got wrong is a result for that request, and the
		// next poll is allowed to try again.
		Assert.That(unknown.Ok, Is.False);
		Assert.That(unknown.Error, Does.Contain("somebody-elses-request"));
		Assert.That(noSensor.Ok, Is.False);
		Assert.That(noSensor.Error, Does.Contain("sensor"));
	}

	[Test]
	public void A_hardware_failure_is_reported_without_taking_the_helper_down()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		source.OnRead = () => throw new IOException("the device vanished");
		using var handler = new HardwareHostHandler(source, logger);

		var read = handler.Handle(new HardwareHostRequest { Type = "read" });

		Assert.That(read.Ok, Is.False);
		Assert.That(read.Error, Is.EqualTo("the device vanished"));
	}
}
