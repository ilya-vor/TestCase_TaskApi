using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TaskApi.Contracts;

namespace Tests;

/// <summary>
/// End-to-end integration tests required by the spec (plus a couple of high-value extras):
///   1. create a task through the API, complete it, assert RabbitMQ received task.completed;
///   2. empty/whitespace title -> 400;
///   3. two concurrent completions -> exactly one 200 and one 409 (xmin concurrency token).
/// </summary>
public class CompleteTaskIntegrationTest : IClassFixture<TaskApiFixture>
{
    private readonly TaskApiFixture _fixture;

    public CompleteTaskIntegrationTest(TaskApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Create_then_complete_task_publishes_task_completed_event()
    {
        using var client = _fixture.CreateClient();

        // ---------- Arrange: subscribe to task.events BEFORE completing ----------
        var connectionFactory = new ConnectionFactory { Uri = new Uri(_fixture.RabbitConnectionString) };
        await using var connection = await connectionFactory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await channel.ExchangeDeclareAsync("task.events", ExchangeType.Topic, durable: true);
        var queue = await channel.QueueDeclareAsync();
        await channel.QueueBindAsync(queue.QueueName, "task.events", "task.completed");

        var received = new TaskCompletionSource<TaskCompletedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, args) =>
        {
            var json = Encoding.UTF8.GetString(args.Body.Span);
            var evt = JsonSerializer.Deserialize<TaskCompletedEvent>(
                json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (evt is not null)
            {
                received.TrySetResult(evt);
            }

            return Task.CompletedTask;
        };
        await channel.BasicConsumeAsync(queue.QueueName, autoAck: true, consumer: consumer);

        // ---------- Act 1: create the task ----------
        var createResponse = await client.PostAsJsonAsync(
            "/tasks", new { title = "Buy milk", priority = "High" });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(created);
        Assert.False(created!.IsCompleted);
        Assert.Null(created.CompletedAt); // CompletedAt must NOT be set on creation

        // ---------- Act 2: complete the task ----------
        var completeResponse = await client.PutAsync($"/tasks/{created.Id}/complete", content: null);

        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        var completed = await completeResponse.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(completed);
        Assert.True(completed!.IsCompleted);
        Assert.NotNull(completed.CompletedAt); // CompletedAt set exactly at completion

        // ---------- Assert: RabbitMQ received the message ----------
        var finished = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(finished == received.Task,
            "RabbitMQ did not receive a task.completed message within 15 seconds.");

        var evt = await received.Task;
        Assert.Equal(created.Id, evt.TaskId);
        Assert.Equal("Buy milk", evt.Title);
        Assert.Equal("High", evt.Priority);
        Assert.Equal(
            completed.CompletedAt!.Value.ToUnixTimeSeconds(),
            evt.CompletedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Complete_task_succeeds_even_when_rabbitmq_is_unreachable()
    {
        // A broker that refuses connections: the task must still be completed (DB is the
        // source of truth) and the publish failure is only logged.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = _fixture.PostgresConnectionString,
                    ["RabbitMq:Host"] = "127.0.0.1",
                    ["RabbitMq:Port"] = "1", // nothing listens here
                    ["RabbitMq:ShutdownTimeout"] = "00:00:01"
                });
            });
        });

        using var client = factory.CreateClient();

        var createResponse = await client.PostAsJsonAsync("/tasks", new { title = "No broker" });
        var task = await createResponse.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(task);

        var completeResponse = await client.PutAsync($"/tasks/{task!.Id}/complete", content: null);

        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        var completed = await completeResponse.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(completed);
        Assert.True(completed!.IsCompleted);
        Assert.NotNull(completed.CompletedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_task_with_blank_title_returns_400(string title)
    {
        using var client = _fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/tasks", new { title });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_task_with_title_longer_than_200_returns_400()
    {
        using var client = _fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/tasks", new { title = new string('x', 201) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_completion_returns_one_success_and_one_conflict()
    {
        using var client = _fixture.CreateClient();

        var createResponse = await client.PostAsJsonAsync("/tasks", new { title = "Race me" });
        var created = await createResponse.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(created);

        // Two simultaneous completions of the same task.
        var t1 = client.PutAsync($"/tasks/{created!.Id}/complete", content: null);
        var t2 = client.PutAsync($"/tasks/{created.Id}/complete", content: null);
        var responses = await Task.WhenAll(t1, t2);

        var statuses = responses.Select(r => r.StatusCode).OrderBy(s => (int)s).ToArray();
        Assert.Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }.OrderBy(s => (int)s),
            statuses);
    }
}
