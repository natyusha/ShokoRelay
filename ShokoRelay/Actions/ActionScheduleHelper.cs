using Shoko.Abstractions.ScheduledActions;
using Shoko.Abstractions.ScheduledActions.Services;

namespace ShokoRelay.Actions;

/// <summary>Translates Shoko Relay dashboard configurations into native Shoko action triggers.</summary>
public static class ActionScheduleHelper
{
    #region Schedule Sync

    /// <summary>Synchronizes the configured dashboard intervals with the native scheduled action service.</summary>
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
                var todayUtc = DateTime.UtcNow.Date;
                for (int hourOffset = 0; hourOffset < 24; hourOffset += freqHours)
                {
                    // Calculate the UTC hour and map it to the local server time
                    int utcHour = (offset + hourOffset + 24) % 24;
                    var utcTime = DateTime.SpecifyKind(todayUtc.AddHours(utcHour), DateTimeKind.Utc);
                    var localTime = TimeZoneInfo.ConvertTimeFromUtc(utcTime, TimeZoneInfo.Local);

                    triggers.Add(ActionTrigger.DailyAt(TimeOnly.FromDateTime(localTime)));
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
}
