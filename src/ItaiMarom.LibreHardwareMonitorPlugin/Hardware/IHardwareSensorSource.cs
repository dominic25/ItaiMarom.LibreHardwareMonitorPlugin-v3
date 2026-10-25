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

	/// <summary>
	/// Updates the hardware and returns every sensor that exposes a software control - the fans and pumps
	/// a super-I/O chip lets the plugin drive.
	/// </summary>
	/// <remarks>
	/// The control only exists when the library could reach the chip, which on Windows means the kernel
	/// driver loaded - so an empty list is the ordinary answer for a plugin that is not elevated. Only the
	/// identity travels: the library's own <c>IControl</c> stays behind this seam, so a caller cannot hold
	/// one across an update.
	/// </remarks>
	IReadOnlyList<SensorIdentifier> ReadControls();

	/// <summary>
	/// Sets a control's software value, clamped to the range that control reports. Returns false when no
	/// controllable sensor has this identifier, which is how a fan that has since vanished is reported.
	/// </summary>
	/// <param name="sensorId">The sensor's library identifier, as <see cref="SensorIdentifier.Id"/> holds it.</param>
	/// <param name="value">The requested value, normally a percentage of the control's range.</param>
	bool SetControl(string sensorId, float value);

	/// <summary>
	/// Hands a control back to the hardware's own curve - what the BIOS does when nothing is driving it -
	/// and returns false when no controllable sensor has this identifier.
	/// </summary>
	bool ResetControl(string sensorId);
}
