using FluentAssertions;
using Stripboard.Application.Common.Models;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;
using Stripboard.Solver;
using Xunit;

namespace Stripboard.Solver.Tests;

/// <summary>
/// Golden tests for the scheduling model (EV-27). Each one pins a production rule that a
/// 1st AD would treat as non-negotiable, with a fixture whose optimal answer is known by
/// hand so a regression shows up as a wrong number rather than a vague "still feasible".
///
/// Sizing note: a 12-hour day reserves a 60-minute meal break, leaving 660 minutes of
/// shooting. Scenes below are 300 minutes (20 eighths), so exactly two fit in a day.
/// </summary>
public class CpSatScheduleSolverTests
{
    private readonly CpSatScheduleSolver _solver = new();
    private static readonly DateOnly Start = new(2026, 8, 10);

    private static Scene SceneAt(int number, string location, DayNight when = DayNight.Day,
        int eighths = 20, params Guid[] cast) =>
        new(Guid.NewGuid(), number, location, IntExt.Int, when, eighths, cast, null, $"Scene {number}");

    private static SolverInput Input(List<Scene> scenes, List<Person>? people = null,
        List<LocationPermitWindow>? permits = null, int maxDays = 8) =>
        new(scenes, people ?? new List<Person>(), permits ?? new List<LocationPermitWindow>(),
            Start, MaxDaysAvailable: maxDays, MaxHoursPerDay: 12);

    /// <summary>
    /// Distinct places visited per day, summed. Counted by Location — the place the unit
    /// travels to — not by the set description, because that is what a company move is.
    /// </summary>
    private static int LocationDays(SolverOutput result) =>
        result.ScheduledDays.Sum(d => d.ScheduledScenes
            .Select(s => s.Location).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    [Fact]
    public async Task EveryScene_IsScheduledExactlyOnce()
    {
        var scenes = Enumerable.Range(1, 6).Select(i => SceneAt(i, i <= 3 ? "STUDIO A" : "STUDIO B")).ToList();

        var result = await _solver.SolveAsync(Input(scenes));

        result.IsFeasible.Should().BeTrue();
        result.ScheduledDays.SelectMany(d => d.ScheduledScenes).Select(s => s.Number)
            .Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5, 6 });
    }

    [Fact]
    public async Task DayAndNightScenes_AreNeverShotOnTheSameDay()
    {
        // A breakfast scene and an evening scene cannot share one call time. Before EV-27
        // the model happily mixed them, which is what made schedules unshootable.
        var scenes = new List<Scene>
        {
            SceneAt(1, "221B BAKER STREET", DayNight.Day, eighths: 3),
            SceneAt(2, "221B BAKER STREET", DayNight.Day, eighths: 2),
            SceneAt(3, "221B BAKER STREET", DayNight.Night, eighths: 4),
        };

        var result = await _solver.SolveAsync(Input(scenes));

        result.IsFeasible.Should().BeTrue();
        result.ScheduledDays.Should().HaveCount(2, "the night scene needs its own unit");
        foreach (var day in result.ScheduledDays)
        {
            day.ScheduledScenes.Select(s => s.DayNight == DayNight.Night).Distinct()
                .Should().ContainSingle("a day is either a day unit or a night unit, never both");
        }
    }

    [Fact]
    public async Task NightUnit_IsNeverFollowedByADayUnit()
    {
        // Wrapping at 06:00 and calling at 08:00 is a two-hour turnaround.
        var scenes = new List<Scene>
        {
            SceneAt(1, "WHARF", DayNight.Night),
            SceneAt(2, "WHARF", DayNight.Night),
            SceneAt(3, "STUDIO", DayNight.Day),
            SceneAt(4, "STUDIO", DayNight.Day),
        };

        var result = await _solver.SolveAsync(Input(scenes));

        result.IsFeasible.Should().BeTrue();
        var callTimes = result.ScheduledDays.Select(d => d.CallTime).ToList();
        for (var i = 1; i < callTimes.Count; i++)
        {
            var previousWasNight = callTimes[i - 1] == new TimeOnly(18, 0);
            var currentIsDay = callTimes[i] == new TimeOnly(8, 0);
            (previousWasNight && currentIsDay).Should().BeFalse(
                "a night unit cannot be followed immediately by a day unit");
        }
    }

    [Fact]
    public async Task TurnaroundHolds_ByConstruction_SoTheUnionValidatorFindsNothing()
    {
        // The union rules service is a cross-check on the model, not the enforcement
        // mechanism. If it ever reports a turnaround violation, the model is wrong.
        var scenes = new List<Scene>
        {
            SceneAt(1, "STUDIO", DayNight.Day),
            SceneAt(2, "STUDIO", DayNight.Day),
            SceneAt(3, "WHARF", DayNight.Night),
            SceneAt(4, "WHARF", DayNight.Night),
            SceneAt(5, "STUDIO", DayNight.Day),
        };

        var result = await _solver.SolveAsync(Input(scenes));

        result.IsFeasible.Should().BeTrue();
        result.DetectedAnomalies.Should().NotContain(a => a.Type == AnomalyType.TurnaroundViolation);
    }

    [Fact]
    public async Task ScenesAtTheSameLocation_AreGroupedToAvoidCompanyMoves()
    {
        // Two locations, two scenes each, two scenes per day. The only schedule that costs
        // two location-days keeps each location on its own day; any mixing costs four.
        var scenes = new List<Scene>
        {
            SceneAt(1, "LOCATION A"), SceneAt(2, "LOCATION A"),
            SceneAt(3, "LOCATION B"), SceneAt(4, "LOCATION B"),
        };

        var result = await _solver.SolveAsync(Input(scenes));

        result.ScheduledDays.Should().HaveCount(2);
        LocationDays(result).Should().Be(2, "each shooting day should stay at one location");
        foreach (var day in result.ScheduledDays)
        {
            day.ScheduledScenes.Select(s => s.SetLocation).Distinct().Should().ContainSingle();
        }
    }

    [Fact]
    public async Task TwoSetsAtOneLocation_DoNotCountAsACompanyMove()
    {
        // Moving from a hotel lobby to room 402 does not move the trucks. Counting it as
        // a company move both overstates the cost and wastes an hour of the day that the
        // schedule could have used (EV-28).
        var scenes = new List<Scene>
        {
            new(Guid.NewGuid(), 1, "HOTEL METROPOLE - LOBBY", IntExt.Int, DayNight.Day, 20,
                null, null, "Arrival", location: "HOTEL METROPOLE"),
            new(Guid.NewGuid(), 2, "HOTEL METROPOLE - ROOM 402", IntExt.Int, DayNight.Day, 20,
                null, null, "The letters", location: "HOTEL METROPOLE"),
        };

        var result = await _solver.SolveAsync(Input(scenes));

        result.ScheduledDays.Should().HaveCount(1);
        LocationDays(result).Should().Be(1, "both sets are at one location");
        // 10h of work + 1h meal = wrap at 19:00. Charging a company move would push it to
        // 20:00, so this time is the assertion that no move was billed.
        result.ScheduledDays[0].WrapTime.Should().Be(new TimeOnly(19, 0));
    }

    [Fact]
    public async Task TwoLocationsSharingAHeadingPrefix_AreStillTwoLocations()
    {
        // "CITY STREETS - RIVERSIDE" and "CITY STREETS - MARKET SQUARE" look similar and
        // are not: the unit crosses the city. The solver trusts Location, not the string.
        var scenes = new List<Scene>
        {
            new(Guid.NewGuid(), 1, "CITY STREETS - RIVERSIDE", IntExt.Ext, DayNight.Day, 20,
                null, null, "Followed", location: "RIVERSIDE"),
            new(Guid.NewGuid(), 2, "CITY STREETS - MARKET SQUARE", IntExt.Ext, DayNight.Day, 20,
                null, null, "The meeting", location: "MARKET SQUARE"),
        };

        var result = await _solver.SolveAsync(Input(scenes));

        LocationDays(result).Should().Be(2, "these are two places, not one");
    }

    [Fact]
    public async Task CompanyMoves_ArePreferredOverAnExtraDay()
    {
        // Priority order matters: adding a day is far more expensive than a move, so a
        // schedule that fits in one day must not be split just to avoid a move.
        var scenes = new List<Scene>
        {
            SceneAt(1, "LOCATION A", eighths: 10),
            SceneAt(2, "LOCATION B", eighths: 10),
        };

        var result = await _solver.SolveAsync(Input(scenes));

        result.ScheduledDays.Should().HaveCount(1, "fewest days outranks fewest company moves");
        LocationDays(result).Should().Be(2);
    }

    [Fact]
    public async Task CastUnavailability_BlocksTheDate_EvenWhenItCostsAnExtraDay()
    {
        // The acceptance case for EV-27. Both scenes fit comfortably in a single day, but
        // the two leads have disjoint availability windows — the situation Day Out of Days
        // exists to capture. Honouring it costs exactly one extra shooting day.
        //
        // Blocking a single date would prove nothing: the solver would simply shoot both
        // scenes a day later and still use one day.
        var horizon = Enumerable.Range(0, 8).Select(Start.AddDays).ToList();
        var holmes = new Person(Guid.NewGuid(), "Sherlock Holmes", PersonRole.Cast, 1500m,
            unavailableDates: horizon.Where(d => d != Start));
        var watson = new Person(Guid.NewGuid(), "Dr. John Watson", PersonRole.Cast, 1200m,
            unavailableDates: horizon.Where(d => d != Start.AddDays(1)));

        var scenes = new List<Scene>
        {
            SceneAt(1, "221B BAKER STREET", DayNight.Day, 20, holmes.Id),
            SceneAt(2, "221B BAKER STREET", DayNight.Day, 20, watson.Id),
        };

        var withoutDood = await _solver.SolveAsync(Input(scenes,
            new List<Person> { new(holmes.Id, holmes.Name, PersonRole.Cast, 1500m),
                               new(watson.Id, watson.Name, PersonRole.Cast, 1200m) }));
        var withDood = await _solver.SolveAsync(Input(scenes, new List<Person> { holmes, watson }));

        withoutDood.ScheduledDays.Should().HaveCount(1, "both scenes fit in one day on their own");
        withDood.ScheduledDays.Should().HaveCount(2, "honouring cast availability costs a day");

        var holmesDay = withDood.ScheduledDays.Single(d => d.ScheduledScenes.Any(s => s.Number == 1));
        var watsonDay = withDood.ScheduledDays.Single(d => d.ScheduledScenes.Any(s => s.Number == 2));
        holmesDay.Date.Should().Be(Start, "the only date Holmes can work");
        watsonDay.Date.Should().Be(Start.AddDays(1), "the only date Watson can work");
    }

    [Fact]
    public async Task PermitWindows_AreStillRespected()
    {
        var scenes = new List<Scene>
        {
            SceneAt(1, "STUDIO A", DayNight.Day, eighths: 8),
            SceneAt(2, "WAREHOUSE B", DayNight.Night, eighths: 8),
        };
        var permits = new List<LocationPermitWindow>
        {
            new("STUDIO A", Start, Start.AddDays(1)),
            new("WAREHOUSE B", Start.AddDays(3), Start.AddDays(5)),
        };

        var result = await _solver.SolveAsync(Input(scenes, permits: permits));

        result.IsFeasible.Should().BeTrue();
        var warehouseDay = result.ScheduledDays.Single(d => d.ScheduledScenes.Any(s => s.Number == 2));
        warehouseDay.Date.Should().BeOnOrAfter(Start.AddDays(3));
    }

    [Fact]
    public async Task ProductionDayNumbers_AreSequentialEvenWhenCalendarDatesSkip()
    {
        var blocked = new Person(Guid.NewGuid(), "Lead", PersonRole.Cast, 1000m,
            unavailableDates: new[] { Start.AddDays(1), Start.AddDays(2) });
        var scenes = new List<Scene>
        {
            SceneAt(1, "STUDIO", DayNight.Day, 20, blocked.Id),
            SceneAt(2, "STUDIO", DayNight.Day, 20, blocked.Id),
            SceneAt(3, "STUDIO", DayNight.Day, 20, blocked.Id),
        };

        var result = await _solver.SolveAsync(Input(scenes, new List<Person> { blocked }));

        result.IsFeasible.Should().BeTrue();
        result.ScheduledDays.Select(d => d.DayNumber).Should()
            .Equal(Enumerable.Range(1, result.ScheduledDays.Count),
                "shooting day numbers are sequential; the calendar date is what skips");
    }

    [Fact]
    public async Task AnImpossibleScheduleExplainsWhy_RatherThanSayingInfeasible()
    {
        // More screenplay than the available days can physically hold.
        var scenes = Enumerable.Range(1, 4).Select(i => SceneAt(i, "STUDIO", DayNight.Day, eighths: 40)).ToList();

        var result = await _solver.SolveAsync(Input(scenes, maxDays: 1));

        result.IsFeasible.Should().BeFalse();
        result.DetectedAnomalies.Should().ContainSingle()
            .Which.Message.Should().Contain("h of shooting")
            .And.Contain("available", "the message must say what ran out, not just that it failed");
    }

    [Fact]
    public async Task ALargerScreenplay_SchedulesFeasiblyAcrossLocationsAndUnits()
    {
        var scenes = new List<Scene>();
        for (int i = 1; i <= 8; i++)
        {
            scenes.Add(SceneAt(i, "STUDIO A", DayNight.Day, eighths: 3));
        }
        for (int i = 9; i <= 15; i++)
        {
            scenes.Add(SceneAt(i, "WAREHOUSE B", DayNight.Night, eighths: 4));
        }

        var permits = new List<LocationPermitWindow>
        {
            new("STUDIO A", Start, Start.AddDays(2)),
            new("WAREHOUSE B", Start.AddDays(2), Start.AddDays(5)),
        };

        var result = await _solver.SolveAsync(Input(scenes, permits: permits, maxDays: 6));

        result.IsFeasible.Should().BeTrue();
        result.ScheduledDays.Sum(d => d.ScheduledScenes.Count).Should().Be(15);
        LocationDays(result).Should().Be(result.ScheduledDays.Count,
            "with two well-separated locations no day should need to visit both");
    }

    [Fact]
    public async Task NoScenes_IsFeasibleAndEmpty()
    {
        var result = await _solver.SolveAsync(Input(new List<Scene>()));

        result.IsFeasible.Should().BeTrue();
        result.ScheduledDays.Should().BeEmpty();
    }
}
