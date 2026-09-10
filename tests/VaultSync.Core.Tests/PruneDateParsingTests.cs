using System;
using VaultSync.CLI.Commands;
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
}
