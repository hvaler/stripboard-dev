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

    private Person() { }

    public Person(
        Guid id,
        string name,
        PersonRole role,
        decimal dailyRate = 0m,
        int maxHoursPerDay = 12)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Person name cannot be empty.", nameof(name));

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Name = name;
        Role = role;
        DailyRate = dailyRate;
        MaxHoursPerDay = maxHoursPerDay;
    }
}
