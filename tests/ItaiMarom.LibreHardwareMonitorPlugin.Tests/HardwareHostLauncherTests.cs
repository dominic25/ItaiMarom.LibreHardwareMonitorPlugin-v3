using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// Which executable the plugin starts for its elevated helper. Macro Deck hosts plugins on a private
/// runtime through its own <c>dotnet</c>, so reusing that host is what makes elevation work on a machine
/// with no globally installed .NET 10; the apphost is only there for a self-contained build.
/// </summary>
[TestFixture]
public sealed class HardwareHostLauncherTests
{
	private const string PluginDirectory = @"C:\MacroDeck\plugins\plugin\versions\1.4.0\runtimes\win-x64";
	private const string AssemblyPath = PluginDirectory + @"\ItaiMarom.LibreHardwareMonitorPlugin.dll";
	private const string AppHostPath = PluginDirectory + @"\ItaiMarom.LibreHardwareMonitorPlugin.exe";
	private const string PrivateDotnet = @"C:\Macro Deck\host\runtime\dotnet.exe";
	private const string PrivateRuntime = @"C:\Macro Deck\host\runtime\shared\Microsoft.NETCore.App\10.0.12\";

	[Test]
	public void A_plugin_hosted_by_dotnet_relaunches_that_same_dotnet()
	{
		var (fileName, prefix) = WindowsHardwareHostLauncher.ResolveHost(
			PrivateDotnet,
			AssemblyPath,
			PrivateRuntime,
			_ => false);

		Assert.That(fileName, Is.EqualTo(PrivateDotnet));
		Assert.That(prefix, Is.EqualTo($"\"{AssemblyPath}\""));
	}

	[Test]
	public void A_plugin_hosted_by_its_apphost_uses_the_dotnet_that_owns_its_runtime()
	{
		// A developer run from an apphost: the apphost found the global runtime, and that runtime's
		// dotnet host is the one the elevated process can reach without any environment.
		var processPath = @"C:\Program Files\dotnet\dotnet.exe";
		var (fileName, prefix) = WindowsHardwareHostLauncher.ResolveHost(
			AppHostPath,
			AssemblyPath,
			@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.15\",
			path => string.Equals(path, processPath, StringComparison.OrdinalIgnoreCase));

		Assert.That(fileName, Is.EqualTo(processPath));
		Assert.That(prefix, Is.EqualTo($"\"{AssemblyPath}\""));
	}

	[Test]
	public void A_self_contained_plugin_keeps_its_own_apphost()
	{
		var (fileName, prefix) = WindowsHardwareHostLauncher.ResolveHost(
			AppHostPath,
			AssemblyPath,
			PluginDirectory + @"\",
			path => string.Equals(path, AppHostPath, StringComparison.OrdinalIgnoreCase));

		Assert.That(fileName, Is.EqualTo(AppHostPath));
		Assert.That(prefix, Is.Empty);
	}

	[Test]
	public void An_assembly_path_with_spaces_is_quoted_for_the_dotnet_host()
	{
		var assembly = @"C:\Users\A User\AppData\Roaming\MacroDeck\plugins\plugin\versions\1.4.0\ItaiMarom.LibreHardwareMonitorPlugin.dll";

		var (_, prefix) = WindowsHardwareHostLauncher.ResolveHost(
			PrivateDotnet,
			assembly,
			PrivateRuntime,
			_ => false);

		Assert.That(prefix, Is.EqualTo($"\"{assembly}\""));
	}

	[Test]
	public void A_plugin_with_no_way_to_relaunch_itself_is_an_explicit_failure()
	{
		Assert.That(
			() => WindowsHardwareHostLauncher.ResolveHost(PrivateDotnet, null, PrivateRuntime, _ => false),
			Throws.TypeOf<InvalidOperationException>());
	}
}
