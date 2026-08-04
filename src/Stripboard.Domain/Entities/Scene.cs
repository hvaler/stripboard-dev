using Stripboard.Domain.Enums;

namespace Stripboard.Domain.Entities;

/// <summary>
/// Represents a broken-down scene in the screenplay (§5 entity).
/// </summary>
public class Scene
{
    public Guid Id { get; private set; }
    public int Number { get; private set; }

    /// <summary>
    /// The full set description as it reads on the stripboard, e.g.
    /// "HOTEL METROPOLE - ROOM 402".
    /// </summary>
    public string SetLocation { get; private set; } = string.Empty;

    /// <summary>
    /// The place the unit physically travels to. Distinct from <see cref="SetLocation"/>:
    /// the lobby and room 402 of one hotel are two sets at a single location, so moving
    /// between them is not a company move, while two streets across a city are two
    /// locations even when the headings share a prefix. Only a reader who understands the
    /// words can tell those apart, so the breakdown agent decides it (EV-28).
    /// </summary>
    public string Location { get; private set; } = string.Empty;
    public IntExt IntExt { get; private set; }
    public DayNight DayNight { get; private set; }
    public int Eighths { get; private set; }
    public List<Guid> CastPersonIds { get; private set; } = new();
    public List<Guid> ElementIds { get; private set; } = new();
    public string Synopsis { get; private set; } = string.Empty;

    private Scene() { }

    public Scene(
        Guid id,
        int number,
        string setLocation,
        IntExt intExt,
        DayNight dayNight,
        int eighths,
        IEnumerable<Guid>? castPersonIds = null,
        IEnumerable<Guid>? elementIds = null,
        string synopsis = "",
        string? location = null)
    {
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Scene number must be positive.");
        if (eighths <= 0)
            throw new ArgumentOutOfRangeException(nameof(eighths), "Scene eighths must be positive.");

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Number = number;
        SetLocation = setLocation ?? string.Empty;
        // Falling back to the full set description over-counts company moves rather than
        // under-counting them, which is the safe direction for a schedule's cost.
        Location = string.IsNullOrWhiteSpace(location) ? SetLocation : location.Trim();
        IntExt = intExt;
        DayNight = dayNight;
        Eighths = eighths;
        CastPersonIds = castPersonIds?.ToList() ?? new List<Guid>();
        ElementIds = elementIds?.ToList() ?? new List<Guid>();
        Synopsis = synopsis ?? string.Empty;
    }
}
