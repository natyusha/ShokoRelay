using System.Collections.Concurrent;

namespace ShokoRelay.Helpers;

/// <summary>
/// Provides a centralized way to track long-running manual tasks for UI feedback.
/// </summary>
public static class TaskHelper
{
    /// <summary>
    /// Currently running tasks and their start times.
    /// </summary>
    public static readonly ConcurrentDictionary<string, DateTime> ActiveTasks = new();

    /// <summary>
    /// Stores the results of completed tasks so the UI can retrieve them after a refresh.
    /// </summary>
    public static readonly ConcurrentDictionary<string, object> TaskResults = new();

    /// <summary>Registers a task as active.</summary>
    /// <param name="taskName">The unique identifier for the task.</param>
    public static void StartTask(string taskName)
    {
        TaskResults.TryRemove(taskName, out _);
        ActiveTasks.TryAdd(taskName, DateTime.UtcNow);
    }

    /// <summary>Unregisters an active task without saving a result.</summary>
    /// <param name="taskName">The unique identifier for the task.</param>
    public static void FinishTask(string taskName) => ActiveTasks.TryRemove(taskName, out _);

    /// <summary>Moves a task from active to completed and stores the result object.</summary>
    /// <param name="taskName">The unique identifier for the task.</param>
    /// <param name="result">The result data to be displayed in the UI toast.</param>
    public static void CompleteTask(string taskName, object result)
    {
        ActiveTasks.TryRemove(taskName, out _);
        TaskResults[taskName] = result;
    }
}
