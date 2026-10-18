using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// A machine that has already answered whether this process can reach the driver, so a test that wants to
/// see the warning can have it without depending on how this one happens to be started.
/// </summary>
/// <remarks>
/// <see cref="KernelAccess"/> reads the running machine, which is exactly what no test can drive: the same
/// suite would assert one thing unelevated and another as an administrator. This fixes the answer instead.
/// </remarks>
internal sealed class FakeAccessDiagnosis : IAccessDiagnosis
{
	private readonly string? _note;

	public FakeAccessDiagnosis(string? note) => _note = note;

	/// <summary>What a driver this process cannot open says, in the shape <see cref="KernelAccess"/> builds it.</summary>
	public const string NotElevated =
		"Processor temperatures and clocks are read through a kernel driver, and Windows only lets an "
		+ "administrator open it, so they are missing. Start Macro Deck as an administrator to see them.";

	/// <summary>The normal answer: the driver is installed and this process opened it.</summary>
	public static FakeAccessDiagnosis Reachable() => new(null);

	/// <summary>A machine whose driver this process was refused.</summary>
	public static FakeAccessDiagnosis Unreachable() => new(NotElevated);

	/// <inheritdoc />
	public string? Describe() => _note;
}
