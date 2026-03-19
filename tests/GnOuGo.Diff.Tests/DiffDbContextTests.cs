using Microsoft.EntityFrameworkCore;
using GnOuGo.Diff.Core.Data;
using GnOuGo.Diff.Core.Models;
using Xunit;

namespace GnOuGo.Diff.Tests;

public sealed class DiffDbContextTests : IDisposable
{
    private readonly DiffDbContext _context;

    public DiffDbContextTests()
    {
        var options = new DbContextOptionsBuilder<DiffDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _context = new DiffDbContext(options);
        _context.Database.OpenConnection();
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Database.CloseConnection();
        _context.Dispose();
    }

    [Fact]
    public async Task CanInsertAndRetrieveDiffEntry()
    {
        var entry = new DiffEntry
        {
            Id = Guid.NewGuid(),
            EntityType = "User",
            EntityId = "user-1",
            TimestampTicks = DateTimeOffset.UtcNow.UtcTicks,
            Author = "admin",
            CurrentValue = "{\"name\":\"Alice\"}",
            ValueHash = "abc123",
            DiffFromPrevious = null
        };

        _context.DiffEntries.Add(entry);
        await _context.SaveChangesAsync();

        var loaded = await _context.DiffEntries.FindAsync(entry.Id);

        Assert.NotNull(loaded);
        Assert.Equal("User", loaded.EntityType);
        Assert.Equal("user-1", loaded.EntityId);
        Assert.Equal("admin", loaded.Author);
        Assert.Equal("{\"name\":\"Alice\"}", loaded.CurrentValue);
        Assert.Equal("abc123", loaded.ValueHash);
        Assert.Null(loaded.DiffFromPrevious);
    }

    [Fact]
    public async Task CompositeIndex_EntityType_EntityId_TimestampTicks_Works()
    {
        // Insert multiple entries for the same entity
        for (int i = 0; i < 5; i++)
        {
            _context.DiffEntries.Add(new DiffEntry
            {
                Id = Guid.NewGuid(),
                EntityType = "Config",
                EntityId = "main",
                TimestampTicks = DateTimeOffset.UtcNow.AddMinutes(i).UtcTicks,
                Author = "system",
                CurrentValue = $"v{i}",
                ValueHash = $"hash{i}"
            });
        }

        await _context.SaveChangesAsync();

        var results = await _context.DiffEntries
            .Where(e => e.EntityType == "Config" && e.EntityId == "main")
            .OrderByDescending(e => e.TimestampTicks)
            .ToListAsync();

        Assert.Equal(5, results.Count);
        Assert.Equal("v4", results[0].CurrentValue); // latest first
    }

    [Fact]
    public async Task MultipleEntities_CanCoexist()
    {
        _context.DiffEntries.Add(new DiffEntry
        {
            Id = Guid.NewGuid(),
            EntityType = "User",
            EntityId = "1",
            TimestampTicks = DateTimeOffset.UtcNow.UtcTicks,
            Author = "a",
            CurrentValue = "u1",
            ValueHash = "h1"
        });

        _context.DiffEntries.Add(new DiffEntry
        {
            Id = Guid.NewGuid(),
            EntityType = "Order",
            EntityId = "100",
            TimestampTicks = DateTimeOffset.UtcNow.UtcTicks,
            Author = "b",
            CurrentValue = "o1",
            ValueHash = "h2"
        });

        await _context.SaveChangesAsync();

        var users = await _context.DiffEntries.Where(e => e.EntityType == "User").ToListAsync();
        var orders = await _context.DiffEntries.Where(e => e.EntityType == "Order").ToListAsync();

        Assert.Single(users);
        Assert.Single(orders);
    }

    [Fact]
    public async Task DiffFromPrevious_CanStoreText()
    {
        var entry = new DiffEntry
        {
            Id = Guid.NewGuid(),
            EntityType = "Doc",
            EntityId = "doc-1",
            TimestampTicks = DateTimeOffset.UtcNow.UtcTicks,
            Author = "editor",
            CurrentValue = "new content",
            ValueHash = "newhash",
            DiffFromPrevious = "- old line\n+ new line"
        };

        _context.DiffEntries.Add(entry);
        await _context.SaveChangesAsync();

        var loaded = await _context.DiffEntries.FindAsync(entry.Id);
        Assert.Equal("- old line\n+ new line", loaded!.DiffFromPrevious);
    }

    [Fact]
    public async Task TimestampProperty_IsIgnoredByEF()
    {
        // The Timestamp computed property is ignored by EF — only TimestampTicks is persisted
        var ts = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var entry = new DiffEntry
        {
            Id = Guid.NewGuid(),
            EntityType = "Test",
            EntityId = "1",
            Author = "x",
            CurrentValue = "{}",
            ValueHash = "h"
        };
        entry.Timestamp = ts;

        _context.DiffEntries.Add(entry);
        await _context.SaveChangesAsync();

        var loaded = await _context.DiffEntries.FindAsync(entry.Id);
        Assert.Equal(ts.UtcTicks, loaded!.TimestampTicks);
    }
}

