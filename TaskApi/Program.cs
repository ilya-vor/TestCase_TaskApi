using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskApi.Contracts;
using TaskApi.Data;
using TaskApi.Domain;
using TaskApi.Messaging;

var builder = WebApplication.CreateBuilder(args);

#region Configuration & DI

builder.Services.Configure<RabbitMqOptions>(
    builder.Configuration.GetSection(RabbitMqOptions.SectionName));

builder.Services.AddDbContext<TaskDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// Singleton: holds a persistent AMQP connection/channel and is disposed on graceful shutdown.
builder.Services.AddSingleton<ITaskEventPublisher, RabbitMqPublisher>();

// Enums in request/response JSON are strings ("High"), not numbers.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// OpenAPI/Swagger — optional per spec, but handy to explore the API manually.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

#endregion

var app = builder.Build();

#region Swagger UI

// Enabled in every environment on purpose (this is a test task): http://localhost:5154/swagger
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "TaskApi v1");
    options.RoutePrefix = "swagger";
});

#endregion

#region Database bootstrap

// HACK: using EnsureCreated instead of EF migrations to save time.
// Would do: `dotnet ef migrations add InitialCreate` + `db.Database.MigrateAsync()`
// (a migration would be checked into the repo) — see README.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TaskDbContext>();
    await db.Database.EnsureCreatedAsync();
}

#endregion

#region Endpoints

// POST /tasks — create. Defaults: IsCompleted=false, Priority=Medium, CompletedAt=null.
app.MapPost("/tasks", async (CreateTaskRequest request, TaskDbContext db, CancellationToken ct) =>
{
    var title = request.Title?.Trim();

    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(title))
    {
        errors["title"] = ["Title must not be empty."];
    }
    else if (title.Length > 200)
    {
        errors["title"] = ["Title must be 200 characters or fewer."];
    }

    if (request.Priority is { } priority && !Enum.IsDefined(priority))
    {
        errors["priority"] = ["Priority must be one of: Low, Medium, High."];
    }

    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var task = new TaskItem
    {
        Title = title!,
        IsCompleted = false,
        Priority = request.Priority ?? Priority.Medium,
        CreatedAt = DateTimeOffset.UtcNow,
        CompletedAt = null // explicitly not set on creation
    };

    db.Tasks.Add(task);
    await db.SaveChangesAsync(ct);

    return Results.Created($"/tasks/{task.Id}", ToResponse(task));
})
.WithName("CreateTask")
.WithSummary("Create a task. Defaults: IsCompleted=false, Priority=Medium, CompletedAt=null.")
.WithTags("Tasks");

// GET /tasks — return everything (no pagination by design).
app.MapGet("/tasks", async (TaskDbContext db, CancellationToken ct) =>
{
    var tasks = await db.Tasks
        .AsNoTracking()
        .OrderByDescending(t => t.CreatedAt)
        .ToListAsync(ct);

    return Results.Ok(tasks.Select(ToResponse));
})
.WithName("GetTasks")
.WithSummary("Return all tasks (no pagination by design).")
.WithTags("Tasks");

// PUT /tasks/{id}/complete — complete, persist, THEN publish. Second concurrent call -> 409.
app.MapPut("/tasks/{id:guid}/complete", async (
    Guid id,
    TaskDbContext db,
    ITaskEventPublisher publisher,
    CancellationToken ct) =>
{
    var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct);
    if (task is null)
    {
        return Results.NotFound();
    }

    if (!task.Complete())
    {
        return Results.Problem(
            title: "Task is already completed.",
            statusCode: StatusCodes.Status409Conflict);
    }

    try
    {
        // Optimistic concurrency (xmin) is enforced here.
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        return Results.Problem(
            title: "Task was modified concurrently. Please retry.",
            statusCode: StatusCodes.Status409Conflict);
    }

    // Publish ONLY after a successful commit, so we never emit an event for a write that failed.
    await publisher.PublishTaskCompletedAsync(task, ct);

    return Results.Ok(ToResponse(task));
})
.WithName("CompleteTask")
.WithSummary("Complete a task, persist it, then publish task.completed to RabbitMQ. 409 if already completed.")
.WithTags("Tasks");

// DELETE /tasks/{id} — hard delete.
app.MapDelete("/tasks/{id:guid}", async (Guid id, TaskDbContext db, CancellationToken ct) =>
{
    var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct);
    if (task is null)
    {
        return Results.NotFound();
    }

    db.Tasks.Remove(task);
    await db.SaveChangesAsync(ct);

    return Results.NoContent();
})
.WithName("DeleteTask")
.WithSummary("Hard-delete a task.")
.WithTags("Tasks");

#endregion

app.Run();

static TaskResponse ToResponse(TaskItem task) => new(
    task.Id,
    task.Title,
    task.IsCompleted,
    task.CreatedAt,
    task.CompletedAt,
    task.Priority.ToString());

// Exposed so the integration test project can use WebApplicationFactory<Program>.
public partial class Program;
