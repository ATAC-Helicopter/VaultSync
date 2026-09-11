using System;
using System.Collections.Generic;
using VaultSync.CLI.Commands;
using VaultSync.Core.Models;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class PruneDateParsingTests
{
    [Fact]
    public void TryParseBeforeDateAcceptsExactInvariantDate()
    {
        bool parsed = PruneSettings.TryParseBeforeDate("2026-09-11", out DateTime value);

        Assert.True(parsed);
        Assert.Equal(new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc), value);
    }

    [Theory]
    [InlineData("09/11/2026")]
    [InlineData("2026-02-30")]
    public void TryParseBeforeDateRejectsAmbiguousOrInvalidDates(string value)
    {
        Assert.False(PruneSettings.TryParseBeforeDate(value, out _));
    }

    [Fact]
    public void PlanPrunePreservesBackedOrProtectedSnapshots()
    {
        Snapshot[] snapshots =
        [
            new() { Id = 3, CreatedUtc = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc) },
            new() { Id = 2, CreatedUtc = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc) },
            new() { Id = 1, CreatedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) }
        ];
        var settings = new PruneSettings { KeepLast = 0 };

        List<int> planned = PruneCommand.PlanPrune(snapshots, settings, new HashSet<int> { 1, 2 });

        Assert.Equal([3], planned);
    }
}
