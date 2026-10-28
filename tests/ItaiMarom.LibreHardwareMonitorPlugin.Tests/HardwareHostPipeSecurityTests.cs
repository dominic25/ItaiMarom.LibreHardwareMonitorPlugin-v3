using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// Who the plugin's pipe lets knock. The token and the client process id decide who is served; the DACL
/// only has to let both kinds of UAC elevation reach that decision.
/// </summary>
/// <remarks>
/// Consent elevation runs the helper as the same user with a higher token, which the current user's SID
/// already covers. Over-the-shoulder elevation runs it as the administrator account whose credentials
/// were entered, which is why <c>BUILTIN\Administrators</c> has to be named as well - and why
/// <c>PipeOptions.CurrentUserOnly</c>, which compares the account too, would refuse it.
/// </remarks>
[TestFixture]
[SupportedOSPlatform("windows")]
public sealed class HardwareHostPipeSecurityTests
{
	[Test]
	public void The_pipe_allows_the_administrators_group()
	{
		using var pipe = WindowsHardwareHostLauncher.CreatePipe(
			"libre-hardware-monitor-test-" + Guid.NewGuid().ToString("N"));

		var rules = pipe.GetAccessControl().GetAccessRules(
			includeExplicit: true,
			includeInherited: true,
			targetType: typeof(SecurityIdentifier));

		var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
		var allowed = rules
			.Cast<PipeAccessRule>()
			.Where(rule => rule.AccessControlType == AccessControlType.Allow)
			.Select(rule => rule.IdentityReference)
			.OfType<SecurityIdentifier>();

		Assert.That(allowed, Does.Contain(administrators));
	}
}
