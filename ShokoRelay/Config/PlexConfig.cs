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

        // Associated server info for this target
        [Browsable(false)]
        public string ServerId { get; set; } = "";

        [Browsable(false)]
        public string ServerName { get; set; } = "";

        [Browsable(false)]
        public string ServerUrl { get; set; } = "";
    }

    public class PlexLibraryConfig
    {
        [Browsable(false)]
        public string SelectedServerId { get; set; } = "";

        [Browsable(false)]
        public string SelectedServerName { get; set; } = "";

        [Browsable(false)]
        public string SelectedLibraryName { get; set; } = "";

        [Browsable(false)]
        public List<PlexLibraryTarget> SelectedLibraries { get; set; } = new();

        [Browsable(false)]
        [Display(Name = "Scan On VFS Refresh", Description = "Trigger Plex library scans when the VFS is refreshed.")]
        [DefaultValue(false)]
        public bool ScanOnVfsRefresh { get; set; } = false;

        [Display(Name = "Plex Server Url", Description = "Base URL for the Plex server (e.g. http://localhost:32400).")]
        [DefaultValue("http://localhost:32400")]
        [Browsable(false)]
        public string ServerUrl { get; set; } = "http://localhost:32400";

        [Display(Name = "Plex Token", Description = "Plex token used for server API calls.")]
        [DefaultValue("")]
        [Browsable(false)]
        public string Token { get; set; } = "";

        [Display(Name = "Library Section Id", Description = "Target library section id to refresh.")]
        [DefaultValue(0)]
        [Browsable(false)]
        public int LibrarySectionId { get; set; } = 0;

        [Display(Name = "Library Type", Description = "Plex library type for collection creation.")]
        [DefaultValue(PlexLibraryType.Show)]
        [Browsable(false)]
        public PlexLibraryType LibraryType { get; set; } = PlexLibraryType.Show;

        [Display(Name = "Client Identifier", Description = "Optional X-Plex-Client-Identifier header value.")]
        [DefaultValue("")]
        [Browsable(false)]
        public string ClientIdentifier { get; set; } = "";

        [Display(Name = "Section UUID", Description = "Optional library section UUID used in item URI templates.")]
        [DefaultValue("")]
        [Browsable(false)]
        public string SectionUuid { get; set; } = "";

        [Display(Name = "Server Identifier", Description = "Optional server machine identifier used in item URI templates.")]
        [DefaultValue("")]
        [Browsable(false)]
        public string ServerIdentifier { get; set; } = "";

        [Display(Name = "Item URI Template", Description = "Template used to build item URIs. Supports {ratingKey}, {sectionId}, {sectionUuid}, {serverId}.")]
        [DefaultValue("library://{sectionId}/item/{ratingKey}")]
        [Browsable(false)]
        public string ItemUriTemplate { get; set; } = "library://{sectionId}/item/{ratingKey}";

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
