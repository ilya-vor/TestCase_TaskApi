using System.ComponentModel.DataAnnotations;
using TaskApi.Domain;

namespace TaskApi.Contracts;

/// <summary>Body for POST /tasks.</summary>
public record CreateTaskRequest(string? Title, Priority? Priority);

/// <summary>Shape returned by the API. Keeps the EF entity out of the public contract.</summary>
public record TaskResponse(
    Guid Id,
    string Title,
    bool IsCompleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string Priority);

/// <summary>
/// Message published to RabbitMQ on task completion.
/// Property names serialize to camelCase: { taskId, title, completedAt, priority }.
/// </summary>
public record TaskCompletedEvent(
    Guid TaskId,
    string Title,
    DateTimeOffset CompletedAt,
    string Priority);
