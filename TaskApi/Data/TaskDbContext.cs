using Microsoft.EntityFrameworkCore;
using TaskApi.Domain;

namespace TaskApi.Data;

public class TaskDbContext : DbContext
{
    public TaskDbContext(DbContextOptions<TaskDbContext> options) : base(options)
    {
    }

    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var task = modelBuilder.Entity<TaskItem>();

        task.ToTable("tasks");
        task.HasKey(t => t.Id);

        task.Property(t => t.Title)
            .IsRequired()
            .HasMaxLength(200);

        // Store the enum as a readable string, matching the JSON payload of the event.
        task.Property(t => t.Priority)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        task.Property(t => t.CreatedAt).IsRequired();
        task.Property(t => t.CompletedAt);

        // Optimistic concurrency the "Postgres way": the system column xmin is used as a
        // concurrency token instead of a manually managed RowVersion column. Two concurrent
        // completions -> the second SaveChanges throws DbUpdateConcurrencyException -> 409.
        //
        // NOTE: Npgsql 9 dropped UseXminAsConcurrencyToken(); the convention now maps a uint
        // property that is OnAddOrUpdate + concurrency token to the built-in xmin column
        // (no column is created in migrations/tables for it).
        task.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
