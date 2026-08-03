namespace Stripboard.Domain.Enums;

/// <summary>
/// Type of schedule anomaly or union rule violation.
/// </summary>
public enum AnomalyType
{
    TurnaroundViolation = 1,
    MealPenaltyRisk = 2,
    CastUnavailable = 3,
    PermitExpired = 4,
    WeatherRisk = 5,
    NightDayTransition = 6
}
