using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Actions;

/// <summary>
/// Hands every controllable fan and pump back to the motherboard's own curve in one press.
/// </summary>
/// <remarks>
/// <see cref="RestoreFanControlAction"/> is the per-fan undo and needs the user to pick the fan. This one
/// exists for the moment the user wants everything back at once - a "silence" button, a shutdown button,
/// or the way out of a fan curve gone wrong - and for a deck where naming every fan would be noise.
/// </remarks>
internal sealed class RestoreAllFansAction(HardwareMonitor monitor, ILogger logger) : IActionDefinition
{
	private readonly HardwareMonitor _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
	private readonly ILogger _logger = (logger ?? throw new ArgumentNullException(nameof(logger)))
		.ForContext<RestoreAllFansAction>();

	/// <inheritdoc />
	public string Id => "restore-all-fans";

	/// <inheritdoc />
	public LocalizedText Name => Strings.Actions.RestoreAllFans.Name();

	/// <inheritdoc />
	public LocalizedText Description => Strings.Actions.RestoreAllFans.Description();

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
				var result = await monitor
					.ResetAllControlsAsync(context.CancellationToken)
					.ConfigureAwait(false);

				if (result.Reset == 0 && result.Failed.Count == 0)
				{
					return ActionResult.Failed(
						ActionErrorCodes.NotFound,
						Strings.Actions.RestoreAllFans.None());
				}

				if (result.Failed.Count > 0)
				{
					logger.Warning(
						"{FailedCount} fan controls could not be handed back: {Failed}.",
						result.Failed.Count,
						string.Join(", ", result.Failed));
				}

				return ActionResult.Accepted(Strings.Actions.RestoreAllFans.Reset());
			}
			catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				logger.Error(exception, "The fan controls could not be handed back.");
				return ActionResult.Failed(ActionErrorCodes.ProviderError, Strings.Actions.ControlFailed());
			}
		}
	}
}
