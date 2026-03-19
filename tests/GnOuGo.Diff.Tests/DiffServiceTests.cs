using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using GnOuGo.Diff.Core.Data;
using GnOuGo.Diff.Core.Models;
using GnOuGo.Diff.Core.Services;
using Xunit;

namespace GnOuGo.Diff.Tests;

public sealed class DiffServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DiffDbContext _context;
    private readonly DiffService _service;

    public DiffServiceTests()
    {
        // Use a shared in-memory SQLite connection that stays open for the test lifetime
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<DiffDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new DiffDbContext(options);
        _context.Database.EnsureCreated();
        _service = new DiffService(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── CreateRevisionAsync ──────────────────────────────────────────

    [Fact]
    public async Task CreateRevision_FirstRevision_IsFirstRevision()
    {
        var request = new CreateRevisionRequest("User", "1", "{\"name\":\"Alice\"}", "admin");

        var result = await _service.CreateRevisionAsync(request);

        Assert.True(result.IsFirstRevision);
        Assert.Null(result.DiffFromPrevious);
        Assert.Equal("User", result.EntityType);
        Assert.Equal("1", result.EntityId);
        Assert.Equal("{\"name\":\"Alice\"}", result.CurrentValue);
        Assert.Equal("admin", result.Author);
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    [Fact]
    public async Task CreateRevision_SecondRevision_HasDiff()
    {
        await _service.CreateRevisionAsync(
            new CreateRevisionRequest("User", "1", "line1\nline2", "admin"));

        var result = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("User", "1", "line1\nline3", "admin"));

        Assert.False(result.IsFirstRevision);
        Assert.NotNull(result.DiffFromPrevious);
        Assert.Contains("line2", result.DiffFromPrevious); // deleted
        Assert.Contains("line3", result.DiffFromPrevious); // added
    }

    [Fact]
    public async Task CreateRevision_DuplicateValue_ReturnsExistingRevision()
    {
        var request = new CreateRevisionRequest("Config", "main", "{\"key\":\"value\"}", "system");

        var first = await _service.CreateRevisionAsync(request);
        var second = await _service.CreateRevisionAsync(request); // same value

        // Should return the first revision (no new entry created)
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.CurrentValue, second.CurrentValue);
    }

    [Fact]
    public async Task CreateRevision_DifferentEntities_IndependentRevisions()
    {
        var r1 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("User", "1", "Alice", "admin"));
        var r2 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("User", "2", "Bob", "admin"));

        Assert.NotEqual(r1.Id, r2.Id);
        Assert.True(r1.IsFirstRevision);
        Assert.True(r2.IsFirstRevision);
    }

    [Fact]
    public async Task CreateRevision_DifferentEntityTypes_Independent()
    {
        var r1 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("User", "1", "{}", "admin"));
        var r2 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("Order", "1", "{}", "admin"));

        Assert.NotEqual(r1.Id, r2.Id);
        Assert.True(r1.IsFirstRevision);
        Assert.True(r2.IsFirstRevision);
    }

    [Fact]
    public async Task CreateRevision_MultipleRevisions_AllPersisted()
    {
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "v1", "a"));
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "v2", "b"));
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "v3", "c"));

        var revisions = await _service.GetRevisionsAsync("Doc", "1");

        Assert.Equal(3, revisions.Count);
    }

    [Fact]
    public async Task CreateRevision_SameValueAfterChange_CreatesNewEntry()
    {
        // v1 → v2 → v1 (reverting should create a new entry since hash differs from latest)
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "original", "a"));
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "modified", "a"));
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "original", "a")); // revert

        var revisions = await _service.GetRevisionsAsync("Doc", "1");
        Assert.Equal(3, revisions.Count);
    }

    // ── GetRevisionsAsync ────────────────────────────────────────────

    [Fact]
    public async Task GetRevisions_ReturnsOrderedByTimestampDesc()
    {
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "first", "a"));
        await Task.Delay(10); // ensure different timestamps
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "second", "a"));
        await Task.Delay(10);
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "third", "a"));

        var revisions = await _service.GetRevisionsAsync("Doc", "1");

        Assert.Equal(3, revisions.Count);
        Assert.Equal("third", revisions[0].CurrentValue);  // most recent first
        Assert.Equal("second", revisions[1].CurrentValue);
        Assert.Equal("first", revisions[2].CurrentValue);
    }

    [Fact]
    public async Task GetRevisions_EmptyForUnknownEntity()
    {
        var revisions = await _service.GetRevisionsAsync("Unknown", "999");
        Assert.Empty(revisions);
    }

    [Fact]
    public async Task GetRevisions_OnlyReturnsMatchingEntity()
    {
        await _service.CreateRevisionAsync(new CreateRevisionRequest("User", "1", "Alice", "a"));
        await _service.CreateRevisionAsync(new CreateRevisionRequest("User", "2", "Bob", "a"));
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Order", "1", "Order1", "a"));

        var user1 = await _service.GetRevisionsAsync("User", "1");
        Assert.Single(user1);
        Assert.Equal("Alice", user1[0].CurrentValue);
    }

    // ── GetRevisionAtTimestampAsync ──────────────────────────────────

    [Fact]
    public async Task GetRevisionAtTimestamp_ReturnsClosestBefore()
    {
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "v1", "a"));
        await Task.Delay(50);
        var midpoint = DateTimeOffset.UtcNow;
        await Task.Delay(50);
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "v2", "a"));

        var result = await _service.GetRevisionAtTimestampAsync("Doc", "1", midpoint);

        Assert.NotNull(result);
        Assert.Equal("v1", result.CurrentValue);
    }

    [Fact]
    public async Task GetRevisionAtTimestamp_FutureTimestamp_ReturnsLatest()
    {
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "v1", "a"));

        var future = DateTimeOffset.UtcNow.AddHours(1);
        var result = await _service.GetRevisionAtTimestampAsync("Doc", "1", future);

        Assert.NotNull(result);
        Assert.Equal("v1", result.CurrentValue);
    }

    [Fact]
    public async Task GetRevisionAtTimestamp_BeforeFirstRevision_ReturnsNull()
    {
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "v1", "a"));

        var past = DateTimeOffset.UtcNow.AddHours(-1);
        var result = await _service.GetRevisionAtTimestampAsync("Doc", "1", past);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetRevisionAtTimestamp_UnknownEntity_ReturnsNull()
    {
        var result = await _service.GetRevisionAtTimestampAsync("Nope", "1", DateTimeOffset.UtcNow);
        Assert.Null(result);
    }

    // ── CompareRevisionsAsync ────────────────────────────────────────

    [Fact]
    public async Task CompareRevisions_SameEntity_ReturnsDiff()
    {
        var r1 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("Doc", "1", "Hello\nWorld", "a"));
        await Task.Delay(10);
        var r2 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("Doc", "1", "Hello\nUniverse", "a"));

        var comparison = await _service.CompareRevisionsAsync(r1.Id, r2.Id);

        Assert.NotNull(comparison);
        Assert.Equal(r1.Id, comparison.FromRevision.Id);
        Assert.Equal(r2.Id, comparison.ToRevision.Id);
        Assert.Contains("World", comparison.UnifiedDiff);    // deleted line
        Assert.Contains("Universe", comparison.UnifiedDiff); // added line
        Assert.True(comparison.Stats.LinesAdded + comparison.Stats.LinesModified > 0);
    }

    [Fact]
    public async Task CompareRevisions_IdenticalContent_NoDiff()
    {
        // Create two different revisions with same content (after a change + revert)
        var r1 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("Doc", "1", "Same content", "a"));
        await Task.Delay(10);
        await _service.CreateRevisionAsync(
            new CreateRevisionRequest("Doc", "1", "Different", "a"));
        await Task.Delay(10);
        var r3 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("Doc", "1", "Same content", "a")); // revert

        var comparison = await _service.CompareRevisionsAsync(r1.Id, r3.Id);

        Assert.NotNull(comparison);
        Assert.Equal(0, comparison.Stats.LinesAdded);
        Assert.Equal(0, comparison.Stats.LinesDeleted);
        Assert.Equal(0, comparison.Stats.LinesModified);
    }

    [Fact]
    public async Task CompareRevisions_UnknownId_ReturnsNull()
    {
        var r1 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("Doc", "1", "Hello", "a"));

        var result = await _service.CompareRevisionsAsync(r1.Id, Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task CompareRevisions_BothUnknown_ReturnsNull()
    {
        var result = await _service.CompareRevisionsAsync(Guid.NewGuid(), Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task CompareRevisions_DifferentEntities_ThrowsInvalidOperation()
    {
        var r1 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("User", "1", "Alice", "a"));
        var r2 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("User", "2", "Bob", "a"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CompareRevisionsAsync(r1.Id, r2.Id));
    }

    [Fact]
    public async Task CompareRevisions_DifferentEntityTypes_ThrowsInvalidOperation()
    {
        var r1 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("User", "1", "{}", "a"));
        var r2 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("Order", "1", "{}", "a"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CompareRevisionsAsync(r1.Id, r2.Id));
    }

    [Fact]
    public async Task CompareRevisions_Stats_AreReasonable()
    {
        var r1 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("Doc", "1", "line1\nline2\nline3", "a"));
        await Task.Delay(10);
        var r2 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("Doc", "1", "line1\nmodified\nline3\nline4", "a"));

        var comparison = await _service.CompareRevisionsAsync(r1.Id, r2.Id);

        Assert.NotNull(comparison);
        var stats = comparison.Stats;
        // line1 and line3 are unchanged (but line3 moved so DiffPlex may count differently)
        Assert.True(stats.LinesUnchanged >= 1); // at least line1
        Assert.True(stats.LinesAdded + stats.LinesModified + stats.LinesDeleted > 0);
    }

    // ── GetEntityTypesAsync ──────────────────────────────────────────

    [Fact]
    public async Task GetEntityTypes_Empty_ReturnsEmpty()
    {
        var types = await _service.GetEntityTypesAsync();
        Assert.Empty(types);
    }

    [Fact]
    public async Task GetEntityTypes_ReturnsTypesWithCounts()
    {
        await _service.CreateRevisionAsync(new CreateRevisionRequest("User", "1", "a", "x"));
        await _service.CreateRevisionAsync(new CreateRevisionRequest("User", "2", "b", "x"));
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Order", "1", "c", "x"));

        var types = await _service.GetEntityTypesAsync();

        Assert.Equal(2, types.Count);
        Assert.Equal(2, types["User"]);
        Assert.Equal(1, types["Order"]);
    }

    [Fact]
    public async Task GetEntityTypes_CountsAllRevisions()
    {
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "v1", "a"));
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "v2", "a"));
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "v3", "a"));

        var types = await _service.GetEntityTypesAsync();

        Assert.Single(types);
        Assert.Equal(3, types["Doc"]);
    }

    // ── GetLatestRevisionsForTypeAsync ────────────────────────────────

    [Fact]
    public async Task GetLatestRevisionsForType_ReturnsLatestPerEntity()
    {
        await _service.CreateRevisionAsync(new CreateRevisionRequest("User", "1", "Alice v1", "a"));
        await Task.Delay(10);
        await _service.CreateRevisionAsync(new CreateRevisionRequest("User", "1", "Alice v2", "a"));
        await _service.CreateRevisionAsync(new CreateRevisionRequest("User", "2", "Bob", "a"));

        var latest = await _service.GetLatestRevisionsForTypeAsync("User");

        Assert.Equal(2, latest.Count);
        var alice = latest.FirstOrDefault(r => r.EntityId == "1");
        var bob = latest.FirstOrDefault(r => r.EntityId == "2");

        Assert.NotNull(alice);
        Assert.Equal("Alice v2", alice.CurrentValue); // latest for user 1
        Assert.NotNull(bob);
        Assert.Equal("Bob", bob.CurrentValue);
    }

    [Fact]
    public async Task GetLatestRevisionsForType_EmptyForUnknownType()
    {
        var latest = await _service.GetLatestRevisionsForTypeAsync("NonExistent");
        Assert.Empty(latest);
    }

    [Fact]
    public async Task GetLatestRevisionsForType_DoesNotMixTypes()
    {
        await _service.CreateRevisionAsync(new CreateRevisionRequest("User", "1", "Alice", "a"));
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Order", "1", "Order1", "a"));

        var users = await _service.GetLatestRevisionsForTypeAsync("User");
        Assert.Single(users);
        Assert.Equal("Alice", users[0].CurrentValue);
    }

    // ── Hash deduplication ───────────────────────────────────────────

    [Fact]
    public async Task DuplicateContent_NotStored_WhenConsecutive()
    {
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "same", "a"));
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "same", "a"));
        await _service.CreateRevisionAsync(new CreateRevisionRequest("Doc", "1", "same", "a"));

        var revisions = await _service.GetRevisionsAsync("Doc", "1");
        Assert.Single(revisions); // only one revision stored
    }

    // ── End-to-end scenario ──────────────────────────────────────────

    [Fact]
    public async Task FullLifecycle_Create_Get_Compare()
    {
        // Create entity
        var r1 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("Config", "db-connection",
                "server=localhost;db=mydb", "deploy-bot"));

        Assert.True(r1.IsFirstRevision);

        // Update entity
        await Task.Delay(10);
        var r2 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("Config", "db-connection",
                "server=prod-db.internal;db=mydb", "deploy-bot"));

        Assert.False(r2.IsFirstRevision);

        // List revisions
        var revisions = await _service.GetRevisionsAsync("Config", "db-connection");
        Assert.Equal(2, revisions.Count);

        // Compare
        var comparison = await _service.CompareRevisionsAsync(r1.Id, r2.Id);
        Assert.NotNull(comparison);
        Assert.Contains("localhost", comparison.UnifiedDiff);
        Assert.Contains("prod-db.internal", comparison.UnifiedDiff);

        // Entity types
        var types = await _service.GetEntityTypesAsync();
        Assert.Contains("Config", types.Keys);

        // Latest revisions for type
        var latest = await _service.GetLatestRevisionsForTypeAsync("Config");
        Assert.Single(latest);
        Assert.Contains("prod-db.internal", latest[0].CurrentValue);
    }

    [Fact]
    public async Task MultilineContent_DiffIsAccurate()
    {
        var v1 = "line1\nline2\nline3\nline4\nline5";
        var v2 = "line1\nLINE2\nline3\nnew_line\nline5";

        var r1 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("Doc", "ml", v1, "editor"));
        await Task.Delay(10);
        var r2 = await _service.CreateRevisionAsync(
            new CreateRevisionRequest("Doc", "ml", v2, "editor"));

        var comparison = await _service.CompareRevisionsAsync(r1.Id, r2.Id);

        Assert.NotNull(comparison);
        // The unified diff should show changes
        Assert.NotEmpty(comparison.UnifiedDiff);
        // Stats should reflect changes
        Assert.True(comparison.Stats.LinesUnchanged > 0);
    }
}

