using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// The elevated copy's end of the pipe: it connects to the plugin that started it, checks that the pipe
/// belongs to that plugin, and answers requests until the plugin disconnects.
/// </summary>
/// <remarks>
/// <para>
/// The plugin creates the pipe, not the helper, and that is the point rather than an accident. A pipe
/// created by an elevated process carries the elevated token's integrity level and an
/// <c>Administrators</c> owner, and an unelevated client is refused write access to it however its DACL
/// reads; <c>PipeOptions.CurrentUserOnly</c> refuses it outright, because on Windows it compares elevation
/// as well as account. A pipe created by the plugin is at the user's own level, which the elevated helper
/// can open, so the transport roles are the other way round from the obvious ones.
/// </para>
/// <para>
/// The helper is therefore the pipe's client and the protocol's server: it presents the token, the plugin
/// answers only after checking the token and the helper's process id, and every later message is a request
/// from the plugin and an answer from here. The helper also checks that the pipe's server is the process
/// that started it, so a pipe that appeared under the same name is not served.
/// </para>
/// <para>
/// The pipe closing ends the helper, and it also watches the plugin process, so a crashed plugin cannot
/// leave an elevated process behind. The job object the plugin assigns it to is a third line of defence,
/// not the only one - an unelevated process cannot always get the handle rights to assign an elevated one.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class HardwareHostSession
{
	/// <summary>How long the helper waits for the plugin's pipe to accept it.</summary>
	private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);

	/// <summary>How long the helper waits for the plugin to answer its handshake.</summary>
	private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

	private readonly string _pipeName;
	private readonly string _token;
	private readonly int _parentProcessId;
	private readonly HardwareHostHandler _handler;
	private readonly ILogger _logger;

	public HardwareHostSession(
		string pipeName,
		string token,
		int parentProcessId,
		HardwareHostHandler handler,
		ILogger logger)
	{
		_pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
		_token = token ?? throw new ArgumentNullException(nameof(token));
		_parentProcessId = parentProcessId;
		_handler = handler ?? throw new ArgumentNullException(nameof(handler));
		_logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<HardwareHostSession>();
	}

	/// <summary>Runs until the plugin disconnects, asks the helper to stop, or the parent process exits.</summary>
	public async Task<int> RunAsync(CancellationToken cancellationToken)
	{
		using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var watching = WatchParentAsync(stopping);

		try
		{
			using var pipe = new NamedPipeClientStream(
				".",
				_pipeName,
				PipeDirection.InOut,
				PipeOptions.Asynchronous);

			if (!await ConnectAsync(pipe, stopping.Token).ConfigureAwait(false))
			{
				return HardwareHostExitCodes.NoPipe;
			}

			if (!IsStartedByParent(pipe))
			{
				_logger.Warning("The elevated hardware helper found a pipe its plugin did not create.");
				return HardwareHostExitCodes.Refused;
			}

			if (!await HandshakeAsync(pipe, stopping.Token).ConfigureAwait(false))
			{
				_logger.Warning("The plugin refused the elevated hardware helper's handshake.");
				return HardwareHostExitCodes.Refused;
			}

			_logger.Information("The elevated hardware helper is serving the plugin.");
			await ServeAsync(pipe, stopping.Token).ConfigureAwait(false);
			return HardwareHostExitCodes.Served;
		}
		catch (OperationCanceledException)
		{
			// The parent exited, the plugin disconnected, or the process is shutting down.
			return HardwareHostExitCodes.Served;
		}
		catch (Exception exception)
		{
			_logger.Error(exception, "The elevated hardware helper stopped unexpectedly.");
			return HardwareHostExitCodes.Failed;
		}
		finally
		{
			await stopping.CancelAsync().ConfigureAwait(false);
			try
			{
				await watching.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// The helper is stopping; the watcher has nothing left to wait for.
			}

			_handler.Dispose();
		}
	}

	/// <summary>Connects to the plugin's pipe, giving up when the parent process goes away or the timeout passes.</summary>
	private async Task<bool> ConnectAsync(NamedPipeClientStream pipe, CancellationToken stopping)
	{
		using var timeout = new CancellationTokenSource(ConnectionTimeout);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, timeout.Token);

		try
		{
			await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
			return true;
		}
		catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
		{
			_logger.Warning("The plugin did not open the hardware helper's pipe within {Timeout}.", ConnectionTimeout);
			return false;
		}
		catch (IOException exception) when (!stopping.IsCancellationRequested)
		{
			_logger.Warning(exception, "The plugin's hardware helper pipe could not be opened.");
			return false;
		}
	}

	/// <summary>Presents the token and waits for the plugin to accept it.</summary>
	private async Task<bool> HandshakeAsync(NamedPipeClientStream pipe, CancellationToken stopping)
	{
		using var timeout = new CancellationTokenSource(HandshakeTimeout);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, timeout.Token);

		try
		{
			await HardwareHostProtocol
				.WriteAsync(pipe, new HardwareHostRequest { Type = "hello", Token = _token }, linked.Token)
				.ConfigureAwait(false);

			var answer = await HardwareHostProtocol
				.ReadAsync<HardwareHostResponse>(pipe, linked.Token)
				.ConfigureAwait(false);

			return answer.Ok;
		}
		catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidDataException)
		{
			_logger.Warning(exception, "The elevated hardware helper did not receive a usable handshake answer.");
			return false;
		}
	}

	/// <summary>Answers requests until the client closes the pipe or asks the helper to stop.</summary>
	private async Task ServeAsync(NamedPipeClientStream pipe, CancellationToken stopping)
	{
		while (!stopping.IsCancellationRequested)
		{
			HardwareHostRequest request;
			try
			{
				request = await HardwareHostProtocol
					.ReadAsync<HardwareHostRequest>(pipe, stopping)
					.ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is EndOfStreamException or IOException)
			{
				_logger.Debug(exception, "The plugin closed the hardware helper's pipe.");
				return;
			}

			if (string.Equals(request.Type, "stop", StringComparison.Ordinal))
			{
				await HardwareHostProtocol
					.WriteAsync(pipe, new HardwareHostResponse { Ok = true }, stopping)
					.ConfigureAwait(false);
				return;
			}

			var response = _handler.Handle(request);
			await HardwareHostProtocol.WriteAsync(pipe, response, stopping).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Whether the pipe was created by the plugin this helper was started for. The plugin created it before
	/// launching the helper, so the name cannot have been taken by anything else, and this check is what
	/// says so out loud rather than trusting the command line.
	/// </summary>
	private bool IsStartedByParent(NamedPipeClientStream pipe)
	{
		if (_parentProcessId <= 0)
		{
			return true;
		}

		if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcessId))
		{
			return false;
		}

		return serverProcessId == (uint)_parentProcessId;
	}

	/// <summary>Exits when the plugin does, so a crashed plugin cannot leave an elevated helper running.</summary>
	private async Task WatchParentAsync(CancellationTokenSource stopping)
	{
		if (_parentProcessId <= 0)
		{
			return;
		}

		try
		{
			using var parent = Process.GetProcessById(_parentProcessId);
			await parent.WaitForExitAsync(stopping.Token).ConfigureAwait(false);
		}
		catch (ArgumentException)
		{
			// The plugin was already gone when the helper started.
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception exception)
		{
			// Not being able to watch is not a reason to refuse to serve; the pipe disconnect still
			// ends the helper.
			_logger.Debug(exception, "The elevated hardware helper could not watch its parent process.");
			return;
		}

		await stopping.CancelAsync().ConfigureAwait(false);
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
