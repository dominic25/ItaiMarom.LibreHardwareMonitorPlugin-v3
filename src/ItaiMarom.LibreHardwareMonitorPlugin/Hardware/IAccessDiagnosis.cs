namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// What to tell a user about the readings this process cannot reach, or null when there is nothing to
/// say.
/// </summary>
/// <remarks>
/// <see cref="KernelAccess"/> answers from the machine this process is running on, which is what makes it
/// the one part of the plugin no test can drive. This is the seam over it, in the same shape
/// <see cref="IHardwareSensorSource"/> is the seam over the hardware library: the real answer in
/// production, a scripted one in a test that has to know what a warning looks like.
/// </remarks>
public interface IAccessDiagnosis
{
	/// <summary>
	/// The sentence to show the user, or null when the driver is installed and this process can open it.
	/// </summary>
	string? Describe();
}

/// <summary>The real diagnosis, read from this machine's driver state.</summary>
public sealed class KernelAccessDiagnosis : IAccessDiagnosis
{
	/// <inheritdoc />
	public string? Describe() => KernelAccess.Describe();
}
