using System.Collections.Concurrent;
using Shoko.QueueProcessor.Abstractions;

namespace ShokoRelay.Helpers;

/// <summary>Provides a centralized way to track long-running manual tasks for UI feedback.</summary>
public static class TaskHelper
{
    #region Setup

    /// <summary>Currently running tasks and their start times.</summary>
    public static readonly ConcurrentDictionary<string, DateTime> ActiveTasks = new();

    /// <summary>Stores the results of completed tasks so the UI can retrieve them after a refresh.</summary>
    public static readonly ConcurrentDictionary<string, object> TaskResults = new();

    /// <summary>Stores the latest completion result keyed by queue job key for synchronous <c>EnqueueImmediate</c> callers.</summary>
    private static readonly ConcurrentDictionary<string, object> s_jobResults = new(StringComparer.Ordinal);

    /// <summary>Tracks active queue job keys per dashboard task name so queued jobs removed externally are reconciled cleanly.</summary>
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> s_queuedJobsByTask = new(StringComparer.Ordinal);

    #endregion

    #region Task Lifecycle

    /// <summary>Registers a task as active.</summary>
    /// <param name="taskName">The unique identifier for the task.</param>
    /// <returns><c>true</c> if this call registered the task as active; <c>false</c> if it was already active.</returns>
    public static bool StartTask(string taskName)
    {
        if (!s_queuedJobsByTask.TryGetValue(taskName, out var jobs) || jobs.IsEmpty)
            TaskResults.TryRemove(taskName, out _);
        return ActiveTasks.TryAdd(taskName, DateTime.UtcNow);
    }

    /// <summary>Registers a queued job key under a dashboard task name when it is still waiting or executing.</summary>
    /// <param name="taskName">The unique identifier for the task.</param>
    /// <param name="jobKey">The queue job key.</param>
    /// <param name="storeForDashboard">Whether completion or external removal should populate <see cref="TaskResults"/>.</param>
    /// <param name="scheduler">The queue scheduler used to verify the job is still queued.</param>
    public static void TrackQueuedJob(string taskName, string jobKey, bool storeForDashboard, IQueueScheduler scheduler)
    {
        s_jobResults.TryRemove(jobKey, out _);
        if (!scheduler.IsQueued(jobKey))
            return;

        if (storeForDashboard)
            TaskResults.TryRemove(taskName, out _);

        var jobs = s_queuedJobsByTask.GetOrAdd(taskName, _ => new ConcurrentDictionary<string, bool>(StringComparer.Ordinal));
        jobs[jobKey] = storeForDashboard;
        ActiveTasks.TryAdd(taskName, DateTime.UtcNow);
    }

    /// <summary>Unregisters an active task without saving a result when no queued jobs for that task remain active.</summary>
    /// <param name="taskName">The unique identifier for the task.</param>
    public static void FinishTask(string taskName)
    {
        if (!s_queuedJobsByTask.TryGetValue(taskName, out var jobs) || jobs.IsEmpty)
            ActiveTasks.TryRemove(taskName, out _);
    }

    /// <summary>Moves a task from active to completed and stores the result object.</summary>
    /// <param name="taskName">The unique identifier for the task.</param>
    /// <param name="result">The result data to be displayed in the UI toast.</param>
    /// <param name="jobKey">Optional queue job key that produced this result.</param>
    /// <param name="storeForDashboard">When <c>true</c>, writes <paramref name="result"/> into <see cref="TaskResults"/> for dashboard polling.</param>
    public static void CompleteTask(string taskName, object result, string? jobKey = null, bool storeForDashboard = true)
    {
        if (!string.IsNullOrEmpty(jobKey))
        {
            s_jobResults[jobKey] = result;
            if (s_queuedJobsByTask.TryGetValue(taskName, out var jobs))
                jobs.TryRemove(jobKey, out _);
        }

        if (storeForDashboard)
            TaskResults[taskName] = result;

        if (!s_queuedJobsByTask.TryGetValue(taskName, out var remaining) || remaining.IsEmpty)
            ActiveTasks.TryRemove(taskName, out _);
    }

    /// <summary>Retrieves and removes the stored result object for a completed queue job key.</summary>
    /// <param name="jobKey">The queue job key.</param>
    /// <returns>The stored result object, or <c>null</c> if none was recorded.</returns>
    public static object? TakeJobResult(string jobKey) => s_jobResults.TryRemove(jobKey, out var result) ? result : null;

    /// <summary>Prunes tracked queue job keys that are no longer waiting or running, marking externally removed waiting jobs as cancelled.</summary>
    /// <param name="scheduler">The queue scheduler to check queued status against.</param>
    public static void ReconcileQueuedTasks(IQueueScheduler scheduler)
    {
        foreach (var (taskName, jobs) in s_queuedJobsByTask)
        {
            bool removedForDashboard = false;
            foreach (var (jobKey, storeForDashboard) in jobs.ToArray())
            {
                if (scheduler.IsQueued(jobKey))
                    continue;

                if (jobs.TryRemove(jobKey, out _))
                    removedForDashboard |= storeForDashboard;
            }

            if (jobs.IsEmpty)
            {
                ActiveTasks.TryRemove(taskName, out _);
                if (removedForDashboard && !TaskResults.ContainsKey(taskName))
                    TaskResults[taskName] = new { status = "cancelled", message = "Removed from queue." };
            }
            else
                ActiveTasks.TryAdd(taskName, DateTime.UtcNow);
        }
    }

    #endregion
}
