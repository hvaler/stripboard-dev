using Stripboard.Domain.Enums;

namespace Stripboard.Domain.Entities;

/// <summary>
/// Represents a production element required for a scene (§5 entity).
/// </summary>
public class Element
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public ElementCategory Category { get; private set; }
    public string? Notes { get; private set; }

    private Element() { }

    public Element(Guid id, string name, ElementCategory category, string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Element name cannot be empty.", nameof(name));

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Name = name;
        Category = category;
        Notes = notes;
    }
}
