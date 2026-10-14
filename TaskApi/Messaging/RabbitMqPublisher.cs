using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TaskApi.Contracts;
using TaskApi.Domain;

namespace TaskApi.Messaging;

/// <summary>
/// Singleton RabbitMQ publisher.
///
/// Design decisions / trade-offs:
/// - Lazy connection + channel, created on first publish, reused afterwards and re-created
///   automatically if the broker dropped them. Publishing never blocks the request thread on
///   confirms (fire-and-forget, per spec).
/// - <see cref="PublishTaskCompletedAsync"/> swallows broker errors (logs them) so a completed
///   task is never rolled back just because RabbitMQ is down.
/// - Implements <see cref="IAsyncDisposable"/> so graceful shutdown (SIGTERM / Ctrl+C) waits
///   up to <see cref="RabbitMqOptions.ShutdownTimeout"/> for in-flight publishes, then closes
///   the channel and the connection cleanly.
/// </summary>
public sealed class RabbitMqPublisher : ITaskEventPublisher, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqPublisher> _logger;

    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    private int _inFlight;
    private volatile bool _disposed;

    public RabbitMqPublisher(IOptions<RabbitMqOptions> options, ILogger<RabbitMqPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task PublishTaskCompletedAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            _logger.LogWarning("Publisher is disposed; dropping task.completed for {TaskId}", task.Id);
            return;
        }

        var payload = new TaskCompletedEvent(
            task.Id,
            task.Title,
            task.CompletedAt ?? DateTimeOffset.UtcNow,
            task.Priority.ToString());

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

        // Track in-flight publishes so shutdown can drain them.
        Interlocked.Increment(ref _inFlight);
        try
        {
            var channel = await GetOrCreateChannelAsync(cancellationToken);

            var properties = new BasicProperties
            {
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = task.Id.ToString()
            };

            // Fire-and-forget: we deliberately do NOT wait for publisher confirms here.
            await channel.BasicPublishAsync(
                exchange: _options.Exchange,
                routingKey: _options.RoutingKey,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Published {RoutingKey} for task {TaskId} to exchange {Exchange}",
                _options.RoutingKey, task.Id, _options.Exchange);
        }
        catch (Exception ex)
        {
            // Fail silently (with a log): the task stays completed, DB is the source of truth.
            _logger.LogError(ex,
                "Failed to publish task.completed for {TaskId}. Task remains completed; event is lost.",
                task.Id);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task<IChannel> GetOrCreateChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            if (_connection is not { IsOpen: true })
            {
                var factory = new ConnectionFactory
                {
                    HostName = _options.Host,
                    Port = _options.Port,
                    UserName = _options.Username,
                    Password = _options.Password,
                    AutomaticRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
                    ClientProvidedName = "TaskApi"
                };

                _connection = await factory.CreateConnectionAsync(cancellationToken);
                _logger.LogInformation("Connected to RabbitMQ at {Host}:{Port}", _options.Host, _options.Port);
            }

            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

            // Declaring the exchange is idempotent; keeps publish working even on a fresh broker.
            await _channel.ExchangeDeclareAsync(
                exchange: _options.Exchange,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                cancellationToken: cancellationToken);

            return _channel;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 1) Give in-flight publishes a chance to finish (bounded by ShutdownTimeout).
        var deadline = DateTime.UtcNow + _options.ShutdownTimeout;
        while (Volatile.Read(ref _inFlight) > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        if (Volatile.Read(ref _inFlight) > 0)
        {
            _logger.LogWarning("{Count} publish(es) did not finish before shutdown timeout", _inFlight);
        }

        // 2) Close channel, then connection — in that order.
        await CloseChannelAsync();
        await CloseConnectionAsync();

        _connectionLock.Dispose();
    }

    private async Task CloseChannelAsync()
    {
        if (_channel is null)
        {
            return;
        }

        try
        {
            if (_channel.IsOpen)
            {
                await _channel.CloseAsync();
            }

            await _channel.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing RabbitMQ channel during shutdown");
        }
        finally
        {
            _channel = null;
        }
    }

    private async Task CloseConnectionAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            if (_connection.IsOpen)
            {
                await _connection.CloseAsync();
            }

            await _connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing RabbitMQ connection during shutdown");
        }
        finally
        {
            _connection = null;
        }
    }
}
