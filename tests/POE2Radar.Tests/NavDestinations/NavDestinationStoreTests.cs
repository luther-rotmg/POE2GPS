using System;
using System.IO;
using System.Linq;
using POE2Radar.Core.NavDestinations;
using Xunit;

namespace POE2Radar.Tests.NavDestinations;

public sealed class NavDestinationStoreTests : IDisposable
{
    private readonly string _configDir;

    public NavDestinationStoreTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "poe2gps-navdest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // --- Helpers ---

    private static NavDestination MakeDest(Guid id, string zoneCode, string name, int x = 100, int y = 200)
    {
        return new NavDestination(id, zoneCode, name, x, y);
    }

    // --- Tests ---

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var file = NavDestinationStore.Load(_configDir);
        Assert.Equal(1, file.SchemaVersion);
        Assert.Empty(file.Destinations);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        var destinations = new[]
        {
            MakeDest(id1, "T17_Necropolis", "chest room", 145, 220),
            MakeDest(id2, "Delirium_Mirror", "altar", 50, 180),
            MakeDest(id3, "T17_Necropolis", "boss", 300, 400),
        };

        var file = new NavDestinationFile(1, destinations);
        NavDestinationStore.Save(_configDir, file);

        var loaded = NavDestinationStore.Load(_configDir);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(3, loaded.Destinations.Count);

        // Verify first destination
        Assert.Equal(id1, loaded.Destinations[0].Id);
        Assert.Equal("T17_Necropolis", loaded.Destinations[0].ZoneCode);
        Assert.Equal("chest room", loaded.Destinations[0].Name);
        Assert.Equal(145, loaded.Destinations[0].X);
        Assert.Equal(220, loaded.Destinations[0].Y);

        // Verify second destination
        Assert.Equal(id2, loaded.Destinations[1].Id);
        Assert.Equal("Delirium_Mirror", loaded.Destinations[1].ZoneCode);
        Assert.Equal("altar", loaded.Destinations[1].Name);
        Assert.Equal(50, loaded.Destinations[1].X);
        Assert.Equal(180, loaded.Destinations[1].Y);

        // Verify third destination
        Assert.Equal(id3, loaded.Destinations[2].Id);
        Assert.Equal("T17_Necropolis", loaded.Destinations[2].ZoneCode);
        Assert.Equal("boss", loaded.Destinations[2].Name);
        Assert.Equal(300, loaded.Destinations[2].X);
        Assert.Equal(400, loaded.Destinations[2].Y);
    }

    [Fact]
    public void Save_AssignsGuidToEmptyId()
    {
        var dest = MakeDest(Guid.Empty, "T17_Necropolis", "no id");
        var file = new NavDestinationFile(1, new[] { dest });
        NavDestinationStore.Save(_configDir, file);

        var loaded = NavDestinationStore.Load(_configDir);
        var saved = Assert.Single(loaded.Destinations);
        Assert.NotEqual(Guid.Empty, saved.Id);
    }

    [Fact]
    public void Upsert_NewDestination_Appends()
    {
        var id = Guid.NewGuid();
        var dest = MakeDest(id, "T17_Necropolis", "new spot");

        NavDestinationStore.Upsert(_configDir, dest);

        var loaded = NavDestinationStore.Load(_configDir);
        Assert.Single(loaded.Destinations);
        Assert.Equal(id, loaded.Destinations[0].Id);
        Assert.Equal("T17_Necropolis", loaded.Destinations[0].ZoneCode);
        Assert.Equal("new spot", loaded.Destinations[0].Name);
    }

    [Fact]
    public void Upsert_ExistingId_Replaces()
    {
        var id = Guid.NewGuid();
        var original = MakeDest(id, "T17_Necropolis", "original");
        NavDestinationStore.Upsert(_configDir, original);

        var replacement = MakeDest(id, "T17_Necropolis", "replaced", 999, 888);
        NavDestinationStore.Upsert(_configDir, replacement);

        var loaded = NavDestinationStore.Load(_configDir);
        Assert.Single(loaded.Destinations);
        Assert.Equal("replaced", loaded.Destinations[0].Name);
        Assert.Equal(999, loaded.Destinations[0].X);
        Assert.Equal(888, loaded.Destinations[0].Y);
    }

    [Fact]
    public void Upsert_DuplicateZoneAndName_AtCreate_Throws()
    {
        var id1 = Guid.NewGuid();
        var dest1 = MakeDest(id1, "T17_Necropolis", "chest room");
        NavDestinationStore.Upsert(_configDir, dest1);

        // Same zoneCode and name, different id
        var id2 = Guid.NewGuid();
        var dest2 = MakeDest(id2, "T17_Necropolis", "chest room", 500, 600);

        var ex = Assert.Throws<ArgumentException>(() => NavDestinationStore.Upsert(_configDir, dest2));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Upsert_SameIdDifferentName_Replaces_NoDupError()
    {
        var id = Guid.NewGuid();
        var original = MakeDest(id, "T17_Necropolis", "original name");
        NavDestinationStore.Upsert(_configDir, original);

        // Update the same id with a different name — should not throw
        var updated = MakeDest(id, "T17_Necropolis", "new name");
        NavDestinationStore.Upsert(_configDir, updated);

        var loaded = NavDestinationStore.Load(_configDir);
        Assert.Single(loaded.Destinations);
        Assert.Equal("new name", loaded.Destinations[0].Name);
    }

    [Fact]
    public void Delete_ExistingId_Returns_True()
    {
        var id = Guid.NewGuid();
        var dest = MakeDest(id, "T17_Necropolis", "to delete");
        NavDestinationStore.Upsert(_configDir, dest);

        var result = NavDestinationStore.Delete(_configDir, id);
        Assert.True(result);

        var loaded = NavDestinationStore.Load(_configDir);
        Assert.Empty(loaded.Destinations);
    }

    [Fact]
    public void Delete_MissingId_Returns_False_NoThrow()
    {
        // Should not throw even though file doesn't exist
        var result = NavDestinationStore.Delete(_configDir, Guid.NewGuid());
        Assert.False(result);
    }

    [Fact]
    public void Save_Over50Destinations_Throws()
    {
        var destinations = new NavDestination[51];
        for (int i = 0; i < 51; i++)
            destinations[i] = MakeDest(Guid.NewGuid(), "T17_Necropolis", $"spot{i}");

        var file = new NavDestinationFile(1, destinations);

        var ex = Assert.Throws<ArgumentException>(() => NavDestinationStore.Save(_configDir, file));
        Assert.Contains("50", ex.Message);
    }

    [Fact]
    public void ValidateDestination_EmptyZone_Throws()
    {
        var dest = MakeDest(Guid.NewGuid(), "", "name");

        var ex = Assert.Throws<ArgumentException>(() => NavDestinationStore.ValidateDestination(dest));
        Assert.Contains("ZoneCode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDestination_EmptyName_Throws()
    {
        var dest = MakeDest(Guid.NewGuid(), "T17_Necropolis", "");

        var ex = Assert.Throws<ArgumentException>(() => NavDestinationStore.ValidateDestination(dest));
        Assert.Contains("Name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDestination_ZoneOver64_Throws()
    {
        var zoneCode = new string('z', 65);
        var dest = MakeDest(Guid.NewGuid(), zoneCode, "name");

        var ex = Assert.Throws<ArgumentException>(() => NavDestinationStore.ValidateDestination(dest));
        Assert.Contains("64", ex.Message);
    }

    [Fact]
    public void ValidateDestination_NameOver40_Throws()
    {
        var name = new string('n', 41);
        var dest = MakeDest(Guid.NewGuid(), "T17_Necropolis", name);

        var ex = Assert.Throws<ArgumentException>(() => NavDestinationStore.ValidateDestination(dest));
        Assert.Contains("40", ex.Message);
    }

    [Fact]
    public void LoadForZone_ExactMatchOnly()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        NavDestinationStore.Upsert(_configDir, MakeDest(id1, "T17_Necropolis", "chest room", 145, 220));
        NavDestinationStore.Upsert(_configDir, MakeDest(id2, "Delirium_Mirror", "altar", 50, 180));
        NavDestinationStore.Upsert(_configDir, MakeDest(id3, "T17_Necropolis", "boss", 300, 400));

        var results = NavDestinationStore.LoadForZone(_configDir, "T17_Necropolis");

        Assert.Equal(2, results.Count);
        Assert.Contains(results, d => d.Id == id1);
        Assert.Contains(results, d => d.Id == id3);
        Assert.DoesNotContain(results, d => d.Id == id2);
    }

    [Fact]
    public void LoadForZone_CaseSensitive()
    {
        var id = Guid.NewGuid();
        NavDestinationStore.Upsert(_configDir, MakeDest(id, "T17_Necropolis", "chest room"));

        // Different case should not match
        var results = NavDestinationStore.LoadForZone(_configDir, "t17_necropolis");
        Assert.Empty(results);

        // Exact case should match
        var exactResults = NavDestinationStore.LoadForZone(_configDir, "T17_Necropolis");
        Assert.Single(exactResults);
    }

    [Fact]
    public void LoadForZone_UnknownZone_ReturnsEmpty()
    {
        NavDestinationStore.Upsert(_configDir, MakeDest(Guid.NewGuid(), "T17_Necropolis", "chest room"));

        var results = NavDestinationStore.LoadForZone(_configDir, "NonExistentZone");
        Assert.Empty(results);
    }
}