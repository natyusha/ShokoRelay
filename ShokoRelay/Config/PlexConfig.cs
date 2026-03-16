using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace ShokoRelay.Config;

#region Enums

/// <summary>Plex library section types used when filtering or identifying libraries.</summary>
public enum PlexLibraryType
{
    /// <summary>Movie library.</summary>
    Movie = 1,

    /// <summary>TV Show library.</summary>
    Show = 2,

    /// <summary>Music/Artist library.</summary>
    Music = 8,

    /// <summary>Photo library.</summary>
    Photo = 13,
}

#endregion

#region Configuration

/// <summary>Credentials required to initiate Plex authentication flows from the dashboard.</summary>
public class PlexAuthConfig
{
    /// <summary>Plex client identifier.</summary>
    [Display(Name = "Client Identifier", Description = "Plex client identifier.")]
    [DefaultValue("")]
    public string ClientIdentifier { get; set; } = "";
}

/// <summary>Configuration options specific to an individual Plex library.</summary>
public class PlexLibraryConfig
{
    /// <summary>Plex token used for server API calls.</summary>
    [Display(Name = "Plex Token", Description = "Plex token used for server API calls.")]
    [DefaultValue("")]
    [Browsable(false)]
    public string Token { get; set; } = "";

    /// <summary>Optional X-Plex-Client-Identifier header value.</summary>
    [Display(Name = "Client Identifier", Description = "Optional X-Plex-Client-Identifier header value.")]
    [DefaultValue("")]
    [Browsable(false)]
    public string ClientIdentifier { get; set; } = "";

    /// <summary>Cached list of discovered servers.</summary>
    [Browsable(false)]
    public List<PlexAvailableServer> DiscoveredServers { get; set; } = [];

    /// <summary>Cached list of discovered libraries.</summary>
    [Browsable(false)]
    public List<PlexAvailableLibrary> DiscoveredLibraries { get; set; } = [];
}

#endregion

#region Targets

/// <summary>Runtime representation of a Plex library section discovered on a server.</summary>
public class PlexLibraryTarget
{
    /// <summary>Section ID.</summary>
    [Browsable(false)]
    public int SectionId { get; set; } = 0;

    /// <summary>Section title.</summary>
    [Browsable(false)]
    public string Title { get; set; } = "";

    /// <summary>Section type.</summary>
    [Browsable(false)]
    public string Type { get; set; } = "";

    /// <summary>Section UUID.</summary>
    [Browsable(false)]
    public string Uuid { get; set; } = "";

    /// <summary>Plex internal type ID.</summary>
    [Browsable(false)]
    public PlexLibraryType LibraryType { get; set; } = PlexLibraryType.Show;

    /// <summary>Parent server ID.</summary>
    [Browsable(false)]
    public string ServerId { get; set; } = "";

    /// <summary>Parent server name.</summary>
    [Browsable(false)]
    public string ServerName { get; set; } = "";

    /// <summary>Base server URL.</summary>
    [Browsable(false)]
    public string ServerUrl { get; set; } = "";
}

#endregion

#region Discovery

/// <summary>Information about a Plex server discovered during authentication.</summary>
public class PlexAvailableServer
{
    /// <summary>Server UUID.</summary>
    public string Id { get; set; } = "";

    /// <summary>Server name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Best connection URI.</summary>
    public string PreferredUri { get; set; } = "";
}

/// <summary>Metadata describing a library section exposed by a Plex server.</summary>
public class PlexAvailableLibrary
{
    /// <summary>Section ID.</summary>
    public int Id { get; set; }

    /// <summary>Section title.</summary>
    public string Title { get; set; } = "";

    /// <summary>Section type.</summary>
    public string Type { get; set; } = "";

    /// <summary>Metadata agent ID.</summary>
    public string Agent { get; set; } = "";

    /// <summary>Section UUID.</summary>
    public string Uuid { get; set; } = "";

    /// <summary>Parent server ID.</summary>
    [Browsable(false)]
    public string ServerId { get; set; } = "";

    /// <summary>Parent server name.</summary>
    [Browsable(false)]
    public string ServerName { get; set; } = "";

    /// <summary>Base server URL.</summary>
    [Browsable(false)]
    public string ServerUrl { get; set; } = "";
}

#endregion
