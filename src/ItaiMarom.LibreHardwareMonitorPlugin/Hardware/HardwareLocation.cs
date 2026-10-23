namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// One device in the hardware tree, as identity rather than as data: what the plugin needs to tell two
/// sensors of the same name apart and to group them in the variable browser.
/// </summary>
/// <param name="Id">The hardware library's identifier, for example <c>/lpc/nct6776f/0</c>. Unique and stable.</param>
/// <param name="Name">The library's display name, for example <c>ITE IT8688E</c>.</param>
/// <param name="Path">
/// The tree path as <c>Parent/Child</c> names, for example <c>Motherboard/ITE IT8688E</c>. Shown to the
/// user and used to build a fallback variable name; <see cref="Id"/> is what is keyed on.
/// </param>
/// <param name="RootId">The identifier of the top-level hardware this hangs off, itself included.</param>
/// <param name="RootName">The name of that top-level hardware, for example <c>Motherboard</c>.</param>
public sealed record HardwareLocation(string Id, string Name, string Path, string RootId, string RootName)
{
	/// <summary>The location of a top-level piece of hardware.</summary>
	public static HardwareLocation Root(string id, string name) => new(id, name, name, id, name);

	/// <summary>A sub-device of this one - a motherboard's super-I/O chip, say.</summary>
	public HardwareLocation Child(string id, string name) => new(id, name, $"{Path}/{name}", RootId, RootName);
}
