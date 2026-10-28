using System.Globalization;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// The command line the host-facing plugin hands to the elevated copy of itself: which pipe to connect
/// to, what secret to present, and which process started it.
/// </summary>
/// <param name="PipeName">
/// The named pipe the plugin is listening on. It carries 128 random bits, so another process cannot
/// guess it - and the token and server process check below mean knowing it is not enough.
/// </param>
/// <param name="Token">The secret the helper presents in its first message.</param>
/// <param name="ParentProcessId">
/// The plugin process. The helper exits when it does, and refuses a pipe whose server is not it.
/// </param>
internal sealed record HardwareHostOptions(string PipeName, string Token, int ParentProcessId)
{
	/// <summary>The switch that turns the plugin binary into the elevated hardware helper.</summary>
	internal const string Flag = "--hardware-host";

	/// <summary>Whether this process was started as the helper rather than as the plugin.</summary>
	public static bool IsRequested(string[] args) => Array.IndexOf(args, Flag) >= 0;

	/// <summary>
	/// Reads the helper's arguments. A helper started without them cannot be trusted to serve anything, so
	/// it is a startup failure rather than a process that connects somewhere unexpected.
	/// </summary>
	public static bool TryParse(string[] args, out HardwareHostOptions? options)
	{
		ArgumentNullException.ThrowIfNull(args);

		options = null;
		if (!IsRequested(args))
		{
			return false;
		}

		string? pipe = null;
		string? token = null;
		var parent = 0;

		for (var index = 0; index < args.Length; index++)
		{
			switch (args[index])
			{
				case "--pipe" when index + 1 < args.Length:
					pipe = args[++index];
					break;
				case "--token" when index + 1 < args.Length:
					token = args[++index];
					break;
				case "--parent" when index + 1 < args.Length
					&& int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid):
					parent = pid;
					index++;
					break;
			}
		}

		if (string.IsNullOrWhiteSpace(pipe) || string.IsNullOrWhiteSpace(token) || parent <= 0)
		{
			return false;
		}

		options = new HardwareHostOptions(pipe, token, parent);
		return true;
	}
}
