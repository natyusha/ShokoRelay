using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace ShokoRelay.Config
{
    public class PlexAuthConfig
    {
        [Display(Name = "Client Identifier", Description = "Plex client identifier.")]
        [DefaultValue("")]
        public string ClientIdentifier { get; set; } = "";
    }

    public enum PlexLibraryType
    {
        Movie = 1,
        Show = 2,
        Music = 8,
        Photo = 13,
    }

    public class PlexLibraryTarget
    {
        [Browsable(false)]
        public int SectionId { get; set; } = 0;

        [Browsable(false)]
        public string Title { get; set; } = "";

        [Browsable(false)]
        public string Type { get; set; } = "";

        [Browsable(false)]
        public string Uuid { get; set; } = "";

        [Browsable(false)]
        public PlexLibraryType LibraryType { get; set; } = PlexLibraryType.Show;

        [Browsable(false)]
        public string ServerId { get; set; } = "";

        [Browsable(false)]
        public string ServerName { get; set; } = "";

        [Browsable(false)]
        public string ServerUrl { get; set; } = "";
    }

    public class PlexLibraryConfig
    {
        [Display(Name = "Plex Token", Description = "Plex token used for server API calls.")]
        [DefaultValue("")]
        [Browsable(false)]
        public string Token { get; set; } = "";

        [Display(Name = "Client Identifier", Description = "Optional X-Plex-Client-Identifier header value.")]
        [DefaultValue("")]
        [Browsable(false)]
        public string ClientIdentifier { get; set; } = "";

        [Browsable(false)]
        public List<PlexAvailableServer> DiscoveredServers { get; set; } = new();

        [Browsable(false)]
        public List<PlexAvailableLibrary> DiscoveredLibraries { get; set; } = new();
    }

    public class PlexAvailableServer
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string PreferredUri { get; set; } = "";
    }

    public class PlexAvailableLibrary
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Type { get; set; } = "";
        public string Agent { get; set; } = "";
        public string Uuid { get; set; } = "";

        // Server association
        [Browsable(false)]
        public string ServerId { get; set; } = "";

        [Browsable(false)]
        public string ServerName { get; set; } = "";

        [Browsable(false)]
        public string ServerUrl { get; set; } = "";
    }
}
