using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Shoko;
using ShokoRelay.Plex;

namespace ShokoRelay.Helpers;

/// <summary>Extension methods for Shoko metadata abstractions to provide Plex-compatible identifiers.</summary>
public static class ShokoExtensionHelper
{
    #region Series (Shows)

    /// <summary>Gets the Plex metadata GUID for a series.</summary>
    /// <param name="s">The series metadata.</param>
    /// <returns>A Plex-compatible GUID string.</returns>
    public static string GetPlexGuid(this ISeries s) => $"{ShokoRelayConstants.AgentScheme}://show/{s.ID}";

    /// <summary>Gets the Plex rating key for a series.</summary>
    /// <param name="s">The series metadata.</param>
    /// <returns>A Plex-compatible rating key string.</returns>
    public static string GetPlexRatingKey(this ISeries s) => s.ID.ToString();

    #endregion

    #region Seasons

    /// <summary>Gets the Plex metadata GUID for a specific season of a series.</summary>
    /// <param name="s">The series metadata.</param>
    /// <param name="seasonNumber">The Plex season index.</param>
    /// <returns>A Plex-compatible season GUID string.</returns>
    public static string GetPlexGuid(this ISeries s, int seasonNumber) => $"{ShokoRelayConstants.AgentScheme}://season/{s.ID}{PlexConstants.SeasonPrefix}{seasonNumber}";

    /// <summary>Gets the Plex rating key for a specific season of a series.</summary>
    /// <param name="s">The series metadata.</param>
    /// <param name="seasonNumber">The Plex season index.</param>
    /// <returns>A Plex-compatible season rating key string.</returns>
    public static string GetPlexRatingKey(this ISeries s, int seasonNumber) => $"{s.ID}{PlexConstants.SeasonPrefix}{seasonNumber}";

    #endregion

    #region Episodes

    /// <summary>Gets the Plex metadata GUID for an episode, with optional part indexing.</summary>
    /// <param name="e">The episode metadata.</param>
    /// <param name="partIndex">Optional index for multi-part files.</param>
    /// <returns>A Plex-compatible episode GUID string.</returns>
    public static string GetPlexGuid(this IEpisode e, int? partIndex = null) =>
        partIndex.HasValue
            ? $"{ShokoRelayConstants.AgentScheme}://episode/{PlexConstants.EpisodePrefix}{e.ID}{PlexConstants.PartPrefix}{partIndex}"
            : $"{ShokoRelayConstants.AgentScheme}://episode/{PlexConstants.EpisodePrefix}{e.ID}";

    /// <summary>Gets the Plex rating key for an episode, with optional part indexing.</summary>
    /// <param name="e">The episode metadata.</param>
    /// <param name="partIndex">Optional index for multi-part files.</param>
    /// <returns>A Plex-compatible episode rating key string.</returns>
    public static string GetPlexRatingKey(this IEpisode e, int? partIndex = null) =>
        partIndex.HasValue ? $"{PlexConstants.EpisodePrefix}{e.ID}{PlexConstants.PartPrefix}{partIndex}" : $"{PlexConstants.EpisodePrefix}{e.ID}";

    #endregion

    #region Groups (Collections)

    /// <summary>Gets the Plex metadata GUID for a Shoko group.</summary>
    /// <param name="g">The Shoko group metadata.</param>
    /// <returns>A Plex-compatible collection GUID string.</returns>
    public static string GetPlexGuid(this IShokoGroup g) => $"{ShokoRelayConstants.AgentScheme}://collections/{g.ID}";

    /// <summary>Gets the Plex rating key for a Shoko group.</summary>
    /// <param name="g">The Shoko group metadata.</param>
    /// <returns>A Plex-compatible collection rating key string.</returns>
    public static string GetPlexRatingKey(this IShokoGroup g) => $"{PlexConstants.CollectionPrefix}{g.ID}";

    #endregion
}
