using System.Globalization;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// A log file of its own for the elevated helper.
/// </summary>
/// <remarks>
/// The helper has no host to send <c>log.publish</c> to and no console - it is a windowless process - so
/// the one place a failure can be read is a file. It is deliberately separate from the plugin's log: the
/// helper may be running as a different token, and a log it cannot write is worse than a second file. A
/// log that cannot be opened falls back to no logging rather than stopping the helper.
/// </remarks>
internal static class HardwareHostLog
{
	private static readonly object Sync = new();

	public static ILogger Create()
	{
		try
		{
			var directory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"MacroDeck",
				"logs");

			Directory.CreateDirectory(directory);
			var path = Path.Combine(directory, "libre-hardware-monitor-host.log");

			return new LoggerConfiguration()
				.MinimumLevel.Information()
				.WriteTo.Sink(new FileSink(path))
				.CreateLogger();
		}
		catch (Exception)
		{
			// A helper that cannot write a log is still a helper. There is nowhere to report this to.
			return Logger.None;
		}
	}

	/// <summary>Appends one rendered event, one line at a time, so two threads cannot interleave a record.</summary>
	private sealed class FileSink(string path) : ILogEventSink
	{
		public void Emit(LogEvent logEvent)
		{
			lock (Sync)
			{
				try
				{
					using var writer = new StreamWriter(path, append: true);
					writer.Write(logEvent.Timestamp.ToString("O"));
					writer.Write(" [");
					writer.Write(logEvent.Level);
					writer.Write("] ");
					writer.WriteLine(logEvent.RenderMessage(CultureInfo.InvariantCulture));

					if (logEvent.Exception is { } exception)
					{
						writer.WriteLine(exception);
					}
				}
				catch (Exception)
				{
					// Logging must never take the helper down.
				}
			}
		}
	}
}
