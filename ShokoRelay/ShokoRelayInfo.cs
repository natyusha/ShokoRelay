namespace ShokoRelay;

/// <summary>Static information about the plugin identity and defaults.</summary>
public static class ShokoRelayInfo
{
    /// <summary>Display name.</summary>
    public const string Name = "Shoko Relay";

    /// <summary>Current version string.</summary>
    public const string Version = "0.10.1";

    /// <summary>Internal API version.</summary>
    public const string ApiVersion = "1";

    /// <summary>Plex agent URI scheme identifier.</summary>
    public const string AgentScheme = "tv.plex.agents.custom.shoko";

    /// <summary>Base HTTP path for plugin endpoints.</summary>
    public const string BasePath = "/api/plugin/ShokoRelay";
}
