namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>What happened when the plugin asked for an elevated hardware helper.</summary>
public enum ElevationOutcome
{
	/// <summary>The helper is running and is serving the hardware.</summary>
	Enabled,

	/// <summary>The user dismissed the UAC prompt. The plugin keeps its limited mode.</summary>
	Cancelled,

	/// <summary>The helper could not be started, or could not open the hardware.</summary>
	Failed,
}

/// <summary>The result of one request for elevated readings.</summary>
/// <param name="Outcome">Whether the helper is now serving the hardware.</param>
/// <param name="Error">What went wrong, when <paramref name="Outcome"/> is <see cref="ElevationOutcome.Failed"/>.</param>
/// <param name="AccessNote">
/// What the helper still cannot reach, or null when it can read everything. An elevated helper that
/// reports a note is running, but the note is why the readings are still missing - the driver is absent,
/// or will not open even for an administrator.
/// </param>
public readonly record struct ElevationResult(
	ElevationOutcome Outcome,
	string? Error = null,
	string? AccessNote = null)
{
	/// <summary>Whether the helper is now serving the hardware.</summary>
	public bool Succeeded => Outcome == ElevationOutcome.Enabled;
}

/// <summary>
/// The part of a hardware source that can move the privilege into a helper the plugin owns.
/// </summary>
/// <remarks>
/// A source that does not implement this simply has no way to be elevated - the test fake, for example -
/// and the issue list offers only the sensor window. <see cref="KernelAccessDiagnosis"/> also uses this to
/// ask what the elevated helper can read, because this process cannot answer that for it.
/// </remarks>
public interface IHardwareElevation
{
	/// <summary>Whether an elevated helper is currently serving the hardware.</summary>
	bool IsElevated { get; }

	/// <summary>
	/// What the elevated helper still cannot read, or null when it can read everything. Null while no
	/// helper is running.
	/// </summary>
	string? ElevatedAccessNote { get; }

	/// <summary>
	/// Starts the elevated helper and switches the source to it. A cancelled UAC prompt is a result, not
	/// an exception: the plugin keeps the readings it already has.
	/// </summary>
	Task<ElevationResult> EnableAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A source that reports what its hardware can reach - the pipe client does, so the plugin can carry the
/// helper's answer back to the issue list and the sensor window.
/// </summary>
internal interface IAccessNoteSource
{
	string? AccessNote { get; }
}
