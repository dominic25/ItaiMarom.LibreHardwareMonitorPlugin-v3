using System.Globalization;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Localization;
using MacroDeck.Sdk.Issues;
using MacroDeck.Sdk.Notifications;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Issues;

/// <summary>
/// The one thing this plugin has to warn about: the readings the kernel driver would answer with, which
/// this process cannot reach.
/// </summary>
/// <remarks>
/// <para>
/// Everything else the plugin does either works or fails loudly, so this is the whole of its issue
/// surface, and it is reported to Macro Deck itself rather than only to the sensor window - a window a
/// user has to think to open, which is what made the missing processor temperatures look like a plugin
/// that had broken. The host lists it wherever it shows integration issues, with a button on it.
/// </para>
/// <para>
/// The description is the sentence <see cref="KernelAccess"/> works out for the window, passed through
/// as a literal on purpose: the two surfaces must never disagree about what is missing or about how to
/// fix it, and the window's copy is built there from what this machine actually reports. The short
/// chrome around it - the title the issue is filed under, the button, the answer the button gives - lives
/// in <c>Strings</c> with everything else the host displays.
/// </para>
/// <para>
/// The button changes with the diagnosis. A process that is simply unelevated can be fixed from here,
/// because the source can start an elevated helper; a driver that is absent, or one that will not open
/// even for an administrator, cannot, and the button opens the sensor window so the same sentence is
/// explained where the values are.
/// </para>
/// </remarks>
internal sealed class MissingReadingsIssueProvider
{
	/// <summary>
	/// The id the host sends back when the issue's action is pressed, and the key the notification is
	/// filed under so a restart replaces it rather than stacking a second one.
	/// </summary>
	internal const string IssueId = "readings-missing";

	private readonly IAccessDiagnosis _diagnosis;
	private readonly IHardwareElevation? _elevation;

	public MissingReadingsIssueProvider(IAccessDiagnosis diagnosis, IHardwareElevation? elevation = null)
	{
		_diagnosis = diagnosis ?? throw new ArgumentNullException(nameof(diagnosis));
		_elevation = elevation;
	}

	/// <summary>
	/// What this process cannot read right now, asked when the host asks rather than remembered from
	/// start-up: starting Macro Deck again as an administrator clears it, and the answer has to follow
	/// that without waiting for the next reconnect.
	/// </summary>
	internal string? Note => _diagnosis.Diagnose().Note;

	/// <summary>
	/// Whether the issue's button can start an elevated helper, as opposed to only opening the sensor
	/// window. A helper that is already running has nothing left to enable, and a diagnosis that is not
	/// about elevation - a missing driver, say - is not something the button can fix.
	/// </summary>
	internal bool CanElevate =>
		_elevation is { IsElevated: false } && _diagnosis.Diagnose().CanElevate;

	/// <summary>
	/// The issue, or nothing. Nothing is the normal answer - it means this machine's readings are all
	/// reachable - so a missing note is an empty list rather than a placeholder issue.
	/// </summary>
	public IReadOnlyList<IntegrationIssue> Issues()
	{
		var diagnosis = _diagnosis.Diagnose();
		if (diagnosis.Note is not { } note)
		{
			return [];
		}

		var canElevate = diagnosis.CanElevate && _elevation is { IsElevated: false };

		return
		[
			new IntegrationIssue
			{
				Id = IssueId,
				Title = Strings.Issues.ReadingsMissing.Title(),
				Description = LocalizedText.FromLiteral(note),
				Severity = IntegrationIssueSeverity.Warning,
				ActionLabel = canElevate
					? Strings.Issues.ReadingsMissing.EnableAction()
					: Strings.Issues.ReadingsMissing.Action(),
			},
		];
	}

	/// <summary>
	/// The answer to the issue's button when this machine cannot be helped by elevation: it opens the
	/// sensor window, which is where the values are live and where the same sentence is printed over the
	/// tree. The follow-up hands the host the configuration flow rather than claiming the readings were
	/// fixed - a missing driver is only fixed by installing it.
	/// </summary>
	internal static IssueResolution OpenSensorWindow() =>
		IssueResolution.Ok(Strings.Issues.ReadingsMissing.Resolved(), IssueResolutionFollowUp.StartConfigFlow);

	/// <summary>The answer to an id this plugin does not report.</summary>
	internal static IssueResolution Unknown() =>
		IssueResolution.Failed(Strings.Issues.ReadingsMissing.Unknown());

	/// <summary>
	/// The answer after the helper took over. A helper that can read everything leaves no note; one that
	/// still cannot - a driver that will not open even for an administrator - carries the sentence that
	/// says so, because the issue has to stay up and explain itself.
	/// </summary>
	internal static IssueResolution Elevated(string? accessNote) =>
		IssueResolution.Ok(
			accessNote is null
				? Strings.Issues.ReadingsMissing.Elevated()
				: LocalizedText.FromLiteral(accessNote));

	/// <summary>The answer when the user dismissed the UAC prompt: the plugin keeps its limited mode.</summary>
	internal static IssueResolution ElevationCancelled() =>
		IssueResolution.Failed(Strings.Issues.ReadingsMissing.ElevationCancelled());

	/// <summary>The answer when the helper could not be started, or could not take the hardware over.</summary>
	internal static IssueResolution ElevationFailed() =>
		IssueResolution.Failed(Strings.Issues.ReadingsMissing.ElevationFailed());

	/// <summary>
	/// The same warning as a notification, for the moment the plugin starts. The issue waits to be looked
	/// up; this does not, and it is filed under the issue's id so a second initialize replaces the first
	/// instead of adding to a pile.
	/// </summary>
	/// <remarks>
	/// The title is rendered to plain text here because a notification is plain text - unlike the issue,
	/// which carries <see cref="LocalizedText"/> and is resolved by the host in the user's language. The
	/// message is the note itself: the same sentence the issue's description and the sensor window carry,
	/// so all three say what this machine is actually missing.
	/// </remarks>
	internal static UserNotificationRequest Notification(string note) => new()
	{
		Key = IssueId,
		Title = Resolver.Resolve(Strings.Issues.ReadingsMissing.Title(), CultureInfo.CurrentUICulture.Name),
		Message = note,
		Level = UserNotificationLevel.Warning,
	};

	/// <summary>
	/// What turns the catalog's keys into words. The host does that for the issue, but a notification is
	/// carried as strings, so this plugin has to do it itself - against the one catalog it ships, which
	/// <c>UseLocalization</c> also registers with the host, and in whatever language this process is in.
	/// </summary>
	private static readonly LocalizationResolver Resolver = CreateResolver();

	private static LocalizationResolver CreateResolver()
	{
		var catalogs = new LocalizationCatalogRegistry();
		catalogs.Register(Strings.LocalizationCatalog);

		return new LocalizationResolver(catalogs);
	}
}
