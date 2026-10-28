using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>What happened when the plugin asked Windows to start its elevated helper.</summary>
internal enum HardwareHostLaunchOutcome
{
	Connected,
	Cancelled,
	Failed,
}

/// <summary>The result of one attempt to start and connect to the elevated helper.</summary>
internal readonly record struct HardwareHostLaunchResult(
	HardwareHostLaunchOutcome Outcome,
	HardwareHostConnection? Connection = null,
	string? Error = null)
{
	public static HardwareHostLaunchResult ConnectedTo(HardwareHostConnection connection) =>
		new(HardwareHostLaunchOutcome.Connected, connection);

	public static HardwareHostLaunchResult CancelledByUser =>
		new(HardwareHostLaunchOutcome.Cancelled);

	public static HardwareHostLaunchResult FailedBecause(string error) =>
		new(HardwareHostLaunchOutcome.Failed, Error: error);
}

/// <summary>
/// Starts the same plugin binary as an elevated hardware helper and connects to it.
/// </summary>
internal interface IHardwareHostLauncher
{
	Task<HardwareHostLaunchResult> LaunchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The helper process and the pipe client that talks to it. Disposing it closes the pipe - which makes the
/// helper exit - and then waits for the process, killing it if it does not go.
/// </summary>
internal sealed class HardwareHostConnection : IDisposable
{
	private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(2);

	private readonly Process? _process;
	private readonly JobObject? _job;
	private readonly ILogger _logger;
	private bool _disposed;

	public HardwareHostConnection(
		IHardwareSensorSource source,
		Process? process,
		JobObject? job,
		ILogger logger)
	{
		Source = source ?? throw new ArgumentNullException(nameof(source));
		_process = process;
		_job = job;
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	public IHardwareSensorSource Source { get; }

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;

		// The pipe first: the helper sees the disconnect and leaves on its own. The process handling
		// below is for the helper that does not, and the job object is the last resort.
		try
		{
			Source.Dispose();
		}
		catch (Exception exception)
		{
			_logger.Debug(exception, "The elevated hardware helper's pipe did not close cleanly.");
		}

		if (_process is not null)
		{
			try
			{
				if (!_process.HasExited && !_process.WaitForExit(ExitTimeout))
				{
					_process.Kill(entireProcessTree: true);
					_process.WaitForExit(ExitTimeout);
				}
			}
			catch (Exception exception)
			{
				// The helper watches the plugin process, so an unelevated plugin that cannot kill an
				// elevated process still does not leave it running.
				_logger.Debug(exception, "The elevated hardware helper could not be waited for.");
			}
			finally
			{
				_process.Dispose();
			}
		}

		if (OperatingSystem.IsWindows())
		{
			_job?.Dispose();
		}
	}
}

/// <summary>
/// The Windows launcher: the apphost is started with <c>Verb = "runas"</c>, so the UAC dialog is the
/// prompt the user sees. A dismissed prompt is <see cref="HardwareHostLaunchOutcome.Cancelled"/> and the
/// plugin keeps the readings it already has.
/// </summary>
/// <remarks>
/// The package ships the apphost so the UAC dialog names the plugin instead of <c>dotnet</c>. A developer
/// run that started this process through the CLI has no apphost to relaunch, so the launcher falls back to
/// the same <c>dotnet</c> and assembly; the helper is the same code either way.
/// </remarks>
internal sealed class WindowsHardwareHostLauncher : IHardwareHostLauncher
{
	/// <summary>Windows' <c>ERROR_CANCELLED</c>, which is what a dismissed UAC prompt returns.</summary>
	private const int ErrorCancelled = 1223;

	private readonly ILogger _logger;

	public WindowsHardwareHostLauncher(ILogger logger) =>
		_logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<WindowsHardwareHostLauncher>();

	/// <inheritdoc />
	public async Task<HardwareHostLaunchResult> LaunchAsync(CancellationToken cancellationToken)
	{
		if (!OperatingSystem.IsWindows())
		{
			return HardwareHostLaunchResult.FailedBecause(
				"Elevated readings are only available on Windows.");
		}

		var pipeName = "libre-hardware-monitor-" + Guid.NewGuid().ToString("N");
		var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

		Process? process = null;
		JobObject? job = null;
		NamedPipeServerStream? pipe = null;

		try
		{
			// The pipe belongs to this process, not the helper. A pipe created by an elevated process is
			// labelled at the elevated integrity level, and an unelevated client is refused write access
			// to it however its DACL reads. Creating it before the helper starts also means the name
			// cannot be taken in between.
			pipe = CreatePipe(pipeName);

			var (fileName, prefix) = ResolveHost();
			var arguments = string.IsNullOrEmpty(prefix)
				? HelperArguments(pipeName, token)
				: $"{prefix} {HelperArguments(pipeName, token)}";

			process = Process.Start(new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = arguments,
				UseShellExecute = true,
				Verb = "runas",
				WorkingDirectory = AppContext.BaseDirectory,
				WindowStyle = ProcessWindowStyle.Hidden,
			});

			if (process is null)
			{
				return HardwareHostLaunchResult.FailedBecause(
					"The elevated hardware helper did not start.");
			}

			// Best effort: an unelevated plugin cannot always open an elevated process with the rights
			// AssignProcessToJobObject needs. The helper also watches this process and exits when the pipe
			// closes, so this is one of three ways it cannot outlive the plugin.
			job = JobObject.CreateKillOnClose();
			if (job is not null && !job.TryAssign(process))
			{
				_logger.Debug("The elevated hardware helper could not be assigned to the plugin's job object.");
			}

			var client = await HardwareHostClient
				.AcceptAsync(pipe, token, process.Id, _logger, cancellationToken, process)
				.ConfigureAwait(false);

			// The client owns the pipe from here: disposing the connection closes it, which is what tells
			// the helper to leave.
			pipe = null;

			_logger.Information("The elevated hardware helper is connected on {PipeName}.", pipeName);
			return HardwareHostLaunchResult.ConnectedTo(new HardwareHostConnection(client, process, job, _logger));
		}
		catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
		{
			_logger.Information("The user cancelled the elevated hardware helper's UAC prompt.");
			pipe?.Dispose();
			Stop(process, job);
			return HardwareHostLaunchResult.CancelledByUser;
		}
		catch (OperationCanceledException)
		{
			pipe?.Dispose();
			Stop(process, job);
			throw;
		}
		catch (Exception exception)
		{
			_logger.Warning(exception, "The elevated hardware helper could not be started.");
			pipe?.Dispose();
			Stop(process, job);
			return HardwareHostLaunchResult.FailedBecause(exception.Message);
		}
	}

	/// <summary>
	/// Creates the plugin's end of the pipe with a DACL that names both the current user and
	/// <c>BUILTIN\Administrators</c>.
	/// </summary>
	/// <remarks>
	/// The current user is enough for consent elevation, where the helper runs as the same account with a
	/// higher token. It is not enough for over-the-shoulder elevation: a standard user who enters an
	/// administrator's credentials gets a helper running as <em>that</em> account, which the current
	/// user's SID does not describe. <c>PipeOptions.CurrentUserOnly</c> has the same gap - on Windows it
	/// compares elevation as well as account - and would deny the helper outright. The token and the
	/// client process check are what actually gate the connection, so naming the administrators group
	/// here widens who may knock, not who is let in.
	/// </remarks>
	[SupportedOSPlatform("windows")]
	internal static NamedPipeServerStream CreatePipe(string pipeName)
	{
		using var identity = WindowsIdentity.GetCurrent();
		var user = identity.User
			?? throw new InvalidOperationException(
				"The plugin cannot name its own user to create the elevated helper's pipe.");

		var security = new PipeSecurity();
		security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
		security.AddAccessRule(new PipeAccessRule(
			new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
			PipeAccessRights.ReadWrite,
			AccessControlType.Allow));

		return NamedPipeServerStreamAcl.Create(
			pipeName,
			PipeDirection.InOut,
			maxNumberOfServerInstances: 1,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous,
			inBufferSize: 0,
			outBufferSize: 0,
			pipeSecurity: security);
	}

	/// <summary>The plugin's own apphost, or the dotnet host that is running this assembly.</summary>
	private static (string FileName, string Prefix) ResolveHost()
	{
		var processPath = Environment.ProcessPath;
		if (!string.IsNullOrEmpty(processPath)
			&& !string.Equals(
				Path.GetFileNameWithoutExtension(processPath),
				"dotnet",
				StringComparison.OrdinalIgnoreCase))
		{
			return (processPath, string.Empty);
		}

		var assembly = Assembly.GetEntryAssembly()?.Location;
		if (!string.IsNullOrEmpty(assembly))
		{
			var appHost = Path.ChangeExtension(assembly, ".exe");
			if (File.Exists(appHost))
			{
				return (appHost, string.Empty);
			}

			return (processPath ?? "dotnet.exe", Quote(assembly));
		}

		throw new InvalidOperationException(
			"The plugin cannot find its own executable to start the elevated hardware helper.");
	}

	private static string HelperArguments(string pipeName, string token) =>
		$"{HardwareHostOptions.Flag} --pipe {pipeName} --token {token} --parent {Environment.ProcessId}";

	private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

	/// <summary>Stops a helper that never became a connection. The job object is the last resort.</summary>
	private static void Stop(Process? process, JobObject? job)
	{
		if (process is not null)
		{
			try
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
				}
			}
			catch (Exception)
			{
				// The helper watches the plugin process; not being able to kill it here is not fatal.
			}
			finally
			{
				process.Dispose();
			}
		}

		if (OperatingSystem.IsWindows())
		{
			job?.Dispose();
		}
	}
}
