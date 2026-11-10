using System.Runtime.InteropServices;

// The plugin identity Jellyfin keys everything on (uninstall, settings file,
// repository updates). It must never change once released: it is duplicated in
// Plugin.PluginId because an assembly attribute cannot read a constant.
[assembly: Guid("8858565b-f937-4d4d-b081-57044455fda1")]
