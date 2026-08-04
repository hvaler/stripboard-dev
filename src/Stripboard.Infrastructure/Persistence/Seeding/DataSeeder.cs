using Microsoft.EntityFrameworkCore;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;

namespace Stripboard.Infrastructure.Persistence.Seeding;

/// <summary>
/// Reproducible and idempotent data seeder for demo screenplay dataset (§5).
/// Creates 12 scenes, 4 cast + 2 crew members, 2 locations, strips, and initial committed schedule.
/// </summary>
public static class DataSeeder
{
    public static async Task SeedAsync(StripboardDbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Idempotency check: exit if database already seeded
        if (await context.Scenes.AnyAsync(cancellationToken))
        {
            return;
        }

        // 1. Create People (4 Cast + 2 Crew)
        var castHolmes = new Person(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Sherlock Holmes", PersonRole.Cast, dailyRate: 1500m, maxHoursPerDay: 12);
        var castWatson = new Person(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Dr. John Watson", PersonRole.Cast, dailyRate: 1200m, maxHoursPerDay: 12);
        var castMoriarty = new Person(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Prof. James Moriarty", PersonRole.Cast, dailyRate: 1800m, maxHoursPerDay: 10);
        var castIrene = new Person(Guid.Parse("44444444-4444-4444-4444-444444444444"), "Irene Adler", PersonRole.Cast, dailyRate: 1400m, maxHoursPerDay: 12);
        var crewAd = new Person(Guid.Parse("55555555-5555-5555-5555-555555555555"), "Arthur Conan (1st AD)", PersonRole.FirstAssistantDirector, dailyRate: 900m, maxHoursPerDay: 14);
        var crewDop = new Person(Guid.Parse("66666666-6666-6666-6666-666666666666"), "Sydney Paget (DoP)", PersonRole.DepartmentHead, dailyRate: 1000m, maxHoursPerDay: 14);

        context.People.AddRange(castHolmes, castWatson, castMoriarty, castIrene, crewAd, crewDop);

        // 2. Create Production Elements
        var propLetter = new Element(Guid.NewGuid(), "Ciphered Document", ElementCategory.Prop, "Encrypted note from Moriarty");
        var propRevolver = new Element(Guid.NewGuid(), "Webley Revolver", ElementCategory.Prop, "Watson's service revolver");
        var fxSmoke = new Element(Guid.NewGuid(), "Fog Machine Smoke", ElementCategory.Fx, "Heavy London fog effect");
        var vehicleCarriage = new Element(Guid.NewGuid(), "Hansom Cab Carriage", ElementCategory.Vehicle, "Victorian horse-drawn carriage");

        context.Elements.AddRange(propLetter, propRevolver, fxSmoke, vehicleCarriage);

        // 3. Create 12 Scenes
        var scenes = new List<Scene>
        {
            new(Guid.NewGuid(), 1, "221B BAKER STREET - SITTING ROOM", IntExt.Int, DayNight.Day, 4,
                new[] { castHolmes.Id, castWatson.Id }, new[] { propLetter.Id }, "Holmes examines the cipher note over morning tea.", location: "221B BAKER STREET"),

            new(Guid.NewGuid(), 2, "221B BAKER STREET - SITTING ROOM", IntExt.Int, DayNight.Day, 3,
                new[] { castHolmes.Id, castWatson.Id, castIrene.Id }, new[] { propLetter.Id }, "Irene Adler arrives unexpectedly with new intelligence.", location: "221B BAKER STREET"),

            new(Guid.NewGuid(), 3, "LONDON STREETS - COVENT GARDEN", IntExt.Ext, DayNight.Day, 5,
                new[] { castWatson.Id, castIrene.Id }, new[] { vehicleCarriage.Id }, "Watson and Irene pursue a suspicious courier through the market.", location: "COVENT GARDEN"),

            new(Guid.NewGuid(), 4, "TOWER BRIDGE WHARF", IntExt.Ext, DayNight.Night, 6,
                new[] { castHolmes.Id, castMoriarty.Id }, new[] { fxSmoke.Id }, "Holmes meets Moriarty under the foggy wharf lanterns.", location: "TOWER BRIDGE WHARF"),

            new(Guid.NewGuid(), 5, "TOWER BRIDGE WHARF - WAREHOUSE", IntExt.Int, DayNight.Night, 8,
                new[] { castHolmes.Id, castWatson.Id, castMoriarty.Id }, new[] { propRevolver.Id, fxSmoke.Id }, "Ambush inside the deserted riverfront warehouse.", location: "TOWER BRIDGE WHARF"),

            new(Guid.NewGuid(), 6, "SCOTLAND YARD - INSPECTOR OFFICE", IntExt.Int, DayNight.Day, 4,
                new[] { castHolmes.Id, castWatson.Id }, new[] { propLetter.Id }, "De-briefing with Lestrade and analyzing captured evidence.", location: "SCOTLAND YARD"),

            new(Guid.NewGuid(), 7, "221B BAKER STREET - LABORATORY", IntExt.Int, DayNight.Night, 5,
                new[] { castHolmes.Id }, new[] { propLetter.Id }, "Holmes works late into the night deciphering the chemical residue.", location: "221B BAKER STREET"),

            new(Guid.NewGuid(), 8, "DIOGENES CLUB - READING ROOM", IntExt.Int, DayNight.Day, 3,
                new[] { castHolmes.Id, castWatson.Id }, null, "Silent consultation regarding Moriarty's network connections.", location: "DIOGENES CLUB"),

            new(Guid.NewGuid(), 9, "VICTORIA STATION - PLATFORM 4", IntExt.Ext, DayNight.Day, 6,
                new[] { castIrene.Id, castWatson.Id }, new[] { vehicleCarriage.Id }, "Irene prepares to board the Continental express train.", location: "VICTORIA STATION"),

            new(Guid.NewGuid(), 10, "THAMES RIVERBANK", IntExt.Ext, DayNight.Dusk, 4,
                new[] { castHolmes.Id, castMoriarty.Id }, new[] { propRevolver.Id }, "Standoff at dusk along the muddy bank.", location: "THAMES RIVERBANK"),

            new(Guid.NewGuid(), 11, "221B BAKER STREET - SITTING ROOM", IntExt.Int, DayNight.Night, 4,
                new[] { castHolmes.Id, castWatson.Id, castIrene.Id }, new[] { propLetter.Id }, "Final reveal of the mastermind's plan.", location: "221B BAKER STREET"),

            new(Guid.NewGuid(), 12, "LONDON STREETS - PICCADILLY", IntExt.Ext, DayNight.Day, 2,
                new[] { castHolmes.Id, castWatson.Id }, null, "Holmes and Watson walk into the crowd as peace is restored.", location: "PICCADILLY")
        };

        context.Scenes.AddRange(scenes);

        // 4. Create Strips for the 12 Scenes
        var strips = scenes.Select((s, index) => new Strip(Guid.NewGuid(), s.Id, order: index + 1, estimatedDurationMinutes: s.Eighths * 15)).ToList();
        context.Strips.AddRange(strips);

        // 5. Create Shoot Days (3 Days)
        var startDate = new DateOnly(2026, 8, 10);
        var day1 = new ShootDay(Guid.NewGuid(), startDate, 1, "221B BAKER STREET", new TimeOnly(8, 0), new TimeOnly(18, 0), strips.Take(4).Select(st => st.Id));
        var day2 = new ShootDay(Guid.NewGuid(), startDate.AddDays(1), 2, "TOWER BRIDGE WHARF", new TimeOnly(18, 0), new TimeOnly(4, 0), strips.Skip(4).Take(4).Select(st => st.Id)); // Overnight shoot
        var day3 = new ShootDay(Guid.NewGuid(), startDate.AddDays(2), 3, "LONDON LOCATIONS", new TimeOnly(8, 0), new TimeOnly(17, 0), strips.Skip(8).Take(4).Select(st => st.Id));

        context.ShootDays.AddRange(day1, day2, day3);

        // 6. Create Initial Schedule Version (V1 - Committed)
        var initialVersion = new ScheduleVersion(Guid.NewGuid(), versionNumber: 1, createdBy: "Producer (Hugo)", isCommitted: true);
        context.ScheduleVersions.Add(initialVersion);

        // 7. Create Initial Audit Event
        var seedAuditEvent = new AuditEvent(
            Guid.NewGuid(),
            DateTime.UtcNow,
            eventType: "DatabaseSeeded",
            actor: "DataSeeder",
            details: "Initial seed completed: 12 scenes, 6 persons, 3 shoot days, schedule v1 committed.",
            relatedEntityId: initialVersion.Id
        );
        context.AuditEvents.Add(seedAuditEvent);

        await context.SaveChangesAsync(cancellationToken);
    }
}
