using System.Runtime.InteropServices;

// The plugin identity Jellyfin keys everything on (uninstall, settings file,
// repository updates). It must never change once released: it is duplicated in
// Plugin.PluginId because an assembly attribute cannot read a constant.
[assembly: Guid("d0c65588-3905-487d-989b-d0d6b601b01d")]
