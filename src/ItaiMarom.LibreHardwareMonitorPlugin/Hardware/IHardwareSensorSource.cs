namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// The seam between the polling loop and the hardware library.
/// </summary>
/// <remarks>
/// Everything behind this interface is blocking and single-threaded by contract: <see cref="HardwareMonitor"/>
/// is the only caller and it serializes every call. Keeping the seam this narrow is what lets the loop's
/// lifecycle - start, poll, stall, stop, restart - be tested without a machine full of sensors behind it.
/// </remarks>
public interface IHardwareSensorSource : IDisposable
{
	/// <summary>
	/// Opens the underlying hardware. Idempotent, and slow enough that callers keep it off anything that
	/// serves the Macro Deck session.
	/// </summary>
	void Open();

	/// <summary>Releases the hardware again, leaving the source openable. Called when the loop stops.</summary>
	void Close();

	/// <summary>
	/// Updates hardware and returns the sensors it now exposes.
	/// </summary>
	/// <param name="hardwareFilter">
	/// When it has entries, only top-level hardware whose name is in it is updated and returned; this is
	/// the old plugin's optimisation, kept so an idle interval does not run a SMART query against every
	/// disk. When it is null or empty every hardware is updated and returned, which is what the sensor
	/// picker and the browsable catalog need.
	/// </param>
	IReadOnlyList<HardwareSensor> Read(IReadOnlyCollection<string>? hardwareFilter);
}
