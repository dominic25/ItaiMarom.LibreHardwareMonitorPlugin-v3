using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Issues;
using MacroDeck.Localization;
using MacroDeck.Sdk.Issues;
using MacroDeck.Sdk.Notifications;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The one warning this plugin has, in the shape it is filed under. A machine whose driver this process
/// opened is the normal answer, so most of what is asserted here is the shape the warning takes when the
/// answer is the other one.
/// </summary>
[TestFixture]
public sealed class MissingReadingsIssueProviderTests
{
	/// <summary>
	/// What turns the catalog's keys into words. The host does that on the wire, so a test that asserted
	/// on the keys alone would pass while saying nothing about what a user reads.
	/// </summary>
	private static readonly LocalizationResolver Resolver = CreateResolver();

	private static LocalizationResolver CreateResolver()
	{
		var catalogs = new LocalizationCatalogRegistry();
		catalogs.Register(Strings.LocalizationCatalog);

		return new LocalizationResolver(catalogs);
	}

	/// <summary>What the host would show the user for a piece of the plugin's own text.</summary>
	private static string Shows(LocalizedText text) => Resolver.Resolve(text, "en");

	[Test]
	public void A_driver_this_process_opened_produces_no_issue()
	{
		// Nothing to warn about is the ordinary case, and it has to stay empty: an issue the user cannot
		// clear is worse than no issue at all.
		var provider = new MissingReadingsIssueProvider(FakeAccessDiagnosis.Reachable());

		Assert.That(provider.Issues(), Is.Empty);
	}

	[Test]
	public void A_driver_this_process_was_refused_produces_one_warning()
	{
		var provider = new MissingReadingsIssueProvider(FakeAccessDiagnosis.Unreachable());

		var issue = provider.Issues().Single();

		// The id is what the host sends back when the button is pressed, so it has to be stable across
		// restarts and versions - it is also the key the notification is filed under.
		Assert.That(issue.Id, Is.EqualTo(MissingReadingsIssueProvider.IssueId));
		Assert.That(Shows(issue.Title), Is.EqualTo("Some readings are missing"));
		Assert.That(Shows(issue.ActionLabel), Is.EqualTo("Open the sensor window"));
		Assert.That(issue.Severity, Is.EqualTo(IntegrationIssueSeverity.Warning));

		// The description is the sentence the sensor window prints over the tree, so a user who reads one
		// and then the other is told the same thing twice rather than two different things.
		Assert.That(issue.Description.ToString(), Is.EqualTo(FakeAccessDiagnosis.NotElevated));
	}

	[Test]
	public void The_issue_is_answered_by_opening_the_sensor_window()
	{
		var resolution = MissingReadingsIssueProvider.Resolve(MissingReadingsIssueProvider.IssueId);

		// The readings are not fixed by pressing the button - only starting Macro Deck again as an
		// administrator fixes them - so the answer opens the settings screen rather than claiming success.
		Assert.That(resolution.Success, Is.True);
		Assert.That(resolution.FollowUp, Is.EqualTo(IssueResolutionFollowUp.StartConfigFlow));
		Assert.That(Shows(resolution.Message), Is.EqualTo("Opening the sensor window."));
	}

	[Test]
	public void An_issue_this_plugin_does_not_know_is_refused_by_name()
	{
		var resolution = MissingReadingsIssueProvider.Resolve("somebody-elses-issue");

		Assert.That(resolution.Success, Is.False);
		Assert.That(resolution.FollowUp, Is.EqualTo(IssueResolutionFollowUp.None));
		Assert.That(Shows(resolution.Message), Is.EqualTo("Unknown issue."));
	}

	[Test]
	public void The_notification_carries_the_same_sentence_as_the_issue()
	{
		var notification = MissingReadingsIssueProvider.Notification(FakeAccessDiagnosis.NotElevated);

		// A notification is plain text where the issue is resolved by the host, so the words have to be
		// spelled out here - and they are the same words, because there is one sentence to say.
		Assert.That(notification.Title, Is.EqualTo("Some readings are missing"));
		Assert.That(notification.Message, Is.EqualTo(FakeAccessDiagnosis.NotElevated));
		Assert.That(notification.Level, Is.EqualTo(UserNotificationLevel.Warning));

		// Filed under the issue's id, so the next initialize replaces this one instead of adding a second.
		Assert.That(notification.Key, Is.EqualTo(MissingReadingsIssueProvider.IssueId));
	}

	[Test]
	public void The_diagnosis_is_read_when_the_host_asks()
	{
		// Starting Macro Deck again as an administrator clears the note, and the host asks again after
		// that. Remembering the first answer would keep the warning up on a machine that no longer has
		// anything wrong with it.
		var diagnosis = new MutableAccessDiagnosis();
		var provider = new MissingReadingsIssueProvider(diagnosis);

		Assert.That(provider.Issues(), Is.Empty);

		diagnosis.Note = FakeAccessDiagnosis.NotElevated;
		Assert.That(provider.Issues(), Has.Count.EqualTo(1));

		diagnosis.Note = null;
		Assert.That(provider.Issues(), Is.Empty);
	}

	/// <summary>A note that can change while the test is watching, which is the whole point of asking.</summary>
	private sealed class MutableAccessDiagnosis : IAccessDiagnosis
	{
		public string? Note { get; set; }

		public string? Describe() => Note;
	}
}
