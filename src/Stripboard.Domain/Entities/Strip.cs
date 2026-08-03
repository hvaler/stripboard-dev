namespace Stripboard.Domain.Entities;

/// <summary>
/// Represents a visual strip representing a scene in the stripboard (§5 entity).
/// </summary>
public class Strip
{
    public Guid Id { get; private set; }
    public Guid SceneId { get; private set; }
    public int Order { get; private set; }
    public int EstimatedDurationMinutes { get; private set; }

    private Strip() { }

    public Strip(Guid id, Guid sceneId, int order, int estimatedDurationMinutes = 30)
    {
        if (sceneId == Guid.Empty)
            throw new ArgumentException("Scene ID must be specified.", nameof(sceneId));
        if (estimatedDurationMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(estimatedDurationMinutes), "Duration must be positive.");

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        SceneId = sceneId;
        Order = order;
        EstimatedDurationMinutes = estimatedDurationMinutes;
    }
}
