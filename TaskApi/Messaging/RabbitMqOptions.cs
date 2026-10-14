namespace TaskApi.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";

    /// <summary>Topic exchange declared on startup of the publisher.</summary>
    public string Exchange { get; set; } = "task.events";

    public string RoutingKey { get; set; } = "task.completed";

    /// <summary>How long shutdown waits for in-flight publishes before closing (3-5s per spec).</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(4);
}
