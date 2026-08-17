using Shoko.Abstractions.Metadata;

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

    /// <summary>Gets the primary Shoko series ID according to VFS override groups and TMDB configuration.</summary>
    /// <param name="s">The series metadata.</param>
    /// <param name="metadataService">The metadata service used for override resolution.</param>
    /// <returns>The resolved primary Shoko series ID.</returns>
    public static int GetPrimaryId(this ISeries s, IMetadataService metadataService) => OverrideHelper.GetPrimary(s.ID, metadataService);

    /// <summary>Gets the collection of series IDs associated with this series in an override group.</summary>
    /// <param name="s">The series metadata.</param>
    /// <param name="metadataService">The metadata service used for override resolution.</param>
    /// <returns>A list of Shoko series IDs with the primary series first.</returns>
    public static IReadOnlyList<int> GetOverrideGroup(this ISeries s, IMetadataService metadataService) => OverrideHelper.GetGroup(s.ID, metadataService);

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
        $"{ShokoRelayConstants.AgentScheme}://episode/{PlexConstants.EpisodePrefix}{e.ID}{(partIndex.HasValue ? $"{PlexConstants.PartPrefix}{partIndex}" : "")}";

    /// <summary>Gets the Plex rating key for an episode, with optional part indexing.</summary>
    /// <param name="e">The episode metadata.</param>
    /// <param name="partIndex">Optional index for multi-part files.</param>
    /// <returns>A Plex-compatible episode rating key string.</returns>
    public static string GetPlexRatingKey(this IEpisode e, int? partIndex = null) => $"{PlexConstants.EpisodePrefix}{e.ID}{(partIndex.HasValue ? $"{PlexConstants.PartPrefix}{partIndex}" : "")}";

    #endregion

    #region Movies

    /// <summary>Gets the Plex metadata GUID for a movie-type episode.</summary>
    /// <param name="e">The episode metadata.</param>
    /// <returns>A Plex-compatible movie GUID string.</returns>
    public static string GetPlexMovieGuid(this IEpisode e) => $"{ShokoRelayConstants.MovieAgentScheme}://movie/{PlexConstants.MoviePrefix}{e.ID}";

    /// <summary>Gets the Plex rating key for a movie-type episode.</summary>
    /// <param name="e">The episode metadata.</param>
    /// <returns>A Plex-compatible movie rating key string.</returns>
    public static string GetPlexMovieRatingKey(this IEpisode e) => $"{PlexConstants.MoviePrefix}{e.ID}";

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
