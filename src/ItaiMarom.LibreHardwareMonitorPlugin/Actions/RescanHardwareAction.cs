using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Actions;

/// <summary>
/// Re-reads the machine's hardware, so a device attached or removed since the plugin started appears
/// without restarting Macro Deck.
/// </summary>
/// <remarks>
/// The sensor window has the same request on a button, because that is where a user notices a missing
/// device. This action exists for the deck: a laptop that docks and undocks, or a USB drive that comes and
/// goes, can be picked up from a button instead of a restart.
/// </remarks>
internal sealed class RescanHardwareAction(HardwareMonitor monitor, ILogger logger) : IActionDefinition
{
	private readonly HardwareMonitor _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
	private readonly ILogger _logger = (logger ?? throw new ArgumentNullException(nameof(logger)))
		.ForContext<RescanHardwareAction>();

	/// <inheritdoc />
	public string Id => "rescan-hardware";

	/// <inheritdoc />
	public LocalizedText Name => Strings.Actions.RescanHardware.Name();

	/// <inheritdoc />
	public LocalizedText Description => Strings.Actions.RescanHardware.Description();

	/// <inheritdoc />
	public IReadOnlyList<ActionParameter> Parameters => [];

	/// <inheritdoc />
	public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

	/// <inheritdoc />
	public string? Role => null;

	/// <inheritdoc />
	public IActionExecutor CreateExecutor() => new Executor(_monitor, _logger);

	private sealed class Executor(HardwareMonitor monitor, ILogger logger) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			ArgumentNullException.ThrowIfNull(context);

			try
			{
				await monitor.RescanAsync(context.CancellationToken).ConfigureAwait(false);
				return ActionResult.Accepted(Strings.Actions.RescanHardware.Done());
			}
			catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				logger.Error(exception, "The hardware could not be re-read.");
				return ActionResult.Failed(ActionErrorCodes.ProviderError, Strings.Actions.RescanHardware.Failed());
			}
		}
	}
}
