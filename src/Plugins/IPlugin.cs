using System.Collections.Generic;

namespace SnapDoc.Plugins;

/// <summary>
/// Plugin contract. Plugins can add exporters, annotation tools, or post-capture actions.
///
/// DEFERRED BY DESIGN: a plugin system is a large architectural commitment that adds no value
/// until real users ask for extension points. The interface exists so the app is *structured*
/// for plugins, but PluginHost does not load external assemblies yet. Build this only once the
/// core app has users and a clear extension need.
/// </summary>
public interface IPlugin
{
    string Name { get; }
    string Version { get; }
    void Initialize(IPluginContext context);
}

/// <summary>What a plugin is allowed to touch. Kept deliberately narrow; widen as needs appear.</summary>
public interface IPluginContext
{
    /// <summary>Register a new export format.</summary>
    void RegisterExporter(Export.IExporter exporter);

    /// <summary>Register a post-capture action (e.g. "upload to X"), shown in the capture toolbar.</summary>
    void RegisterPostCaptureAction(string name, System.Action<Models.Capture> action);
}
