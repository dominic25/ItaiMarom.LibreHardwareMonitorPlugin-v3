using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The swap between the local hardware and the elevated helper: the part of elevation that can be tested
/// without a UAC prompt.
/// </summary>
/// <remarks>
/// The launcher is the seam. A test hands back a connection built around a fake source, so what is under
/// test is the order of the swap - close local, open helper, fall back if it cannot - rather than the
/// Windows call that starts the process.
/// </remarks>
[TestFixture]
public sealed class ElevatedHardwareSourceTests
{
	[Test]
	public async Task The_plugin_starts_local_and_moves_to_the_helper_when_it_is_enabled()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var local = LocalSource();
		var remote = RemoteSource();
		var launcher = new FakeHardwareHostLauncher
		{
			Result = HardwareHostLaunchResult.ConnectedTo(Connection(remote, logger)),
		};
		using var source = new ElevatedHardwareSource(local, launcher, logger);

		source.Open();
		Assert.That(ReadHardware(source), Is.EqualTo("CPU"));
		Assert.That(source.IsElevated, Is.False);

		var result = await source.EnableAsync();

		Assert.That(result.Outcome, Is.EqualTo(ElevationOutcome.Enabled));
		Assert.That(source.IsElevated, Is.True);

		// The local hardware had to be released before the helper opened the same driver, and every
		// read after the swap comes from the helper.
		Assert.That(local.CloseCount, Is.EqualTo(1));
		Assert.That(ReadHardware(source), Is.EqualTo("GPU"));
		Assert.That(remote.ReadCount, Is.GreaterThanOrEqualTo(1));
	}

	[Test]
	public async Task A_dismissed_prompt_keeps_the_local_readings()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var local = LocalSource();
		var launcher = new FakeHardwareHostLauncher
		{
			Result = HardwareHostLaunchResult.CancelledByUser,
		};
		using var source = new ElevatedHardwareSource(local, launcher, logger);

		source.Open();
		var result = await source.EnableAsync();

		Assert.That(result.Outcome, Is.EqualTo(ElevationOutcome.Cancelled));
		Assert.That(source.IsElevated, Is.False);
		Assert.That(ReadHardware(source), Is.EqualTo("CPU"));
		Assert.That(launcher.LaunchCount, Is.EqualTo(1));
	}

	[Test]
	public async Task A_helper_that_cannot_open_the_hardware_leaves_the_local_source_running()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var local = LocalSource();
		var remote = RemoteSource();
		remote.OnOpen = () => throw new InvalidOperationException("the driver would not open");
		var launcher = new FakeHardwareHostLauncher
		{
			Result = HardwareHostLaunchResult.ConnectedTo(Connection(remote, logger)),
		};
		using var source = new ElevatedHardwareSource(local, launcher, logger);

		source.Open();
		var result = await source.EnableAsync();

		// A failed elevation is not a failed plugin: the local source is reopened so the readings that
		// do not need the driver keep moving.
		Assert.That(result.Outcome, Is.EqualTo(ElevationOutcome.Failed));
		Assert.That(result.Error, Is.EqualTo("the driver would not open"));
		Assert.That(source.IsElevated, Is.False);
		Assert.That(ReadHardware(source), Is.EqualTo("CPU"));
		Assert.That(local.OpenCount, Is.EqualTo(2));
	}

	[Test]
	public async Task A_broken_pipe_falls_back_to_the_local_readings()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var local = LocalSource();
		var remote = RemoteSource();
		var launcher = new FakeHardwareHostLauncher
		{
			Result = HardwareHostLaunchResult.ConnectedTo(Connection(remote, logger)),
		};
		using var source = new ElevatedHardwareSource(local, launcher, logger);

		source.Open();
		await source.EnableAsync();
		Assert.That(source.IsElevated, Is.True);

		remote.OnRead = () => throw new IOException("the helper went away");

		// A helper that dies is not a dead plugin: the next read comes from the local source, and the
		// issue list goes back to offering elevation.
		Assert.That(ReadHardware(source), Is.EqualTo("CPU"));
		Assert.That(source.IsElevated, Is.False);
	}

	[Test]
	public async Task The_helpers_access_note_is_carried_to_the_issue()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var local = LocalSource();
		var remote = RemoteSource();
		remote.AccessNote = "the driver still will not open";
		var launcher = new FakeHardwareHostLauncher
		{
			Result = HardwareHostLaunchResult.ConnectedTo(Connection(remote, logger)),
		};
		using var source = new ElevatedHardwareSource(local, launcher, logger);

		source.Open();
		var result = await source.EnableAsync();

		// The helper is the process that can say what is still missing; this process cannot answer that
		// for it, so the note travels back with the switch.
		Assert.That(result.AccessNote, Is.EqualTo("the driver still will not open"));
		Assert.That(source.ElevatedAccessNote, Is.EqualTo("the driver still will not open"));
	}

	[Test]
	public async Task An_elevated_helpers_answer_wins_over_this_process_s_own()
	{
		var source = new FakeElevatableSource
		{
			NextResult = new ElevationResult(ElevationOutcome.Enabled, AccessNote: "the driver still will not open"),
		};

		await source.EnableAsync();
		var diagnosis = new KernelAccessDiagnosis(source).Diagnose();

		// This process cannot open the driver whether the helper can or not, so asking it would always
		// report the readings as missing. The helper is the process that knows.
		Assert.That(diagnosis.Note, Is.EqualTo("the driver still will not open"));
		Assert.That(diagnosis.CanElevate, Is.False);
	}

	[Test]
	public async Task Disposing_the_source_closes_the_helper_and_the_local_hardware()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var local = LocalSource();
		var remote = RemoteSource();
		var launcher = new FakeHardwareHostLauncher
		{
			Result = HardwareHostLaunchResult.ConnectedTo(Connection(remote, logger)),
		};
		var source = new ElevatedHardwareSource(local, launcher, logger);

		source.Open();
		await source.EnableAsync();
		source.Dispose();

		Assert.That(remote.CloseCount, Is.GreaterThanOrEqualTo(1));
		Assert.That(local.CloseCount, Is.GreaterThanOrEqualTo(1));
	}

	private static FakeHardwareSensorSource LocalSource() =>
		new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 10f);

	private static FakeHardwareSensorSource RemoteSource() =>
		new FakeHardwareSensorSource().Add("GPU", "GPU Core", SensorType.Temperature, 55f);

	private static HardwareHostConnection Connection(IHardwareSensorSource remote, ILogger logger) =>
		new(remote, process: null, job: null, logger);

	private static string ReadHardware(ElevatedHardwareSource source) =>
		source.Read(null).Single().Sensor.RootHardwareName;
}
