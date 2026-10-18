using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Migration;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using MacroDeck.Sdk.ConfigFlow;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Configuration;

/// <summary>
/// The plugin's settings screen, which is the Macro Deck 2 plugin's configuration dialog: which sensors
/// to declare as variables, and how often to read them.
/// </summary>
/// <remarks>
/// <para>
/// The screen opens straight onto that dialog. Its one button hands the browser a page serving the old
/// checkbox tree - hardware, then reading kind, then sensor, with the polling-rate control under it - and
/// what the user ticks there is what the host stores. There is no chooser of the plugin's own in front of
/// it: Macro Deck 3 has no tree control in its declared fields, and a flat form of grouped multi-selects
/// standing in for the tree is not the dialog that was ported.
/// </para>
/// <para>
/// The window comes back through the host's OAuth callback, which is the only door into a flow that has
/// handed control to a browser: the host opens the page, and when the page is finished it sends the
/// browser to a callback the host is watching. That callback is a <c>GET</c> whose whole request line
/// Kestrel caps at 8 KB, so the selection is posted to the plugin before the redirect and only a key
/// follows it - read back out of <see cref="ClassicWindowServer" /> when the flow resumes.
/// </para>
/// <para>
/// What is stored is the sensors' full identity, not the resource ids the window round-trips through, for
/// the same reason the migration stores it: the integration has to be able to declare their variables at
/// startup without probing the hardware first.
/// </para>
/// <para>
/// Macro Deck 2 made the user pick before the plugin did anything. Here the picker decides only which
/// sensors are declared as variables up front - every other sensor is still reachable through the
/// catalog - but a button that names one by hand resolves only once that sensor has been declared here
/// or carried over by the migration.
/// </para>
/// </remarks>
internal sealed class LibreHardwareMonitorConfigFlow : IConfigFlow
{
	/// <summary>The one step, also the id the host submits back when its button is pressed.</summary>
	internal const string StepId = "sensors";

	/// <summary>
	/// The id the host submits once the classic window says it is finished. Nothing comes with it - the
	/// dialog resubmits an external step with an empty input - so the selection is read from
	/// <see cref="ClassicWindowServer" /> under the key the callback carried.
	/// </summary>
	internal const string ResumeStepId = "classic-window";

	/// <summary>
	/// The name of the configuration entry this flow saves, written once in the plugin's own language:
	/// the host stores it as the entry's name and the user renames it from there.
	/// </summary>
	private const string EntryTitle = "Libre Hardware Monitor";

	/// <summary>The old plugin's slider bounds, reproduced.</summary>
	internal const double MinimumPollingMilliseconds = 10;
	internal const double MaximumPollingMilliseconds = 10_000;

	private readonly HardwareMonitor _hardware;
	private readonly SensorSelection _selection;
	private readonly ClassicWindowServer _window;
	private readonly ILogger _logger;

	/// <summary>
	/// The sensors known when the window was opened, which is what its tree was drawn from and what the
	/// ids it posts back are resolved against.
	/// </summary>
	private SensorIndex? _sensors;

	public LibreHardwareMonitorConfigFlow(
		HardwareMonitor hardware,
		SensorSelection selection,
		ClassicWindowServer window,
		ILogger logger)
	{
		_hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
		_selection = selection ?? throw new ArgumentNullException(nameof(selection));
		_window = window ?? throw new ArgumentNullException(nameof(window));
		_logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<LibreHardwareMonitorConfigFlow>();
	}

	/// <inheritdoc />
	public Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken cancellationToken) =>
		Task.FromResult(ConfigFlowResult.Step(BuildStep()));

	/// <summary>
	/// The screen's one button, and the window's answer. <c>Continue</c> opens the classic window;
	/// <see cref="ResumeStepId" /> means the window is finished, and what it collected is what is stored.
	/// </summary>
	public async Task<ConfigFlowResult> SubmitAsync(
		string stepId,
		IReadOnlyDictionary<string, object?> input,
		IConfigFlowContext context,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);

		return stepId switch
		{
			ResumeStepId => await CompleteWindowAsync(context, cancellationToken).ConfigureAwait(false),
			StepId => await OpenWindowAsync(context, cancellationToken).ConfigureAwait(false),
			_ => ConfigFlowResult.Error(BuildStep(), Strings.Configuration.UnknownStep()),
		};
	}

	/// <summary>
	/// Opens the classic window on the configuration the plugin is running with, which is what the old
	/// dialog opened on too. A window that cannot be opened is reported rather than worked around: this
	/// screen has no form to fall back to, and saving "nothing was ticked" instead would quietly
	/// undeclare every variable.
	/// </summary>
	private async Task<ConfigFlowResult> OpenWindowAsync(
		IConfigFlowContext context,
		CancellationToken cancellationToken)
	{
		var configured = _selection.Sensors;

		try
		{
			var index = _sensors ??= await IndexAsync(cancellationToken).ConfigureAwait(false);
			var oauth = context.OAuth;

			var opened = _window.Open(new ClassicWindowOpening(
				EntryTitle,
				SensorCategory.Build(index.Reported),
				configured.Select(sensor => sensor.ResourceId).Distinct(StringComparer.Ordinal).ToArray(),
				CurrentPollingMilliseconds(),
				MinimumPollingMilliseconds,
				MaximumPollingMilliseconds,
				oauth.RedirectUri,
				oauth.State));

			_logger.Information(
				"The classic sensor window was opened on {SensorCount} sensors at {WindowUri}.",
				configured.Count,
				opened.PageUri);

			return ConfigFlowResult.External(opened.PageUri.ToString(), ResumeStepId);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			_logger.Error(exception, "The classic sensor window could not be opened.");

			return ConfigFlowResult.Error(BuildStep(), Strings.Configuration.WindowRefused());
		}
	}

	/// <summary>
	/// Reads what the window left behind, under the key the host's callback carried back. A key the
	/// plugin is not holding is refused rather than read as "nothing was ticked", because an empty
	/// selection would quietly declare no variables at all.
	/// </summary>
	private async Task<ConfigFlowResult> CompleteWindowAsync(
		IConfigFlowContext context,
		CancellationToken cancellationToken)
	{
		var key = context.OAuth.AuthorizationCode;
		if (string.IsNullOrEmpty(key) || !_window.TryTakePayload(key, out var payload) || payload is null)
		{
			_logger.Warning("The classic sensor window came back without a selection this plugin was holding.");

			return ConfigFlowResult.Error(BuildStep(), Strings.Configuration.WindowLost());
		}

		var sensors = await ResolveAsync([.. payload.ResourceIds], cancellationToken).ConfigureAwait(false);

		// A window always posts its interval; if one ever does not, the interval the loop is already
		// running at is kept rather than dropped, which is what would silently reset it to the default.
		var polling = PollingInterval(payload)
			?? TimeSpan.FromMilliseconds(CurrentPollingMilliseconds());

		_logger.Information("The classic sensor window sent back {SensorCount} sensors.", sensors.Count);

		return Complete(sensors, polling);
	}

	/// <summary>What the screen ends in: the selection written as a configuration entry.</summary>
	private ConfigFlowResult Complete(IReadOnlyList<SensorIdentifier> sensors, TimeSpan? polling)
	{
		_logger.Information("The sensor configuration was saved with {SensorCount} sensors.", sensors.Count);

		return ConfigFlowResult.Complete(
			EntryTitle,
			new MigratedSensorSelection(sensors, polling).ToConfigurationValues());
	}

	/// <summary>
	/// The interval the window sent, clamped into the control's own bounds. The page bounds its field, but
	/// what arrives is a form post, and only the plugin is in a position to insist.
	/// </summary>
	private static TimeSpan? PollingInterval(ClassicWindowPayload payload) =>
		payload.PollingMilliseconds is { } milliseconds && double.IsFinite(milliseconds)
			? TimeSpan.FromMilliseconds(Math.Clamp(
				milliseconds,
				MinimumPollingMilliseconds,
				MaximumPollingMilliseconds))
			: null;

	/// <summary>
	/// The one step: a title, a line saying where the sensors are chosen, and the button that opens the
	/// window. It declares no fields on purpose - the window is the chooser, and the tree it holds is one
	/// this screen has no control to draw.
	/// </summary>
	private static ConfigFlowStep BuildStep() => new()
	{
		StepId = StepId,
		Title = Strings.Configuration.Title(),
		Description = Strings.Configuration.Description(),
		Fields = [],
	};

	/// <summary>
	/// Every sensor the hardware reports, read once for the session. The tree the window is drawn from
	/// and the ids it posts back both come from this one read, so they cannot disagree: a second read
	/// could come back naming hardware the user never saw.
	/// </summary>
	private async Task<SensorIndex> IndexAsync(CancellationToken cancellationToken)
	{
		IReadOnlyList<SensorIdentifier> reported;
		try
		{
			reported = await _hardware.EnumerateSensorsAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			_logger.Error(exception, "The hardware could not be enumerated, so the sensor window is empty.");
			reported = [];
		}

		return BuildIndex(reported);
	}

	/// <summary>
	/// Turns one enumeration into the two things the session needs from it: the sensors in the order the
	/// window shows them, and the same sensors keyed by the id the window posts back.
	/// </summary>
	private static SensorIndex BuildIndex(IReadOnlyList<SensorIdentifier> reported)
	{
		var byResourceId = new Dictionary<string, SensorIdentifier>(StringComparer.Ordinal);
		var unique = new List<SensorIdentifier>(reported.Count);

		foreach (var sensor in reported)
		{
			// Two sensors that reduce to one resource id cannot both be ticked under it, so the first is
			// what the tree holds and what that id resolves back to.
			if (byResourceId.TryAdd(sensor.ResourceId, sensor))
			{
				unique.Add(sensor);
			}
		}

		return new SensorIndex(unique, byResourceId);
	}

	/// <summary>
	/// Turns the ticked resource ids back into the sensors they name, dropping any that are no longer
	/// present. Re-reads the hardware once if an id cannot be resolved, so a submit that follows a device
	/// being plugged in or removed does not silently lose the rest of the selection.
	/// </summary>
	private async Task<IReadOnlyList<SensorIdentifier>> ResolveAsync(
		List<string> resourceIds,
		CancellationToken cancellationToken)
	{
		if (resourceIds.Count == 0)
		{
			return [];
		}

		var wanted = resourceIds.Distinct(StringComparer.Ordinal).Count();
		var index = _sensors ??= await IndexAsync(cancellationToken).ConfigureAwait(false);
		var resolved = Map(resourceIds, index.ByResourceId);
		if (resolved.Count == wanted)
		{
			return resolved;
		}

		var refreshed = await IndexAsync(cancellationToken).ConfigureAwait(false);
		_sensors = refreshed;
		resolved = Map(resourceIds, refreshed.ByResourceId);
		if (resolved.Count != wanted)
		{
			_logger.Warning(
				"{MissingCount} of the {PickedCount} ticked sensors are not present on this machine.",
				wanted - resolved.Count,
				resourceIds.Count);
		}

		return resolved;
	}

	private static List<SensorIdentifier> Map(
		List<string> resourceIds,
		IReadOnlyDictionary<string, SensorIdentifier> known)
	{
		var seen = new HashSet<SensorIdentifier>();
		var sensors = new List<SensorIdentifier>(resourceIds.Count);
		foreach (var resourceId in resourceIds)
		{
			if (known.TryGetValue(resourceId, out var sensor) && seen.Add(sensor))
			{
				sensors.Add(sensor);
			}
		}

		return sensors;
	}

	/// <summary>The interval the loop is running at, clamped into the control's own bounds.</summary>
	private double CurrentPollingMilliseconds() =>
		Math.Clamp(
			_hardware.Options.PollingInterval.TotalMilliseconds,
			MinimumPollingMilliseconds,
			MaximumPollingMilliseconds);

	/// <summary>
	/// One enumeration of the hardware, both ways the session asks for it: the order the window shows the
	/// sensors in, and the lookup that turns a ticked id back into the sensor it names.
	/// </summary>
	private sealed record SensorIndex(
		IReadOnlyList<SensorIdentifier> Reported,
		IReadOnlyDictionary<string, SensorIdentifier> ByResourceId);
}
