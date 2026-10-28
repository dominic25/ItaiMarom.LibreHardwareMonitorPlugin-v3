using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The plugin's side of the pipe: who it accepts, and what it can ask once it has.
/// </summary>
/// <remarks>
/// The pipe is created by the plugin and the elevated helper connects to it - the other way round from the
/// obvious design, because a pipe created by an elevated process refuses an unelevated client. That part
/// cannot be tested without elevation, but the acceptance rules and the request direction can: both ends
/// run in this process, and the process id the launcher would report is this one.
/// </remarks>
[TestFixture]
[SupportedOSPlatform("windows")]
public sealed class HardwareHostHandshakeTests
{
	private const string Token = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

	[Test]
	public async Task The_plugin_accepts_the_helper_it_started_and_then_asks_it_questions()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var (server, helper) = CreatePair();

		var accepted = HardwareHostClient.AcceptAsync(
			server,
			Token,
			Environment.ProcessId,
			logger,
			CancellationToken.None);

		await HelloAsync(helper, Token);
		Assert.That(
			(await HardwareHostProtocol.ReadAsync<HardwareHostResponse>(helper, CancellationToken.None)).Ok,
			Is.True);

		using var client = await accepted;

		// The plugin asks; the helper answers. Ping is the one request that needs no hardware.
		var pending = HardwareHostProtocol.ReadAsync<HardwareHostRequest>(helper, CancellationToken.None);
		var ping = Task.Run(client.Ping);
		var request = await pending;
		await HardwareHostProtocol.WriteAsync(
			helper,
			new HardwareHostResponse { Ok = true },
			CancellationToken.None);

		Assert.That(request.Type, Is.EqualTo("ping"));
		Assert.That(await ping, Is.True);
	}

	[Test]
	public async Task A_helper_that_does_not_present_the_token_is_refused()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var (server, helper) = CreatePair();

		var accepted = HardwareHostClient.AcceptAsync(
			server,
			Token,
			Environment.ProcessId,
			logger,
			CancellationToken.None);

		await HelloAsync(helper, "somebody-elses-token");

		// The name is discoverable, so the token is what a stranger would have to guess.
		Assert.That(
			async () => await accepted,
			Throws.TypeOf<HardwareHostException>());
	}

	[Test]
	public async Task A_client_that_is_not_the_helper_process_is_refused()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var (server, helper) = CreatePair();

		var accepted = HardwareHostClient.AcceptAsync(
			server,
			Token,
			helperProcessId: int.MaxValue,
			logger,
			CancellationToken.None);

		await HelloAsync(helper, Token);

		// The client here is this test process, not the one the launcher reported.
		Assert.That(
			async () => await accepted,
			Throws.TypeOf<HardwareHostException>());
	}

	[Test]
	public async Task A_helper_that_exits_before_connecting_fails_at_once()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var (server, helperEnd) = CreatePair();
		using (server)
		using (helperEnd)
		{
			using var helper = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 7")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
			})!;

			var stopwatch = Stopwatch.StartNew();
			var accepted = HardwareHostClient.AcceptAsync(
				server,
				Token,
				helper.Id,
				logger,
				CancellationToken.None,
				helper);

			// The connection timeout is 30 seconds: a helper that died during startup has to be reported
			// from its exit, not by making the user wait out a pipe that will never connect.
			Assert.That(
				async () => await accepted,
				Throws.TypeOf<HardwareHostException>().With.Message.Contains("exit code 7"));
			Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
		}
	}

	/// <summary>The plugin's end and the helper's end of one pipe, both in this process.</summary>
	private static (NamedPipeServerStream Server, NamedPipeClientStream Helper) CreatePair()
	{
		var pipeName = "libre-hardware-monitor-test-" + Guid.NewGuid().ToString("N");
		var server = new NamedPipeServerStream(
			pipeName,
			PipeDirection.InOut,
			maxNumberOfServerInstances: 1,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous);

		var helper = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

		// Both ends are disposed by the test when it ends; there is no other owner to hand them to.
		return (server, helper);
	}

	/// <summary>Connects as the helper would and presents the token.</summary>
	private static async Task HelloAsync(NamedPipeClientStream helper, string token)
	{
		await helper.ConnectAsync(10_000);
		await HardwareHostProtocol.WriteAsync(
			helper,
			new HardwareHostRequest { Type = "hello", Token = token },
			CancellationToken.None);
	}
}
