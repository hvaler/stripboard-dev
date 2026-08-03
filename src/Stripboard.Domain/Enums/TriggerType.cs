namespace Stripboard.Domain.Enums;

/// <summary>
/// Trigger mechanism or source of a schedule disruption.
/// </summary>
public enum TriggerType
{
    CastUnavailability = 1,
    PermitExpiration = 2,
    WeatherAlert = 3,
    Manual = 4
}
