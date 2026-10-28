using System.Runtime.Versioning;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// The elevated helper's entry point. It is the same binary as the plugin, started with
/// <c>--hardware-host</c>; it owns the <c>Computer</c> and serves the plugin over a named pipe.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HardwareHost
{
	public static async Task<int> RunAsync(HardwareHostOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		var logger = HardwareHostLog.Create();
		logger.Information(
			"The elevated hardware helper is starting for plugin process {ParentProcessId}.",
			options.ParentProcessId);

		// The whole point of this process is the administrator token. If Windows did not grant one - UAC
		// was configured off in a way that launched a filtered token, or something other than the launcher
		// started the helper - then opening the hardware here would report limited readings as elevated
		// ones: the plugin would mark itself elevated, stop offering the button, and leave the user with a
		// warning that tells them to press it. Refusing is the only answer that can be acted on.
		if (!KernelAccess.IsElevated())
		{
			logger.Error(
				"The elevated hardware helper was started without administrator rights; refusing to serve "
					+ "limited readings as elevated ones.");
			return HardwareHostExitCodes.NotElevated;
		}

		var source = new LibreHardwareMonitorSource();
		var handler = new HardwareHostHandler(source, logger, () => KernelAccess.Diagnose().Note);
		var session = new HardwareHostSession(
			options.PipeName,
			options.Token,
			options.ParentProcessId,
			handler,
			logger);

		var exitCode = await session.RunAsync(CancellationToken.None).ConfigureAwait(false);

		logger.Information("The elevated hardware helper stopped with exit code {ExitCode}.", exitCode);
		return exitCode;
	}
}
