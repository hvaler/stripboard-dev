using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;
using Stripboard.Infrastructure.Persistence;

namespace Stripboard.Infrastructure.Services;

/// <summary>
/// Imports the breakdown produced by the Gemini breakdown agent (EV-18) into the schedule
/// database, which is what makes a change of screenplay visible on the stripboard (EV-21).
///
/// Input is exactly what <c>python -m agents.breakdown --file &lt;script&gt; --json</c> prints,
/// so the Python and .NET halves share one contract and neither reformats for the other.
/// </summary>
public class BreakdownImportService
{
    private readonly StripboardDbContext _db;

    public BreakdownImportService(StripboardDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    public record ImportResult(int Scenes, int CastCreated, string Source);

    private sealed record BreakdownDto(List<SceneDto>? Scenes, string? Source);

    private sealed record SceneDto(
        int Number, string? Set_Location, string? Location, string? Set_Name,
        string? Int_Ext, string? Day_Night,
        int Eighths, string? Synopsis, List<string>? Cast, List<ElementDto>? Elements);

    private sealed record ElementDto(string? Name, string? Category);

    /// <summary>
    /// Replaces the current screenplay with the imported one. Schedules derived from the
    /// previous screenplay are removed rather than left behind: a stripboard for a script
    /// that is no longer loaded would be worse than none.
    /// </summary>
    public async Task<ImportResult> ImportAsync(string json, CancellationToken ct = default)
    {
        BreakdownDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<BreakdownDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"That is not a valid breakdown document: {ex.Message}", ex);
        }

        if (dto?.Scenes is not { Count: > 0 })
        {
            throw new InvalidOperationException("The breakdown contains no scenes.");
        }

        if (string.Equals(dto.Source, "fallback", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "This breakdown was produced by the parser fallback, so it has no cast or elements. "
                + "Re-run the breakdown agent with Gemini configured before importing.");
        }

        _db.ShootDays.RemoveRange(_db.ShootDays);
        _db.Strips.RemoveRange(_db.Strips);
        _db.ScheduleVersions.RemoveRange(_db.ScheduleVersions);
        _db.Scenes.RemoveRange(_db.Scenes);
        await _db.SaveChangesAsync(ct);

        var people = await _db.People.ToListAsync(ct);
        var byName = people.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var created = 0;

        foreach (var scene in dto.Scenes)
        {
            var castIds = new List<Guid>();
            foreach (var name in scene.Cast ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!byName.TryGetValue(name, out var person))
                {
                    person = new Person(Guid.NewGuid(), name.Trim(), PersonRole.Cast, dailyRate: 1_000m);
                    _db.People.Add(person);
                    byName[person.Name] = person;
                    created++;
                }
                castIds.Add(person.Id);
            }

            _db.Scenes.Add(new Scene(
                Guid.NewGuid(),
                scene.Number,
                scene.Set_Location ?? scene.Location ?? "UNKNOWN",
                ParseIntExt(scene.Int_Ext),
                ParseDayNight(scene.Day_Night),
                Math.Max(1, scene.Eighths),
                castIds,
                null,
                scene.Synopsis ?? string.Empty,
                // The place the unit travels to, which is what company moves are counted
                // against. Absent it, Scene falls back to the full set description.
                location: scene.Location));
        }

        _db.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(),
            DateTime.UtcNow,
            eventType: "BreakdownImported",
            actor: "breakdown-agent",
            details: $"Imported {dto.Scenes.Count} scenes (source: {dto.Source ?? "unknown"}) "
                   + $"across {dto.Scenes.Select(s => s.Location ?? s.Set_Location).Distinct(StringComparer.OrdinalIgnoreCase).Count()} location(s), "
                   + $"{created} new cast member(s) created."));

        await _db.SaveChangesAsync(ct);
        return new ImportResult(dto.Scenes.Count, created, dto.Source ?? "unknown");
    }

    private static IntExt ParseIntExt(string? value) => value?.ToUpperInvariant() switch
    {
        "EXT" => IntExt.Ext,
        "INT/EXT" or "EXT/INT" => IntExt.IntExt,
        _ => IntExt.Int,
    };

    private static DayNight ParseDayNight(string? value) => value?.ToUpperInvariant() switch
    {
        "NIGHT" => DayNight.Night,
        "DAWN" => DayNight.Dawn,
        "DUSK" => DayNight.Dusk,
        _ => DayNight.Day,
    };
}
