using GnOuGo.Diff.Core.Models;
using Xunit;

namespace GnOuGo.Diff.Tests;

public sealed class DtoTests
{
    // ── CreateRevisionRequest ────────────────────────────────────────

    [Fact]
    public void CreateRevisionRequest_RecordEquality()
    {
        var a = new CreateRevisionRequest("User", "1", "{}", "admin");
        var b = new CreateRevisionRequest("User", "1", "{}", "admin");

        Assert.Equal(a, b);
    }

    [Fact]
    public void CreateRevisionRequest_RecordInequality()
    {
        var a = new CreateRevisionRequest("User", "1", "{}", "admin");
        var b = new CreateRevisionRequest("User", "2", "{}", "admin");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CreateRevisionRequest_Properties()
    {
        var req = new CreateRevisionRequest("Order", "42", "{\"total\":100}", "system");

        Assert.Equal("Order", req.EntityType);
        Assert.Equal("42", req.EntityId);
        Assert.Equal("{\"total\":100}", req.CurrentValue);
        Assert.Equal("system", req.Author);
    }

    [Fact]
    public void CreateRevisionRequest_With_CreatesNewInstance()
    {
        var original = new CreateRevisionRequest("User", "1", "{}", "admin");
        var modified = original with { Author = "superadmin" };

        Assert.Equal("superadmin", modified.Author);
        Assert.Equal("admin", original.Author);
        Assert.Equal(original.EntityType, modified.EntityType);
    }

    // ── RevisionDto ──────────────────────────────────────────────────

    [Fact]
    public void RevisionDto_Properties()
    {
        var id = Guid.NewGuid();
        var ts = DateTimeOffset.UtcNow;

        var dto = new RevisionDto(id, "Config", "main", ts, "dev", "{}", null, true);

        Assert.Equal(id, dto.Id);
        Assert.Equal("Config", dto.EntityType);
        Assert.Equal("main", dto.EntityId);
        Assert.Equal(ts, dto.Timestamp);
        Assert.Equal("dev", dto.Author);
        Assert.Equal("{}", dto.CurrentValue);
        Assert.Null(dto.DiffFromPrevious);
        Assert.True(dto.IsFirstRevision);
    }

    [Fact]
    public void RevisionDto_NotFirstRevision()
    {
        var dto = new RevisionDto(Guid.NewGuid(), "User", "1", DateTimeOffset.UtcNow,
            "admin", "{\"v\":2}", "+ v: 2\n- v: 1", false);

        Assert.False(dto.IsFirstRevision);
        Assert.NotNull(dto.DiffFromPrevious);
    }

    [Fact]
    public void RevisionDto_RecordEquality()
    {
        var id = Guid.NewGuid();
        var ts = new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero);

        var a = new RevisionDto(id, "T", "1", ts, "a", "{}", null, true);
        var b = new RevisionDto(id, "T", "1", ts, "a", "{}", null, true);

        Assert.Equal(a, b);
    }

    // ── DiffStats ────────────────────────────────────────────────────

    [Fact]
    public void DiffStats_Properties()
    {
        var stats = new DiffStats(5, 3, 2, 10);

        Assert.Equal(5, stats.LinesAdded);
        Assert.Equal(3, stats.LinesDeleted);
        Assert.Equal(2, stats.LinesModified);
        Assert.Equal(10, stats.LinesUnchanged);
    }

    [Fact]
    public void DiffStats_AllZeros()
    {
        var stats = new DiffStats(0, 0, 0, 0);

        Assert.Equal(0, stats.LinesAdded);
        Assert.Equal(0, stats.LinesDeleted);
        Assert.Equal(0, stats.LinesModified);
        Assert.Equal(0, stats.LinesUnchanged);
    }

    [Fact]
    public void DiffStats_RecordEquality()
    {
        Assert.Equal(new DiffStats(1, 2, 3, 4), new DiffStats(1, 2, 3, 4));
        Assert.NotEqual(new DiffStats(1, 2, 3, 4), new DiffStats(1, 2, 3, 5));
    }

    // ── ComparisonResult ─────────────────────────────────────────────

    [Fact]
    public void ComparisonResult_Properties()
    {
        var from = new RevisionDto(Guid.NewGuid(), "T", "1", DateTimeOffset.UtcNow, "a", "v1", null, true);
        var to = new RevisionDto(Guid.NewGuid(), "T", "1", DateTimeOffset.UtcNow, "b", "v2", "+ line", false);
        var stats = new DiffStats(1, 0, 0, 5);

        var result = new ComparisonResult(from, to, "- v1\n+ v2", stats);

        Assert.Same(from, result.FromRevision);
        Assert.Same(to, result.ToRevision);
        Assert.Equal("- v1\n+ v2", result.UnifiedDiff);
        Assert.Equal(stats, result.Stats);
    }
}

