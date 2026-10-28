using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// The host-facing half of the elevated helper: an <see cref="IHardwareSensorSource"/> whose calls travel
/// down a named pipe to the process that owns the hardware.
/// </summary>
/// <remarks>
/// <para>
/// The plugin owns the pipe - see <see cref="HardwareHostSession"/> for why the roles are that way round -
/// and this type accepts the helper on it. The helper presents a 256-bit token, and it is accepted only if
/// the process on the other end is the one the launcher just started, which is what the token and the
/// process id together are for: a pipe name can be discovered, but a process id cannot be borrowed.
/// </para>
/// <para>
/// One request is outstanding at a time. The polling loop already serialises every call into the source,
/// so a second lock here is all the concurrency control the protocol needs. A request the helper refuses
/// because the hardware failed is reported as <see cref="HardwareHostException"/>; a broken pipe is an
/// ordinary <see cref="IOException"/> and is what tells the proxy to fall back to the local source.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class HardwareHostClient : IHardwareSensorSource, IAccessNoteSource
{
	private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

	private readonly PipeStream _pipe;
	private readonly ILogger _logger;
	private readonly object _gate = new();

	private bool _disposed;

	public HardwareHostClient(PipeStream pipe, ILogger logger)
	{
		_pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
		_logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<HardwareHostClient>();
	}

	/// <inheritdoc />
	public string? AccessNote { get; private set; }

	/// <summary>
	/// Waits for the helper to connect to the plugin's pipe, checks that it is the process the launcher
	/// started, and accepts its token. A helper that does not arrive, or arrives without the token, is a
	/// failed elevation rather than a silent one.
	/// </summary>
	/// <param name="pipe">The pipe the plugin created before it started the helper.</param>
	/// <param name="token">The secret both processes were given.</param>
	/// <param name="helperProcessId">The process id <see cref="System.Diagnostics.Process.Start"/> reported.</param>
	/// <param name="logger">Where a refusal is recorded.</param>
	/// <param name="cancellationToken">Cancels the wait for the helper.</param>
	/// <param name="helper">
	/// The process the launcher started, when there is one. Watching it turns a helper that died during
	/// startup into an immediate failure instead of a wait for the connection timeout.
	/// </param>
	public static async Task<HardwareHostClient> AcceptAsync(
		NamedPipeServerStream pipe,
		string token,
		int helperProcessId,
		ILogger logger,
		CancellationToken cancellationToken,
		Process? helper = null)
	{
		ArgumentNullException.ThrowIfNull(pipe);
		ArgumentNullException.ThrowIfNull(token);

		await WaitForHelperAsync(pipe, helper, cancellationToken).ConfigureAwait(false);

		// Read before judging, because the helper's write is the one thing on this pipe that can block:
		// a write to a connected pipe waits for the other end to read, so a refusal that skipped the
		// hello would leave the helper stuck in its handshake until its own timeout expired.
		var hello = await ReadHelloAsync(pipe, cancellationToken).ConfigureAwait(false);

		if (!string.Equals(hello.Type, "hello", StringComparison.Ordinal) || !TokenMatches(hello.Token, token))
		{
			throw new HardwareHostException(
				"The elevated hardware helper did not present the plugin's token.");
		}

		if (!IsExpectedHelper(pipe, helperProcessId))
		{
			throw new HardwareHostException(
				"A process other than the elevated hardware helper connected to the plugin's pipe.");
		}

		await HardwareHostProtocol
			.WriteAsync(pipe, new HardwareHostResponse { Ok = true }, cancellationToken)
			.ConfigureAwait(false);

		return new HardwareHostClient(pipe, logger);
	}

	/// <inheritdoc />
	public void Open() => AccessNote = Send(new HardwareHostRequest { Type = "open" }).AccessNote;

	/// <inheritdoc />
	public void Close() => Send(new HardwareHostRequest { Type = "close" });

	/// <inheritdoc />
	public IReadOnlyList<HardwareSensor> Read(IReadOnlyCollection<string>? hardwareFilter)
	{
		var response = Send(new HardwareHostRequest
		{
			Type = "read",
			Filter = hardwareFilter is { Count: > 0 } ? [.. hardwareFilter] : null,
		});

		return response.Sensors is { Length: > 0 } sensors
			? [.. sensors.Select(sensor => sensor.ToHardwareSensor())]
			: [];
	}

	/// <inheritdoc />
	public IReadOnlyList<SensorIdentifier> ReadControls()
	{
		var response = Send(new HardwareHostRequest { Type = "controls" });

		return response.Controls is { Length: > 0 } controls
			? [.. controls.Select(control => control.ToSensorIdentifier())]
			: [];
	}

	/// <inheritdoc />
	public bool SetControl(string sensorId, float value) =>
		Send(new HardwareHostRequest { Type = "set", SensorId = sensorId, Value = value }).Result ?? false;

	/// <inheritdoc />
	public bool ResetControl(string sensorId) =>
		Send(new HardwareHostRequest { Type = "reset", SensorId = sensorId }).Result ?? false;

	/// <summary>Asks the helper to answer, which is how a connection is known to be alive.</summary>
	public bool Ping() => Send(new HardwareHostRequest { Type = "ping" }).Ok;

	/// <inheritdoc />
	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_pipe.Dispose();
		}
	}

	/// <summary>
	/// Waits for the helper, treating a wait that ran out as a timeout rather than a cancellation. When the
	/// launcher knows the helper's process, its exit is raced against the pipe: a helper that died during
	/// startup fails at once instead of holding the UAC button for the whole connection timeout.
	/// </summary>
	private static async Task WaitForHelperAsync(
		NamedPipeServerStream pipe,
		Process? helper,
		CancellationToken cancellationToken)
	{
		using var timeout = new CancellationTokenSource(ConnectTimeout);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

		var connected = pipe.WaitForConnectionAsync(linked.Token);

		if (helper is null)
		{
			await AwaitConnectionAsync(connected, cancellationToken).ConfigureAwait(false);
			return;
		}

		// Deliberately not cancelled with the linked token: the wait outlives this method when the helper
		// connects, and cancelling it through a disposed source would fault an unobserved task.
		var exited = WatchExitAsync(helper);

		if (ReferenceEquals(await Task.WhenAny(connected, exited).ConfigureAwait(false), exited)
			&& await exited.ConfigureAwait(false))
		{
			// The connection task may still fault when the pipe is disposed; observe it so the failure
			// does not surface later as an unobserved exception.
			_ = connected.ContinueWith(
				static task => _ = task.Exception,
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);

			throw new HardwareHostException(DescribeExit(helper));
		}

		await AwaitConnectionAsync(connected, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Waits for the helper to exit, reporting false when this process cannot watch it. An unelevated
	/// plugin may not have the rights to wait on an elevated process, and that is not a reason to fail an
	/// elevation: the connection timeout still bounds the wait, exactly as it did before.
	/// </summary>
	private static async Task<bool> WatchExitAsync(Process helper)
	{
		try
		{
			await helper.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Waits for the connection, turning the timeout into a sentence rather than a cancellation.</summary>
	private static async Task AwaitConnectionAsync(Task connection, CancellationToken cancellationToken)
	{
		try
		{
			await connection.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException("The elevated hardware helper did not connect to the plugin's pipe.");
		}
	}

	/// <summary>Why a helper that never connected is gone, in the words the user's issue action shows.</summary>
	private static string DescribeExit(Process helper)
	{
		int? exitCode;
		try
		{
			exitCode = helper.ExitCode;
		}
		catch (Exception)
		{
			// An unelevated plugin may not be able to read an elevated process's exit code even after it
			// has exited. The failure is still a failure; the number is only a convenience.
			exitCode = null;
		}

		return exitCode switch
		{
			HardwareHostExitCodes.NotElevated =>
				"The elevated hardware helper was started without administrator rights.",
			{ } code =>
				$"The elevated hardware helper stopped before it connected (exit code {code}).",
			null => "The elevated hardware helper stopped before it connected.",
		};
	}

	/// <summary>Reads the helper's first message, giving up rather than waiting on a helper that never speaks.</summary>
	private static async Task<HardwareHostRequest> ReadHelloAsync(
		NamedPipeServerStream pipe,
		CancellationToken cancellationToken)
	{
		using var timeout = new CancellationTokenSource(HandshakeTimeout);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

		try
		{
			return await HardwareHostProtocol
				.ReadAsync<HardwareHostRequest>(pipe, linked.Token)
				.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidDataException)
		{
			throw new HardwareHostException(
				$"The elevated hardware helper did not identify itself: {exception.Message}");
		}
	}

	/// <summary>
	/// Whether the process on the other end is the helper this plugin started. This is the check that
	/// makes the pipe name and the token safe: a process cannot pretend to have another process's id.
	/// </summary>
	private static bool IsExpectedHelper(NamedPipeServerStream pipe, int helperProcessId)
	{
		if (helperProcessId <= 0)
		{
			return false;
		}

		if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId))
		{
			return false;
		}

		return clientProcessId == (uint)helperProcessId;
	}

	/// <summary>
	/// Compares the token without letting the comparison time say how much of it matched. The token is 256
	/// random bits, so this is defence in depth rather than the only thing standing between a stranger and
	/// the fans.
	/// </summary>
	private static bool TokenMatches(string? token, string expected) =>
		token is not null
		&& CryptographicOperations.FixedTimeEquals(
			Encoding.UTF8.GetBytes(token),
			Encoding.UTF8.GetBytes(expected));

	private HardwareHostResponse Send(HardwareHostRequest request)
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);

			try
			{
				HardwareHostProtocol
					.WriteAsync(_pipe, request, CancellationToken.None)
					.GetAwaiter()
					.GetResult();

				var response = HardwareHostProtocol
					.ReadAsync<HardwareHostResponse>(_pipe, CancellationToken.None)
					.GetAwaiter()
					.GetResult();

				if (!response.Ok)
				{
					throw new HardwareHostException(
						response.Error ?? "The elevated hardware helper refused the request.");
				}

				return response;
			}
			catch (Exception exception) when (exception is not HardwareHostException)
			{
				_logger.Debug(exception, "The elevated hardware helper did not answer {RequestType}.", request.Type);
				throw;
			}
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}

/// <summary>The helper answered, and the answer was a refusal - a hardware failure, not a broken pipe.</summary>
internal sealed class HardwareHostException : Exception
{
	public HardwareHostException(string message)
		: base(message)
	{
	}
}
