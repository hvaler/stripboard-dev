namespace Stripboard.Domain.Services;

/// <summary>
/// The collective agreement a schedule is being held to (EV-42).
///
/// The numbers in this project were always IATSE / SAG-AFTRA — twelve hours of turnaround, a
/// meal break inside six hours of continuous work, fourteen hours coming off nights. That was
/// stated in a code comment and nowhere a reader would find it, while the demo screenplay is
/// set in Salford. A British 1st AD reading "12-hour turnaround" over a Manchester location
/// notices, because on this side of the Atlantic the figure is eleven.
///
/// So the thresholds become a named profile rather than constants. Two consequences follow,
/// and the second is the interesting one:
///
/// **The rules are what they are because somebody agreed them**, not because they are physics.
/// A tool that hardcodes one union's numbers is a tool for one union's territory.
///
/// **A day can be as long as the rest period allows, and no longer.** The longest lawful
/// call-to-wrap is twenty-four hours minus the turnaround owed before the next call — so a
/// twelve-hour rest caps the day at twelve hours, and an eleven-hour rest permits thirteen.
/// That is why changing the agreement changes the schedule and not merely the warnings: a
/// longer day fits more of the screenplay, and the solver needs fewer of them.
/// </summary>
public sealed record UnionAgreement(
    string Name,
    string Source,
    TimeSpan MinimumTurnaround,
    TimeSpan MaximumContinuousWorkBeforeMeal,
    TimeSpan MinimumNightToDayTurnaround)
{
    /// <summary>
    /// The longest lawful shooting day under this agreement, call to wrap.
    ///
    /// Derived rather than configured, because it is not an independent choice: a day that ran
    /// longer than this would leave less than the required rest before the next call, and the
    /// schedule would be illegal by arithmetic. Deriving it is what keeps the solver and the
    /// rule from being able to disagree.
    /// </summary>
    public int MaxHoursPerDay => (int)(TimeSpan.FromHours(24) - MinimumTurnaround).TotalHours;

    /// <summary>
    /// IATSE / SAG-AFTRA, the agreements the North American industry works to and the ones
    /// this project modelled from the start.
    /// </summary>
    public static readonly UnionAgreement IatseSagAftra = new(
        Name: "IATSE / SAG-AFTRA",
        Source: "IATSE Basic Agreement and SAG-AFTRA Theatrical Agreement (12-hour rest period)",
        MinimumTurnaround: TimeSpan.FromHours(12),
        MaximumContinuousWorkBeforeMeal: TimeSpan.FromHours(6),
        MinimumNightToDayTurnaround: TimeSpan.FromHours(14));

    /// <summary>
    /// A European profile, modelled on the eleven consecutive hours of daily rest the Working
    /// Time Directive requires and UK feature agreements are built around.
    ///
    /// Deliberately **not** labelled as a reproduction of any particular agreement. It is here
    /// to prove the thresholds are configuration rather than physics, and naming a specific
    /// union's contract for numbers nobody has checked against it would be the kind of small
    /// false precision this codebase spends its time removing. A production adopting this would
    /// set its own figures from its own agreement.
    /// </summary>
    public static readonly UnionAgreement EuropeanDailyRest = new(
        Name: "European daily rest (11 hours)",
        Source: "EU Working Time Directive 2003/88/EC, article 3 — 11 consecutive hours per 24",
        MinimumTurnaround: TimeSpan.FromHours(11),
        MaximumContinuousWorkBeforeMeal: TimeSpan.FromHours(6),
        MinimumNightToDayTurnaround: TimeSpan.FromHours(14));

    /// <summary>
    /// Resolves a profile by name, for configuration. An unknown name throws rather than
    /// falling back: quietly scheduling a European shoot to American rest periods is exactly
    /// the sort of wrong-but-plausible answer that never gets noticed.
    /// </summary>
    public static UnionAgreement FromName(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "iatse" or "sag-aftra" or "iatse/sag-aftra" => IatseSagAftra,
        "european" or "eu" or "wtd" => EuropeanDailyRest,
        _ => throw new ArgumentOutOfRangeException(nameof(name),
            $"Unknown union agreement '{name}'. Known profiles: iatse, european."),
    };
}
