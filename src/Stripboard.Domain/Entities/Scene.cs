using Stripboard.Domain.Enums;

namespace Stripboard.Domain.Entities;

/// <summary>
/// Represents a broken-down scene in the screenplay (§5 entity).
/// </summary>
public class Scene
{
    public Guid Id { get; private set; }
    public int Number { get; private set; }
    public string SetLocation { get; private set; } = string.Empty;
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
        string synopsis = "")
    {
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Scene number must be positive.");
        if (eighths <= 0)
            throw new ArgumentOutOfRangeException(nameof(eighths), "Scene eighths must be positive.");

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Number = number;
        SetLocation = setLocation ?? string.Empty;
        IntExt = intExt;
        DayNight = dayNight;
        Eighths = eighths;
        CastPersonIds = castPersonIds?.ToList() ?? new List<Guid>();
        ElementIds = elementIds?.ToList() ?? new List<Guid>();
        Synopsis = synopsis ?? string.Empty;
    }
}
