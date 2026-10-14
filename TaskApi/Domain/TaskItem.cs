namespace TaskApi.Domain;

/// <summary>
/// Task aggregate. <see cref="CompletedAt"/> is intentionally <c>null</c> on creation and
/// is set only by <see cref="Complete"/>.
/// </summary>
public class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Non-null, non-empty, max 200 chars (validated at the API boundary + DB constraint).</summary>
    public string Title { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set exactly once, at completion time. Never set on creation.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    public Priority Priority { get; set; } = Priority.Medium;

    /// <summary>
    /// Marks the task as completed. Returns <c>false</c> when it was already completed so the
    /// API can translate that into a 409. This is only a fast pre-check — the real race is
    /// resolved by the xmin concurrency token on SaveChanges.
    /// </summary>
    public bool Complete()
    {
        if (IsCompleted)
        {
            return false;
        }

        IsCompleted = true;
        CompletedAt = DateTimeOffset.UtcNow;
        return true;
    }
}
