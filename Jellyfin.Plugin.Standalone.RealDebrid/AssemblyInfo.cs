using System.Runtime.InteropServices;

// The plugin identity Jellyfin keys everything on (uninstall, settings file,
// repository updates). It must never change once released: it is duplicated in
// Plugin.PluginId because an assembly attribute cannot read a constant.
[assembly: Guid("6d1185f2-1db4-4f9d-bca8-1cb24aa1fdb7")]
