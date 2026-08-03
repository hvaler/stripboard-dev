using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;

namespace Stripboard.Domain.Services;

/// <summary>
/// Domain service implementing SAG-AFTRA / IATSE union rules (§5 / ADR-003).
/// Pure domain logic with no external dependencies.
/// </summary>
public class UnionRulesService
{
    public static readonly TimeSpan MinimumTurnaroundHours = TimeSpan.FromHours(12);
    public static readonly TimeSpan MaximumContinuousWorkBeforeMeal = TimeSpan.FromHours(6);

    /// <summary>
    /// Validates the 12-hour turnaround rule between the wrap of a previous shoot day
    /// and the call time of the current shoot day.
    /// </summary>
    /// <param name="previousDay">The preceding shoot day.</param>
    /// <param name="currentDay">The following shoot day.</param>
    /// <returns>An Anomaly if turnaround is violated (< 12 hours); otherwise null.</returns>
    public Anomaly? ValidateTurnaround(ShootDay previousDay, ShootDay currentDay)
    {
        ArgumentNullException.ThrowIfNull(previousDay);
        ArgumentNullException.ThrowIfNull(currentDay);

        var prevWrap = previousDay.GetWrapDateTime();
        var currCall = currentDay.GetCallDateTime();

        var restDuration = currCall - prevWrap;

        if (restDuration < MinimumTurnaroundHours)
        {
            var hoursFormatted = restDuration.TotalHours.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            return new Anomaly(
                id: Guid.NewGuid(),
                severity: AnomalySeverity.Critical,
                type: AnomalyType.TurnaroundViolation,
                message: $"Turnaround violation between Day {previousDay.DayNumber} and Day {currentDay.DayNumber}: rest duration was {hoursFormatted}h (minimum required is 12.00h).",
                sceneIds: currentDay.StripIds
            );
        }

        return null;
    }

    /// <summary>
    /// Validates the meal penalty rule (continuous work > 6 hours without a meal break).
    /// </summary>
    /// <param name="shootDay">The shoot day being evaluated.</param>
    /// <param name="continuousWorkDuration">The duration of continuous work.</param>
    /// <returns>An Anomaly if work duration exceeds 6 hours; otherwise null.</returns>
    public Anomaly? ValidateMealPenalty(ShootDay shootDay, TimeSpan continuousWorkDuration)
    {
        ArgumentNullException.ThrowIfNull(shootDay);

        if (continuousWorkDuration > MaximumContinuousWorkBeforeMeal)
        {
            var hoursFormatted = continuousWorkDuration.TotalHours.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            return new Anomaly(
                id: Guid.NewGuid(),
                severity: AnomalySeverity.High,
                type: AnomalyType.MealPenaltyRisk,
                message: $"Meal penalty risk on Day {shootDay.DayNumber}: continuous work duration of {hoursFormatted}h exceeds 6.00h limit without a meal break.",
                sceneIds: shootDay.StripIds
            );
        }

        return null;
    }

    /// <summary>
    /// Validates night to day transition rules.
    /// Flags tight transitions when an overnight shoot is immediately followed by a morning call.
    /// </summary>
    /// <param name="previousDay">The preceding night shoot day.</param>
    /// <param name="currentDay">The following day shoot day.</param>
    /// <param name="isPreviousNight">True if previous day was a night shoot.</param>
    /// <param name="isCurrentDay">True if current day is a day shoot.</param>
    /// <returns>An Anomaly if the transition is invalid/risky; otherwise null.</returns>
    public Anomaly? ValidateNightDayTransition(
        ShootDay previousDay,
        ShootDay currentDay,
        bool isPreviousNight,
        bool isCurrentDay)
    {
        ArgumentNullException.ThrowIfNull(previousDay);
        ArgumentNullException.ThrowIfNull(currentDay);

        if (isPreviousNight && isCurrentDay)
        {
            var turnaroundAnomaly = ValidateTurnaround(previousDay, currentDay);
            if (turnaroundAnomaly != null)
            {
                return turnaroundAnomaly;
            }

            var restDuration = currentDay.GetCallDateTime() - previousDay.GetWrapDateTime();
            // Even if >= 12h, a night-to-day transition with < 14h rest causes circadian fatigue
            if (restDuration < TimeSpan.FromHours(14))
            {
                var hoursFormatted = restDuration.TotalHours.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                return new Anomaly(
                    id: Guid.NewGuid(),
                    severity: AnomalySeverity.High,
                    type: AnomalyType.NightDayTransition,
                    message: $"Night to Day transition risk between Day {previousDay.DayNumber} and Day {currentDay.DayNumber}: rest duration of {hoursFormatted}h is tight for circadian adjustment.",
                    sceneIds: currentDay.StripIds
                );
            }
        }

        return null;
    }
}
