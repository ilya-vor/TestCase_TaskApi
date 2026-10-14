using TaskApi.Domain;

namespace TaskApi.Messaging;

/// <summary>
/// Abstraction over the event bus so the API can be tested with a fake and so the
/// transport can be swapped without touching endpoints.
/// </summary>
public interface ITaskEventPublisher
{
    /// <summary>
    /// Publishes a <c>task.completed</c> event. Never throws: if the broker is unavailable
    /// the failure is logged and the caller continues (the DB is the source of truth).
    /// </summary>
    Task PublishTaskCompletedAsync(TaskItem task, CancellationToken cancellationToken = default);
}
