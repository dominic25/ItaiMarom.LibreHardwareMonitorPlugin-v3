using System.Globalization;
using MacroDeck.Localization;

namespace ItaiMarom.LibreHardwareMonitorPlugin;

/// <summary>
/// Resolves the plugin's localized strings into plain text where the host cannot do it for us - a
/// notification message, a text variable's value, or a literal the page has to carry.
/// </summary>
internal static class LocalizedTexts
{
	private static readonly LocalizationResolver Resolver = CreateResolver();

	/// <summary>Resolves against the process's UI culture, with an empty string when nothing matches.</summary>
	internal static string Resolve(LocalizedText text) =>
		Resolver.Resolve(text, CultureInfo.CurrentUICulture.Name) ?? string.Empty;

	private static LocalizationResolver CreateResolver()
	{
		var catalogs = new LocalizationCatalogRegistry();
		catalogs.Register(Strings.LocalizationCatalog);

		return new LocalizationResolver(catalogs);
	}
}
