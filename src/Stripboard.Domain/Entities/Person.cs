using Stripboard.Domain.Enums;

namespace Stripboard.Domain.Entities;

/// <summary>
/// Represents a person (cast or crew) in the film production (§5 entity).
/// </summary>
public class Person
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public PersonRole Role { get; private set; }
    public decimal DailyRate { get; private set; }
    public int MaxHoursPerDay { get; private set; }
    public bool IsCast => Role == PersonRole.Cast;

    /// <summary>
    /// Dates this person cannot work — the "Day Out of Days" availability a 1st AD treats
    /// as non-negotiable. The solver refuses to place any scene featuring this person on
    /// one of these dates (EV-27).
    /// </summary>
    public List<DateOnly> UnavailableDates { get; private set; } = new();

    private Person() { }

    public Person(
        Guid id,
        string name,
        PersonRole role,
        decimal dailyRate = 0m,
        int maxHoursPerDay = 12,
        IEnumerable<DateOnly>? unavailableDates = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Person name cannot be empty.", nameof(name));

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Name = name;
        Role = role;
        DailyRate = dailyRate;
        MaxHoursPerDay = maxHoursPerDay;
        UnavailableDates = unavailableDates?.Distinct().OrderBy(d => d).ToList() ?? new List<DateOnly>();
    }

    public bool IsAvailableOn(DateOnly date) => !UnavailableDates.Contains(date);

    public void SetUnavailability(IEnumerable<DateOnly> dates)
    {
        UnavailableDates = (dates ?? Enumerable.Empty<DateOnly>()).Distinct().OrderBy(d => d).ToList();
    }
}
