using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// What to tell a user about the readings this process cannot reach, or null when there is nothing to
/// say.
/// </summary>
/// <remarks>
/// <para>
/// Part of what the hardware library reports - processor temperatures and clocks, a motherboard's fan
/// speeds and voltages, a drive's health - is read through a kernel driver rather than through Windows,
/// and the driver the library asks for, PawnIO, only opens for an administrator. Without it those
/// readings are not merely empty: the library cannot enumerate the devices behind them at all, so whole
/// branches of the tree are missing rather than greying out.
/// </para>
/// <para>
/// The library version the Macro Deck 2 plugin used shipped its own driver, and that one let any process
/// read the machine. The same machine could therefore produce those readings there and produce nothing
/// here, with nothing said about why. This is that missing sentence.
/// </para>
/// </remarks>
internal static class KernelAccess
{
	/// <summary>The driver the library loads its modules into.</summary>
	private const string DriverName = "PawnIO";

	/// <summary>Where that driver is downloaded from.</summary>
	private const string DriverUri = "https://pawnio.eu";

	/// <summary>The service it installs, whose presence is what says whether it is installed at all.</summary>
	private const string DriverServiceKey = @"SYSTEM\CurrentControlSet\Services\" + DriverName;

	/// <summary>The device it exposes, and the one the library opens.</summary>
	private const string DriverDevicePath = @"\\.\PawnIO";

	/// <summary>What is missing, in the words a user would look for it under.</summary>
	private const string Affected = "Processor temperatures and clocks, fan speeds, voltages and drive health";

	/// <summary>
	/// The sentence to show the user, or null when the driver is installed and this process can open it.
	/// </summary>
	public static string? Describe()
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}

		if (!IsInstalled())
		{
			return $"{Affected} are read through the {DriverName} driver, which this machine does not "
				+ $"have. Install it from {DriverUri} and start Macro Deck again to see them here.";
		}

		if (CanOpenDevice())
		{
			return null;
		}

		return IsElevated()
			? $"{Affected} are read through the {DriverName} driver, which is installed but would not "
				+ "open. They are missing from this list until it can be opened again."
			: $"{Affected} are read through a kernel driver, and Windows only lets an administrator open "
				+ "it, so they are missing from this list. Start Macro Deck as an administrator to see "
				+ "them here.";
	}

	/// <summary>
	/// Whether the driver is installed. The service is asked rather than the device, because the device
	/// object only exists while the driver is loaded - and loading it is itself the thing this process
	/// may not be allowed to do.
	/// </summary>
	[SupportedOSPlatform("windows")]
	private static bool IsInstalled()
	{
		try
		{
			using var service = Registry.LocalMachine.OpenSubKey(DriverServiceKey);
			return service is not null;
		}
		catch (Exception)
		{
			// A registry that cannot be read is not a diagnosis, and telling someone to install a driver
			// they already have is worse than saying nothing about it.
			return true;
		}
	}

	/// <summary>
	/// Opens the driver's device and closes it again. A refused open is what an unelevated process sees
	/// when the driver is installed; a plain failure is what anyone sees when it is not loaded.
	/// </summary>
	[SupportedOSPlatform("windows")]
	private static bool CanOpenDevice()
	{
		try
		{
			using var device = new FileStream(
				DriverDevicePath,
				FileMode.Open,
				FileAccess.ReadWrite,
				FileShare.ReadWrite);

			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Whether this process runs elevated, which is what the driver asks for.</summary>
	[SupportedOSPlatform("windows")]
	private static bool IsElevated()
	{
		try
		{
			using var identity = WindowsIdentity.GetCurrent();
			return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
		}
		catch (Exception)
		{
			// Not being able to ask is not a yes.
			return false;
		}
	}
}
