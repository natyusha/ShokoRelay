using Shoko.Abstractions.ScheduledActions;
using Shoko.Abstractions.ScheduledActions.Services;

namespace ShokoRelay.Actions;

/// <summary>Translates Shoko Relay dashboard configurations into native Shoko action triggers.</summary>
public static class ActionScheduleHelper
{
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
    private static void Sync<TAction>(IScheduledActionService svc, int freqHours, int offset)
        where TAction : class, IScheduledAction
    {
        var actionInfo = svc.GetScheduledAction<TAction>();
        if (actionInfo == null)
            return;

        var triggers = new List<ActionTrigger>();
        if (freqHours > 0)
        {
            // If the interval divides evenly into 24 hours, generate specific daily clock-aligned triggers mapped to the user's timezone via the UTC offset
            if (freqHours <= 24 && 24 % freqHours == 0)
            {
                for (int i = 0; i < 24; i += freqHours)
                {
                    // Calculate the UTC hour and map it to the local server time
                    int utcHour = (offset + i + 24) % 24;
                    var utcTime = new DateTime(2000, 1, 1, utcHour, 0, 0, DateTimeKind.Utc);
                    var localTime = TimeZoneInfo.ConvertTimeFromUtc(utcTime, TimeZoneInfo.Local);

                    triggers.Add(ActionTrigger.DailyAt(TimeOnly.FromDateTime(localTime)));
                }
            }
            else
            {
                // For intervals greater than 24 hours or non-divisors, fall back to a rolling interval
                triggers.Add(ActionTrigger.Every(TimeSpan.FromHours(freqHours)));
            }
        }

        // Compare existing triggers with desired triggers to avoid unnecessary database writes
        bool identical = actionInfo.Triggers.Count == triggers.Count && actionInfo.Triggers.All(t => triggers.Any(newT => newT.Equals(t)));

        if (!identical)
            svc.SetTriggers(actionInfo.ID, triggers);
    }
}
