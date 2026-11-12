using System.Runtime.InteropServices;

// The plugin identity Jellyfin keys everything on (uninstall, settings file,
// repository updates). It must never change once released: it is duplicated in
// Plugin.PluginId because an assembly attribute cannot read a constant.
[assembly: Guid("eedbc5ae-56c4-4145-9a2b-c12716963c95")]
