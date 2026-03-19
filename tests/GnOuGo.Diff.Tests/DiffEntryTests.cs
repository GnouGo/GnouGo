using GnOuGo.Diff.Core.Models;
using Xunit;

namespace GnOuGo.Diff.Tests;

public sealed class DiffEntryTests
{
    [Fact]
    public void Timestamp_GetSet_RoundTrips()
    {
        var entry = new DiffEntry
        {
            EntityType = "User",
            EntityId = "1",
            Author = "admin",
            CurrentValue = "{}",
            ValueHash = "abc"
        };

        var now = DateTimeOffset.UtcNow;
        entry.Timestamp = now;

        Assert.Equal(now.UtcTicks, entry.TimestampTicks);
        Assert.Equal(now, entry.Timestamp);
    }

    [Fact]
    public void Timestamp_SetViaTicks_ReflectsInProperty()
    {
        var entry = new DiffEntry
        {
            EntityType = "Order",
            EntityId = "42",
            Author = "system",
            CurrentValue = "{\"total\":100}",
            ValueHash = "def"
        };

        var expected = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        entry.TimestampTicks = expected.UtcTicks;

        Assert.Equal(expected, entry.Timestamp);
    }

    [Fact]
    public void Timestamp_SetMultipleTimes_LastWins()
    {
        var entry = new DiffEntry
        {
            EntityType = "Config",
            EntityId = "main",
            Author = "dev",
            CurrentValue = "v1",
            ValueHash = "hash1"
        };

        var t1 = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero);

        entry.Timestamp = t1;
        Assert.Equal(t1, entry.Timestamp);

        entry.Timestamp = t2;
        Assert.Equal(t2, entry.Timestamp);
    }

    [Fact]
    public void DiffFromPrevious_IsNull_ForFirstRevision()
    {
        var entry = new DiffEntry
        {
            EntityType = "Doc",
            EntityId = "doc1",
            Author = "user",
            CurrentValue = "hello",
            ValueHash = "xyz",
            DiffFromPrevious = null
        };

        Assert.Null(entry.DiffFromPrevious);
    }

    [Fact]
    public void Id_CanBeSet()
    {
        var id = Guid.NewGuid();
        var entry = new DiffEntry
        {
            Id = id,
            EntityType = "Test",
            EntityId = "1",
            Author = "a",
            CurrentValue = "v",
            ValueHash = "h"
        };

        Assert.Equal(id, entry.Id);
    }

    [Fact]
    public void AllProperties_AreAssignable()
    {
        var entry = new DiffEntry
        {
            Id = Guid.NewGuid(),
            EntityType = "Product",
            EntityId = "SKU-001",
            Author = "warehouse",
            CurrentValue = "{\"name\":\"Widget\",\"price\":9.99}",
            ValueHash = "aabbccdd",
            DiffFromPrevious = "+ price: 9.99\n- price: 8.99"
        };

        Assert.Equal("Product", entry.EntityType);
        Assert.Equal("SKU-001", entry.EntityId);
        Assert.Equal("warehouse", entry.Author);
        Assert.Contains("Widget", entry.CurrentValue);
        Assert.Contains("+ price", entry.DiffFromPrevious);
        Assert.Equal("aabbccdd", entry.ValueHash);
    }
}

