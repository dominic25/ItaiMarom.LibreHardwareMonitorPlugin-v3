using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// Hands a set of controls back to the hardware in one place, so the normal fan reset and the one the
/// elevated helper performs before it exits cannot drift apart.
/// </summary>
internal static class ControlReset
{
	/// <summary>
	/// Resets every control, recording the ones that refused or vanished. A throwing control is a
	/// failure for that control, not a reason to leave the rest of the fans where they are.
	/// </summary>
	internal static ControlResetResult ResetAll(
		IReadOnlyList<SensorIdentifier> controls,
		Func<string, bool> reset,
		ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(controls);
		ArgumentNullException.ThrowIfNull(reset);
		ArgumentNullException.ThrowIfNull(logger);

		var failed = new List<string>();

		foreach (var control in controls)
		{
			try
			{
				if (!reset(control.Id))
				{
					failed.Add(control.Id);
				}
			}
			catch (Exception exception)
			{
				logger.Debug(exception, "The control of {SensorId} could not be reset.", control.Id);
				failed.Add(control.Id);
			}
		}

		return new ControlResetResult(controls.Count - failed.Count, failed);
	}
}
