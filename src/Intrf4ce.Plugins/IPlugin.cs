namespace Intrf4ce.Plugins;

/// <summary>
/// What a plugin's entry assembly provides: one public class, with a constructor that takes
/// nothing. intrf4ce makes one when it loads the plugin and asks it what it adds.
/// </summary>
public interface IPlugin
{
    void Register(IPluginRegistrar registrar);
}

/// <summary>Where a plugin says what it adds.</summary>
public interface IPluginRegistrar
{
    /// <summary>The plugin's id, from its plugin.json: what its tool names begin with.</summary>
    string PluginId { get; }

    /// <summary>The network, the clock, settings and the log, for the plugin's tools; its elements get the same in their host.</summary>
    PluginServices Services { get; }

    /// <summary>Adds a kind of element the agent can put on a slide.</summary>
    /// <exception cref="PluginException">The name is taken or not a valid name.</exception>
    void AddElement(ElementType type);

    /// <summary>Adds a tool the agent can call.</summary>
    /// <exception cref="PluginException">The name does not begin with the plugin's id, or is taken.</exception>
    void AddTool(PluginTool tool);
}

/// <summary>A plugin that cannot be read, loaded or registered; the message is for the user.</summary>
public sealed class PluginException : Exception
{
    public PluginException(string message)
        : base(message)
    {
    }

    public PluginException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
