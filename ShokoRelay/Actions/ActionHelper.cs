using Shoko.Abstractions.Actions;
using Shoko.Abstractions.ScheduledActions;
using Shoko.Abstractions.ScheduledActions.Services;
using ShokoRelay.Actions.Scheduled;

namespace ShokoRelay.Actions;

/// <summary>Translates Shoko Relay dashboard configurations into native Shoko action triggers and provides shared execution/validation helpers.</summary>
public static class ActionScheduleHelper
{
    #region Schedule Sync

    /// <summary>Synchronizes the configured dashboard intervals with the native scheduled action service.</summary>
    /// <remarks>Shoko Relay owns the trigger schedules for <see cref="ShokoImportAction"/>, <see cref="PlexWatchedSyncAction"/>, and <see cref="PlexAutomationAction"/>, overwriting manual WebUI trigger edits on startup or settings save.</remarks>
    /// <param name="settings">The current relay configuration.</param>
    /// <param name="scheduledActionService">The native scheduled action service.</param>
    public static void SyncTriggers(RelayConfig settings, IScheduledActionService scheduledActionService)
    {
        var offset = Math.Clamp(settings.Automation.UtcOffsetHours, -12, 14);

        Sync<ShokoImportAction>(scheduledActionService, settings.Automation.ShokoImportFrequencyHours, offset);
        Sync<PlexWatchedSyncAction>(scheduledActionService, settings.Automation.ShokoSyncWatchedFrequencyHours, offset);
        Sync<PlexAutomationAction>(scheduledActionService, settings.Automation.PlexAutomationFrequencyHours, offset);
    }

    /// <summary>Translates a raw hour interval into optimal clock-aligned triggers and applies them if changed.</summary>
    /// <typeparam name="TAction">The scheduled action type to synchronize.</typeparam>
    /// <param name="scheduledActionService">The native scheduled action service.</param>
    /// <param name="freqHours">The configured interval in hours.</param>
    /// <param name="offset">The configured UTC offset in hours.</param>
    private static void Sync<TAction>(IScheduledActionService scheduledActionService, int freqHours, int offset)
        where TAction : class, IScheduledAction
    {
        var actionInfo = scheduledActionService.GetScheduledAction<TAction>();
        if (actionInfo == null)
            return;

        var triggers = new List<ActionTrigger>();
        if (freqHours > 0)
        {
            // If the interval divides evenly into 24 hours (excluding 1 hour), generate specific daily clock-aligned triggers mapped to the user's timezone via the UTC offset
            if (freqHours > 1 && freqHours <= 24 && 24 % freqHours == 0)
            {
                // Note: Uses the current local offset at sync time, so triggers can drift by 1 hour across DST transitions (fixed via a server restart or saving settings)
                double localOffsetMinutes = DateTimeOffset.Now.Offset.TotalMinutes;
                for (int hourOffset = 0; hourOffset < 24; hourOffset += freqHours)
                {
                    int totalMinutes = (((int)Math.Round(((offset + hourOffset) * 60.0) + localOffsetMinutes) % 1440) + 1440) % 1440;
                    triggers.Add(ActionTrigger.DailyAt(new TimeOnly(totalMinutes / 60, totalMinutes % 60)));
                }
            }
            else
                // For 1 hour, intervals greater than 24 hours, or non-divisors, fall back to a rolling interval
                triggers.Add(ActionTrigger.Every(TimeSpan.FromHours(freqHours)));
        }

        // Compare existing triggers with desired triggers to avoid unnecessary database writes
        bool identical = actionInfo.Triggers.Count == triggers.Count && actionInfo.Triggers.ToHashSet().SetEquals(triggers);

        if (!identical)
            scheduledActionService.SetTriggers(actionInfo.ID, triggers);
    }

    #endregion

    #region Action Helpers

    /// <summary>Validates that Plex authentication and at least one configured library target exist.</summary>
    /// <param name="plexClient">The Plex client.</param>
    /// <returns>An <see cref="ActionValidationResult"/> refusal if Plex is not configured; otherwise <c>null</c>.</returns>
    public static ActionValidationResult? ValidatePlexConfigured(PlexClient plexClient) => !plexClient.IsEnabled ? new("Plex server configuration is missing or no library is selected.") : null;

    /// <summary>Validates that Plex is configured and watched-state synchronization is enabled for at least one user group.</summary>
    /// <param name="plexClient">The Plex client.</param>
    /// <param name="configProvider">The configuration provider.</param>
    /// <returns>An <see cref="ActionValidationResult"/> refusal if disabled; otherwise <c>null</c>.</returns>
    public static ActionValidationResult? ValidateWatchedSync(PlexClient plexClient, ConfigProvider configProvider) =>
        ValidatePlexConfigured(plexClient) ?? (configProvider.GetSettings().Automation.ShokoSyncWatchedUserType == SyncUserType.None ? new("Watched sync is disabled because Sync Users is set to None.") : null);

    /// <summary>Executes an action body under a concurrency semaphore while tracking task active state for the dashboard UI.</summary>
    /// <param name="taskName">The unique task identifier registered in <see cref="TaskHelper"/>.</param>
    /// <param name="semaphore">The concurrency semaphore protecting the operation.</param>
    /// <param name="action">The asynchronous work to execute.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the synchronized execution.</returns>
    public static async Task RunWithTaskLockAsync(string taskName, SemaphoreSlim semaphore, Func<Task> action, CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool startedTask = false;
        try
        {
            startedTask = TaskHelper.StartTask(taskName);
            await action().ConfigureAwait(false);
        }
        finally
        {
            if (startedTask)
                TaskHelper.FinishTask(taskName);
            semaphore.Release();
        }
    }

    #endregion
}
