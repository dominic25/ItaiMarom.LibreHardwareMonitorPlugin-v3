using ItaiMarom.LibreHardwareMonitorPlugin;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;
using Microsoft.Extensions.DependencyInjection;

// Identity, description and icon are not set here: they come from manifest.json at the content root.
// Strings is generated from Localization/*.resx, so UseLocalization is what makes every LocalizedText
// resolve in the user's language rather than falling back to its key.
var plugin = MacroDeckPlugin.CreatePlugin(args)
    .UseMacroDeckLogging()
    .UseLocalization(Strings.LocalizationCatalog)
    .ConfigureServices((_, services) =>
    {
        // One hardware library for the process, owned by the loop that reads it, and one selection shared
        // by the integration that fills it and the provider that reads it. The diagnosis is a service so
        // the issue it feeds can be tested against a scripted answer instead of this machine's driver.
        services.AddSingleton<IHardwareSensorSource, LibreHardwareMonitorSource>();
        services.AddSingleton<SensorSelection>();
        services.AddSingleton<IAccessDiagnosis, KernelAccessDiagnosis>();
    })
    .RegisterIntegration<LibreHardwareMonitorIntegration>()
    .Build();

await plugin.RunAsync();
