using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Shoko.QueueProcessor.Abstractions;
using Shoko.QueueProcessor.Builder;
using Shoko.QueueProcessor.Concurrency;
using Shoko.QueueProcessor.Workers;
using ShokoRelay.Controllers;

namespace ShokoRelay.Jobs;

/// <summary>Base class for all Shoko Relay queue jobs providing progress, cancellation, and dashboard tracking.</summary>
/// <param name="cancellation">Scoped queue cancellation token accessor.</param>
/// <param name="progress">Scoped queue progress reporting accessor.</param>
/// <param name="logger">Logger instance.</param>
[LongRunning]
[RetryPolicy(MaxRetries = 0)]
public abstract class RelayJobBase(IJobCancellationAccessor cancellation, IJobProgressAccessor progress, ILogger logger) : IQueueJob
{
    #region Setup & Properties

    /// <summary>The controller API base URL used when generating report links.</summary>
    [JobKeyIgnore]
    public string ApiBase { get; set; } = string.Empty;

    /// <summary>Whether completion and error results should be surfaced via dashboard task polling.</summary>
    [JobKeyIgnore]
    public bool TrackInDashboard { get; set; } = true;

    /// <summary>The canonical queue job key computed by <see cref="JobKeyBuilder{T}"/> at enqueue time.</summary>
    [JobKeyIgnore]
    public string TrackedJobKey { get; set; } = string.Empty;

    /// <summary>The cancellation token bound to this queue job execution.</summary>
    [JsonIgnore]
    protected CancellationToken CancellationToken => cancellation.Token;

    /// <summary>The progress reporter bound to this queue job execution.</summary>
    [JsonIgnore]
    protected IProgress<decimal> Progress => progress.Progress;

    /// <inheritdoc/>
    [JsonIgnore]
    public abstract string TypeName { get; }

    /// <inheritdoc/>
    [JsonIgnore]
    public abstract string Title { get; }

    /// <inheritdoc/>
    [JsonIgnore]
    public virtual Dictionary<string, object> Details => [];

    #endregion

    #region Execution

    /// <inheritdoc/>
    public abstract Task Process();

    /// <summary>Executes a tracked job operation, writes its report file, and propagates cancellation or failure cleanly to the queue worker.</summary>
    /// <typeparam name="TResult">The operation result payload type.</typeparam>
    /// <param name="taskName">The dashboard task identifier.</param>
    /// <param name="reportFileName">The target report filename.</param>
    /// <param name="reportBuilder">The report formatter delegate.</param>
    /// <param name="executeAsync">The asynchronous work to run.</param>
    /// <param name="isCancelled">Optional predicate evaluating whether a returned result ended early due to cancellation.</param>
    /// <param name="semaphore">Optional concurrency semaphore protecting shared resources.</param>
    /// <returns>A task representing the synchronized job execution.</returns>
    protected async Task ExecuteWithTaskTrackingAsync<TResult>(
        string taskName,
        string reportFileName,
        Action<StringBuilder, TResult> reportBuilder,
        Func<Task<TResult>> executeAsync,
        Func<TResult, bool>? isCancelled = null,
        SemaphoreSlim? semaphore = null
    )
    {
        string jobKey = !string.IsNullOrEmpty(TrackedJobKey) ? TrackedJobKey : JobKeyBuilder.BuildFor(this);
        if (semaphore != null)
            await semaphore.WaitAsync(CancellationToken).ConfigureAwait(false);

        TaskHelper.StartTask(taskName);
        Progress.Report(0);

        string resolvedBase = string.IsNullOrWhiteSpace(ApiBase) ? $"{ServerBaseUrl}{ShokoRelayConstants.BasePath}" : ApiBase;
        string reportUrl = $"{resolvedBase}/reports/{reportFileName}";
        bool wroteReport = false;

        try
        {
            TResult result = await executeAsync().ConfigureAwait(false);
            ReportHelper.WriteReport(reportFileName, result, reportBuilder, logger);
            wroteReport = true;

            bool cancelled = isCancelled?.Invoke(result) == true || CancellationToken.IsCancellationRequested;
            object response = cancelled
                ? new ShokoRelayBaseController.RelayResponse<TResult>(Status: "cancelled", Message: "Task was cancelled.", Data: result, ReportUrl: reportUrl)
                : new ShokoRelayBaseController.RelayResponse<TResult>(Data: result, ReportUrl: reportUrl);

            TaskHelper.CompleteTask(taskName, response, jobKey, storeForDashboard: TrackInDashboard);
            if (cancelled)
                throw new OperationCanceledException(CancellationToken);

            Progress.Report(100);
        }
        catch (OperationCanceledException)
        {
            TaskHelper.CompleteTask(
                taskName,
                new ShokoRelayBaseController.RelayResponse<object>(Status: "cancelled", Message: "Task was cancelled.", ReportUrl: wroteReport ? reportUrl : null),
                jobKey,
                storeForDashboard: TrackInDashboard
            );
            throw;
        }
        catch (Exception exception)
        {
            TaskHelper.CompleteTask(taskName, new ShokoRelayBaseController.RelayResponse<object>(Status: "error", Message: exception.Message), jobKey, storeForDashboard: TrackInDashboard);
            throw;
        }
        finally
        {
            semaphore?.Release();
        }
    }

    /// <summary>Produces a compact, key-length-safe identifier for comma-separated series filter strings.</summary>
    /// <param name="filter">The normalized filter string.</param>
    /// <returns><c>"all"</c>, the short filter string, or a 16-character SHA-256 hex digest for long filter lists.</returns>
    public static string ComputeFilterKey(string? filter) =>
        string.IsNullOrWhiteSpace(filter) ? "all"
        : filter.Length <= 64 ? filter
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(filter)))[..16];

    /// <summary>Parses a comma-separated list of integer primary series IDs.</summary>
    /// <param name="filter">The normalized comma-separated filter string.</param>
    /// <returns>The parsed list of series IDs.</returns>
    protected static List<int> ParsePrimaryFilterIds(string? filter) =>
        string.IsNullOrWhiteSpace(filter) ? [] : [.. filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse)];

    #endregion
}
