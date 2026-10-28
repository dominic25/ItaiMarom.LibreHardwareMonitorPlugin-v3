using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// A job object that kills the elevated helper when the plugin's job handle closes. Best effort by design:
/// an unelevated process cannot always open an elevated one with the rights
/// <c>AssignProcessToJobObject</c> needs, which is why the helper also watches the plugin process and
/// exits when its pipe disconnects.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class JobObject : IDisposable
{
	private const int ExtendedLimitInformationClass = 9;
	private const uint JobObjectLimitKillOnJobClose = 0x2000;

	private readonly SafeJobHandle _handle;

	private JobObject(SafeJobHandle handle) => _handle = handle;

	/// <summary>Creates a kill-on-close job, or null when Windows will not create one.</summary>
	public static JobObject? CreateKillOnClose()
	{
		var handle = CreateJobObject(IntPtr.Zero, null);
		if (handle.IsInvalid)
		{
			return null;
		}

		var information = new JobObjectExtendedLimitInformation
		{
			BasicLimitInformation = new JobObjectBasicLimitInformation
			{
				LimitFlags = JobObjectLimitKillOnJobClose,
			},
		};

		if (!SetInformationJobObject(
			handle,
			ExtendedLimitInformationClass,
			ref information,
			(uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
		{
			handle.Dispose();
			return null;
		}

		return new JobObject(handle);
	}

	/// <summary>Puts a process in the job. False when Windows refuses the handle rights, not a fatal error.</summary>
	public bool TryAssign(Process process)
	{
		ArgumentNullException.ThrowIfNull(process);

		try
		{
			return AssignProcessToJobObject(_handle, process.Handle);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <inheritdoc />
	public void Dispose() => _handle.Dispose();

	[StructLayout(LayoutKind.Sequential)]
	private struct JobObjectBasicLimitInformation
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public UIntPtr MinimumWorkingSetSize;
		public UIntPtr MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public UIntPtr Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IoCounters
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JobObjectExtendedLimitInformation
	{
		public JobObjectBasicLimitInformation BasicLimitInformation;
		public IoCounters IoInfo;
		public UIntPtr ProcessMemoryLimit;
		public UIntPtr JobMemoryLimit;
		public UIntPtr PeakProcessMemoryUsed;
		public UIntPtr PeakJobMemoryUsed;
	}

	private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
	{
		public SafeJobHandle()
			: base(ownsHandle: true)
		{
		}

		protected override bool ReleaseHandle() => CloseHandle(handle);
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetInformationJobObject(
		SafeJobHandle job,
		int informationClass,
		ref JobObjectExtendedLimitInformation information,
		uint informationLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);
}
