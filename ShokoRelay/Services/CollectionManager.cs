using System.Diagnostics;
using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Services;
using ShokoRelay.Config;
using ShokoRelay.Helpers;
using ShokoRelay.Plex;

namespace ShokoRelay.Services
{
    public interface ICollectionManager
    {
        Task<BuildCollectionsResult> BuildCollectionsAsync(IEnumerable<IShokoSeries?> seriesList, CancellationToken cancellationToken = default);
        Task<ApplyPostersResult> ApplyCollectionPostersAsync(IEnumerable<IShokoSeries?> seriesList, CancellationToken cancellationToken = default);
    }

    public sealed record BuildCollectionsResult(
        int Processed,
        int Created,
        int Uploaded,
        int SeasonPostersUploaded,
        int Skipped,
        int Errors,
        int DeletedEmptyCollections,
        List<object> CreatedCollections,
        List<string> ErrorsList
    );

    public sealed record ApplyPostersResult(int Processed, int Uploaded, int Skipped, int Errors, List<string> ErrorsList);

    public class CollectionManager : ICollectionManager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly PlexClient _plexClient;
        private readonly PlexCollections _plexCollections;
        private readonly IMetadataService _metadataService;
        private readonly PlexMetadata _mapper;
        private readonly ConfigProvider _configProvider;

        public CollectionManager(PlexClient plexClient, PlexCollections plexCollections, IMetadataService metadataService, PlexMetadata mapper, ConfigProvider configProvider)
        {
            _plexClient = plexClient;
            _plexCollections = plexCollections;
            _metadataService = metadataService;
            _mapper = mapper;
            _configProvider = configProvider;
        }

        public async Task<BuildCollectionsResult> BuildCollectionsAsync(IEnumerable<IShokoSeries?> seriesList, CancellationToken cancellationToken = default)
        {
            var createdCollections = new List<object>();
            var errorsList = new List<string>();

            int created = 0,
                uploaded = 0,
                seasonPostersUploaded = 0,
                errors = 0;
            var addedSeries = new HashSet<int>();
            var presentSeriesUnique = new HashSet<int>();

            var allowedIds = new HashSet<int>(seriesList?.Where(s => s != null).Select(s => s!.ID) ?? Enumerable.Empty<int>());
            var targets = _plexClient.GetConfiguredTargets();

            var totalSw = Stopwatch.StartNew();
            Logger.Info("BuildCollectionsAsync: starting collection build (allowedIds={AllowedCount}, targets={TargetCount})", allowedIds.Count, targets?.Count ?? 0);

            if (targets == null || targets.Count == 0)
            {
                totalSw.Stop();
                Logger.Info("BuildCollectionsAsync: no Plex targets configured — exiting (elapsed={Elapsed}ms)", totalSw.ElapsedMilliseconds);
                return new BuildCollectionsResult(Processed: 0, Created: 0, Uploaded: 0, SeasonPostersUploaded: 0, Skipped: 0, Errors: 0, DeletedEmptyCollections: 0, createdCollections, errorsList);
            }

            var preFetchedSeries = new HashSet<int>();

            foreach (var target in targets)
            {
                var targetSw = Stopwatch.StartNew();
                int createdBeforeTarget = created;
                int uploadedBeforeTarget = uploaded;
                int errorsBeforeTarget = errors;
                int processedForTarget = 0;

                List<PlexMetadataItem> items;
                try
                {
                    items = await _plexClient.GetSectionShowsAsync(target, cancellationToken).ConfigureAwait(false);
                    Logger.Info("BuildCollectionsAsync: target {Server}:{Section} returned {Count} shows", target.ServerUrl, target.SectionId, items?.Count ?? 0);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to list section {SectionId} on {Server}", target.SectionId, target.ServerUrl);
                    errors++;
                    errorsList.Add($"Failed to list series in section {target.SectionId}: {ex.Message}");
                    continue;
                }

                // defensive: ensure items is never null for the foreach below
                items ??= new List<PlexMetadataItem>();

                var postedCollections = new HashSet<int>();

                foreach (var item in items)
                {
                    processedForTarget++;
                    if (string.IsNullOrWhiteSpace(item.Guid))
                        continue;

                    var shokoId = Helpers.PlexHelpers.ExtractShokoSeriesIdFromGuid(item.Guid);
                    if (!shokoId.HasValue)
                        continue;

                    // Allowed ids check: if allowedIds empty => process all, otherwise only process allowed ones
                    if (allowedIds.Count > 0 && !allowedIds.Contains(shokoId.Value))
                        continue;

                    presentSeriesUnique.Add(shokoId.Value);

                    var series = _metadataService.GetShokoSeriesByID(shokoId.Value);
                    if (series == null)
                    {
                        errors++;
                        errorsList.Add($"Shoko series {shokoId.Value} not found for Plex item {item.RatingKey} in section {target.SectionId}");
                        continue;
                    }

                    var collectionName = _mapper.GetCollectionName(series);
                    if (string.IsNullOrWhiteSpace(collectionName))
                        continue;

                    if (!int.TryParse(item.RatingKey, out int plexRatingKey))
                    {
                        errors++;
                        errorsList.Add($"Unable to parse Plex ratingKey '{item.RatingKey}' for Shoko series {shokoId.Value} in section {target.SectionId}");
                        continue;
                    }

                    try
                    {
                        bool ok = await _plexCollections.AssignCollectionToItemByMetadataAsync(plexRatingKey, collectionName, target, cancellationToken).ConfigureAwait(false);

                        if (ok)
                        {
                            created++;
                            addedSeries.Add(shokoId.Value);
                            createdCollections.Add(
                                new
                                {
                                    seriesId = shokoId.Value,
                                    ratingKey = plexRatingKey,
                                    collectionName,
                                    sectionId = target.SectionId,
                                }
                            );

                            try
                            {
                                int? collectionId = await _plexCollections.GetOrCreateCollectionIdAsync(collectionName, target, cancellationToken).ConfigureAwait(false);

                                if (collectionId.HasValue && !postedCollections.Contains(collectionId.Value))
                                {
                                    bool posterApplied = await TryApplyCollectionPosterAsync(series, collectionName, collectionId.Value, target, cancellationToken).ConfigureAwait(false);

                                    if (posterApplied)
                                    {
                                        uploaded++;
                                        postedCollections.Add(collectionId.Value);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn(ex, "Failed to get/create collection id for '{CollectionName}' on {Server}:{Section}", collectionName, target.ServerUrl, target.SectionId);
                            }
                        }
                        else
                        {
                            errors++;
                            errorsList.Add($"Failed to assign collection '{collectionName}' to series {shokoId.Value} (Plex ratingKey {plexRatingKey}) in section {target.SectionId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        errorsList.Add($"Exception assigning collection to series {shokoId.Value} in section {target.SectionId}: {ex.Message}");
                        Logger.Warn(ex, "AssignCollection exception for series {Series} ratingKey {RatingKey} on {Section}", shokoId.Value, item.RatingKey, target.SectionId);
                    }
                }

                // per-target summary
                targetSw.Stop();
                var createdInTarget = created - createdBeforeTarget;
                var uploadedInTarget = uploaded - uploadedBeforeTarget;
                var errorsInTarget = errors - errorsBeforeTarget;
                Logger.Info(
                    "BuildCollectionsAsync: finished target {Server}:{Section} — processed {Processed}, created {Created}, postersUploaded {Uploaded}, errors {Errors}, elapsed={Elapsed}ms",
                    target.ServerUrl,
                    target.SectionId,
                    processedForTarget,
                    createdInTarget,
                    uploadedInTarget,
                    errorsInTarget,
                    targetSw.ElapsedMilliseconds
                );
            }

            int processed = presentSeriesUnique.Count;
            int deletedEmptyCollections = await _plexCollections.DeleteEmptyCollectionsAsync(cancellationToken).ConfigureAwait(false);
            if (deletedEmptyCollections > 0)
                Logger.Info("Deleted {Count} empty Plex collections after building.", deletedEmptyCollections);

            int skipped = processed - addedSeries.Count;

            totalSw.Stop();
            Logger.Info(
                "BuildCollectionsAsync: completed — processed={Processed}, created={Created}, uploaded={Uploaded}, skipped={Skipped}, errors={Errors}, deletedEmptyCollections={Deleted}, elapsedMs={Elapsed}",
                processed,
                created,
                uploaded,
                skipped,
                errors,
                deletedEmptyCollections,
                totalSw.ElapsedMilliseconds
            );

            return new BuildCollectionsResult(
                Processed: processed,
                Created: created,
                Uploaded: uploaded,
                SeasonPostersUploaded: seasonPostersUploaded,
                Skipped: skipped,
                Errors: errors,
                DeletedEmptyCollections: deletedEmptyCollections,
                CreatedCollections: createdCollections,
                ErrorsList: errorsList
            );
        }

        public async Task<ApplyPostersResult> ApplyCollectionPostersAsync(IEnumerable<IShokoSeries?> seriesList, CancellationToken cancellationToken = default)
        {
            var errorsList = new List<string>();
            int processed = 0,
                uploaded = 0,
                skipped = 0,
                errors = 0;

            var allowedIds = new HashSet<int>(seriesList?.Where(s => s != null).Select(s => s!.ID) ?? Enumerable.Empty<int>());
            var targets = _plexClient.GetConfiguredTargets();

            if (targets == null || targets.Count == 0)
                return new ApplyPostersResult(Processed: 0, Uploaded: 0, Skipped: 0, Errors: 0, ErrorsList: errorsList);

            foreach (var target in targets)
            {
                List<PlexMetadataItem> items;
                try
                {
                    items = await _plexClient.GetSectionShowsAsync(target, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to list section {SectionId} on {Server}", target.SectionId, target.ServerUrl);
                    errors++;
                    errorsList.Add($"Failed to list series in section {target.SectionId}: {ex.Message}");
                    continue;
                }

                items ??= new List<PlexMetadataItem>();

                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.Guid))
                        continue;

                    var shokoId = Helpers.PlexHelpers.ExtractShokoSeriesIdFromGuid(item.Guid);
                    if (!shokoId.HasValue)
                        continue;

                    if (allowedIds.Count > 0 && !allowedIds.Contains(shokoId.Value))
                        continue;

                    processed++;

                    var series = _metadataService.GetShokoSeriesByID(shokoId.Value);
                    if (series == null)
                    {
                        errors++;
                        errorsList.Add($"Shoko series {shokoId.Value} not found for Plex item {item.RatingKey} in section {target.SectionId}");
                        continue;
                    }

                    var collectionName = _mapper.GetCollectionName(series);
                    if (string.IsNullOrWhiteSpace(collectionName))
                    {
                        skipped++;
                        continue;
                    }

                    int? collectionId = null;
                    try
                    {
                        collectionId = await _plexCollections.GetOrCreateCollectionIdAsync(collectionName, target, cancellationToken).ConfigureAwait(false);
                        if (collectionId == null)
                        {
                            errors++;
                            errorsList.Add($"Failed to get/create collection '{collectionName}' for series {shokoId.Value} in section {target.SectionId}");
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        errorsList.Add($"Exception getting/creating collection '{collectionName}' for series {shokoId.Value} in section {target.SectionId}: {ex.Message}");
                        continue;
                    }

                    string? posterUrl = PlexHelpers.GetCollectionPosterUrl(series, collectionName, collectionId.Value, _metadataService, _configProvider.GetSettings().CollectionPosters);

                    if (string.IsNullOrWhiteSpace(posterUrl))
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        bool ok = await _plexCollections.UploadCollectionPosterByUrlAsync(collectionId.Value, posterUrl, target, cancellationToken).ConfigureAwait(false);
                        if (ok)
                        {
                            uploaded++;
                        }
                        else
                        {
                            errors++;
                            errorsList.Add($"Failed to upload poster for collection {collectionId} in section {target.SectionId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        errorsList.Add($"Exception uploading poster for collection {collectionId} in section {target.SectionId}: {ex.Message}");
                    }
                }
            }

            return new ApplyPostersResult(Processed: processed, Uploaded: uploaded, Skipped: skipped, Errors: errors, ErrorsList: errorsList);
        }

        private async Task<bool> TryApplyCollectionPosterAsync(IShokoSeries series, string collectionName, int collectionId, PlexLibraryTarget target, CancellationToken cancellationToken)
        {
            string? posterUrl = PlexHelpers.GetCollectionPosterUrl(series, collectionName, collectionId, _metadataService, _configProvider.GetSettings().CollectionPosters);
            if (string.IsNullOrWhiteSpace(posterUrl))
                return false;

            try
            {
                return await _plexCollections.UploadCollectionPosterByUrlAsync(collectionId, posterUrl, target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "UploadCollectionPosterByUrlAsync failed for collection {CollectionId} on {Server}:{Section}", collectionId, target.ServerUrl, target.SectionId);
                return false;
            }
        }
    }
}
