namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// The exit codes the elevated helper reports. They exist so a helper that never connected can say why in
/// one number: the plugin logs the code, and the launcher turns the interesting ones back into a sentence.
/// </summary>
internal static class HardwareHostExitCodes
{
	/// <summary>The helper served the plugin and stopped when the plugin disconnected.</summary>
	internal const int Served = 0;

	/// <summary>The helper stopped unexpectedly; its own log has the exception.</summary>
	internal const int Failed = 1;

	/// <summary>The helper was started without usable arguments, or on a platform without the pipe.</summary>
	internal const int BadArguments = 2;

	/// <summary>The plugin's pipe never appeared, or the helper could not open it.</summary>
	internal const int NoPipe = 3;

	/// <summary>The pipe was not the plugin's, or the plugin refused the handshake.</summary>
	internal const int Refused = 4;

	/// <summary>
	/// Windows did not give the helper administrator rights. Serving from here would report limited
	/// readings as if they were elevated ones, so the helper leaves instead.
	/// </summary>
	internal const int NotElevated = 5;
}
