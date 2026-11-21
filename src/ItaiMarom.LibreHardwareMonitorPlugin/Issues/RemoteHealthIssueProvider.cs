using System.Globalization;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Localization;
using MacroDeck.Sdk.Issues;
using MacroDeck.Sdk.Notifications;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Issues;

/// <summary>
/// The warning for a configured remote machine that has stopped answering.
/// </summary>
/// <remarks>
/// A remote that goes away used to be a log line and a branch whose sensors turned into dashes. That is
/// indistinguishable from a machine that has no such sensors, so the state is raised where the user
/// already looks: one issue per offline machine, with a button that retries immediately instead of
/// waiting out the backoff, and one notification when the machine crosses from online to offline.
/// </remarks>
internal sealed class RemoteHealthIssueProvider
{
	/// <summary>The stable prefix of every issue this provider owns.</summary>
	private const string IssuePrefix = "remote-offline-";

	private readonly IRemoteHealthSource? _health;

	public RemoteHealthIssueProvider(IRemoteHealthSource? health) => _health = health;

	/// <summary>One issue per remote that is currently unreachable.</summary>
	public IReadOnlyList<IntegrationIssue> Issues()
	{
		if (_health is null)
		{
			return [];
		}

		return
		[
			.. _health.RemoteStatuses
				.Where(status => status.Health.State == RemoteSourceState.Offline)
				.Select(status => new IntegrationIssue
				{
					Id = IssueId(status.Key),
					Title = Strings.Issues.RemoteOffline.Title(),
					Description = LocalizedText.FromLiteral(Describe(status)),
					Severity = IntegrationIssueSeverity.Warning,
					ActionLabel = Strings.Issues.RemoteOffline.Action(),
				}),
		];
	}

	/// <summary>Whether an issue id belongs to this provider rather than the missing-readings warning.</summary>
	public static bool Owns(string issueId) => issueId.StartsWith(IssuePrefix, StringComparison.Ordinal);

	/// <summary>The answer to the issue's button: retry that machine now.</summary>
	public IssueResolution Retry(string issueId)
	{
		var status = _health?.RemoteStatuses.FirstOrDefault(candidate =>
			string.Equals(IssueId(candidate.Key), issueId, StringComparison.Ordinal));

		if (_health is null || status is null)
		{
			return IssueResolution.Failed(Strings.Issues.RemoteOffline.Unknown());
		}

		return _health.TryRetryRemote(status.Key)
			? IssueResolution.Ok(Strings.Issues.RemoteOffline.Resolved())
			: IssueResolution.Failed(LocalizedText.FromLiteral(
				status.Health.LastError ?? "The remote machine could not be retried."));
	}

	/// <summary>
	/// The key the notification and the issue for one machine are filed under. A machine key is a variable
	/// name fragment and can contain underscores; an issue id is hyphenated like the plugin's other ids.
	/// </summary>
	public static string IssueId(string key) => IssuePrefix + key.Replace('_', '-');

	/// <summary>
	/// The transition notification. It is filed under the issue's key, so a machine that flaps replaces
	/// its own message instead of stacking a second one, and recovery dismisses it.
	/// </summary>
	public static UserNotificationRequest Notification(RemoteSourceStatus status) => new()
	{
		Key = IssueId(status.Key),
		Title = LocalizedTexts.Resolve(Strings.Issues.RemoteOffline.NotificationTitle()),
		Message = Describe(status),
		Level = UserNotificationLevel.Warning,
	};

	private static string Describe(RemoteSourceStatus status) =>
		$"{status.Name} ({status.Url}) is not answering: {status.Health.LastError ?? "no answer"}.";
}
