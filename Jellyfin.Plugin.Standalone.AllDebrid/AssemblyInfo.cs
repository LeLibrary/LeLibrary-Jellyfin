using System.Runtime.InteropServices;

// The plugin identity Jellyfin keys everything on (uninstall, settings file,
// repository updates). It must never change once released: it is duplicated in
// Plugin.PluginId because an assembly attribute cannot read a constant.
[assembly: Guid("7aa38d7a-bd3f-4b1d-81be-49f9cc1bf09d")]
