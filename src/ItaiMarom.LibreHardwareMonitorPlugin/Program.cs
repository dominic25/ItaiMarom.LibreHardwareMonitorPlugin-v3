using ItaiMarom.LibreHardwareMonitorPlugin;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

// The elevated helper is this same binary started with --hardware-host. It is intercepted before the
// host builder ever sees the arguments: they are the helper's, not the plugin's, and a helper that went
// looking for a Macro Deck session would be a second plugin rather than a second process.
if (HardwareHostOptions.IsRequested(args))
{
    if (!OperatingSystem.IsWindows())
    {
        Environment.ExitCode = HardwareHostExitCodes.BadArguments;
        return;
    }

    if (!HardwareHostOptions.TryParse(args, out var options) || options is null)
    {
        Environment.ExitCode = HardwareHostExitCodes.BadArguments;
        return;
    }

    Environment.ExitCode = await HardwareHost.RunAsync(options);
    return;
}

// Identity, description and icon are not set here: they come from manifest.json at the content root.
// Strings is generated from Localization/*.resx, so UseLocalization is what makes every LocalizedText
// resolve in the user's language rather than falling back to its key.
var plugin = MacroDeckPlugin.CreatePlugin(args)
    .UseMacroDeckLogging()
    .UseLocalization(Strings.LocalizationCatalog)
    .ConfigureServices((_, services) =>
    {
        // The plugin starts with the ordinary hardware library and can swap to the elevated helper on
        // demand. The local source is constructed here rather than registered separately so the
        // elevated wrapper is the one owner that closes it, whichever mode the plugin ends up in.
        services.AddSingleton<IHardwareHostLauncher, WindowsHardwareHostLauncher>();
        services.AddSingleton<IHardwareSensorSource>(provider => new ElevatedHardwareSource(
            new LibreHardwareMonitorSource(),
            provider.GetRequiredService<IHardwareHostLauncher>(),
            provider.GetRequiredService<ILogger>()));

        // One selection shared by the integration that fills it and the provider that reads it. The
        // diagnosis sees the source so an elevated helper's answer wins over this process's own: this
        // process cannot open the driver, but the helper can.
        services.AddSingleton<SensorSelection>();
        services.AddSingleton<IAccessDiagnosis>(provider =>
            new KernelAccessDiagnosis(provider.GetRequiredService<IHardwareSensorSource>()));
    })
    .RegisterIntegration<LibreHardwareMonitorIntegration>()
    .Build();

await plugin.RunAsync();
