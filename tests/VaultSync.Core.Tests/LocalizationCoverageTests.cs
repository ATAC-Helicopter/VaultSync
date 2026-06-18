using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LocalizationCoverageTests
{
    [Fact]
    public void ShippedLocales_MatchEnglishKeySet()
    {
        string localizationDirectory = FindLocalizationDirectory();
        HashSet<string> englishKeys = ReadKeys(Path.Combine(localizationDirectory, "strings.en.json"));

        foreach (string localePath in Directory.GetFiles(localizationDirectory, "strings.*.json"))
        {
            HashSet<string> localeKeys = ReadKeys(localePath);
            string locale = Path.GetFileName(localePath);

            Assert.True(
                englishKeys.SetEquals(localeKeys),
                $"{locale} key mismatch. Missing: {string.Join(", ", englishKeys.Except(localeKeys).Order())}; " +
                $"Extra: {string.Join(", ", localeKeys.Except(englishKeys).Order())}");
        }
    }

    private static HashSet<string> ReadKeys(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet();
    }

    private static string FindLocalizationDirectory()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "Localization");
            if (File.Exists(Path.Combine(candidate, "strings.en.json")))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository Localization directory.");
    }
}
