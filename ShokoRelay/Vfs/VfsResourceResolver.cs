using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Providers;

namespace ShokoRelay.Vfs;

/// <summary>Contributes external links on AniDB/Shoko series pointing directly to the matching entry in the VFS Browser.</summary>
/// <param name="serviceProvider">Root service provider used to lazily resolve <see cref="IMetadataService"/> without circular construction.</param>
public class VfsResourceResolver(IServiceProvider serviceProvider) : IResourceResolver
{
    #region Resource Resolution

    /// <inheritdoc/>
    public string Name => $"{ShokoRelayConstants.Name}: VFS Browser";

    /// <inheritdoc/>
    public IReadOnlyList<Resource> Resolve(IWithResources entity)
    {
        if (entity is not IAnidbAnime anime || serviceProvider.GetService<IMetadataService>() is not { } metadataService)
            return [];

        if (metadataService.GetShokoSeriesByAnidbID(anime.AnidbID) is not { } shokoSeries)
            return [];

        int primaryId = shokoSeries.GetPrimaryId(metadataService);
        return
        [
            new()
            {
                Type = ResourceType.CrossReference,
                Name = Name,
                Title = "VFS Browser",
                ID = primaryId.ToString(),
                Url = $"{ShokoRelayConstants.BasePath}/browser?seriesId={primaryId}",
            },
        ];
    }

    #endregion
}
