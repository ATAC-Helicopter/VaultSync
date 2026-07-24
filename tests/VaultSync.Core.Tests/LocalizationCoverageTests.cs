#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Data;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LocalizationCoverageTests
{
    private static readonly Regex FormatPlaceholder = new(
        @"\{\d+(?:,[^}:]+)?(?::[^}]+)?\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void ShippedLocales_MatchEnglishContracts()
    {
        string localizationDirectory = FindLocalizationDirectory();
        Dictionary<string, string> english = ReadLocale(Path.Combine(localizationDirectory, "strings.en.json"));

        foreach (string localePath in Directory.GetFiles(localizationDirectory, "strings.*.json"))
        {
            Dictionary<string, string> localeValues = ReadLocale(localePath);
            string locale = Path.GetFileName(localePath);

            Assert.True(
                english.Keys.ToHashSet().SetEquals(localeValues.Keys),
                $"{locale} key mismatch. Missing: {string.Join(", ", english.Keys.Except(localeValues.Keys).Order())}; " +
                $"Extra: {string.Join(", ", localeValues.Keys.Except(english.Keys).Order())}");

            foreach ((string key, string value) in localeValues)
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"{locale} has an empty value for {key}.");
                Assert.Equal(
                    GetPlaceholders(english[key]),
                    GetPlaceholders(value));
            }
        }
    }

    [Fact]
    public void SupportedLanguages_MatchShippedLocaleFiles()
    {
        string localizationDirectory = FindLocalizationDirectory();
        string[] shippedCodes = Directory.GetFiles(localizationDirectory, "strings.*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path)["strings.".Length..])
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] supportedCodes = new LocalizationService().SupportedLanguages
            .Select(language => language.Code)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(shippedCodes, supportedCodes);
    }

    [Fact]
    public void LocalizedStringExtension_ReturnsBinding_WhenProviderIsNotAvailableYet()
    {
        PropertyInfo? serviceProperty = typeof(LocalizationProvider).GetProperty("Service", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(serviceProperty);

        object? previousService = serviceProperty!.GetValue(null);
        serviceProperty.SetValue(null, null);

        try
        {
            var extension = new LocalizedStringExtension { Key = "Dashboard.Kpi.Projects" };
            object value = extension.ProvideValue(null!);

            Assert.IsType<Binding>(value);
        }
        finally
        {
            serviceProperty.SetValue(null, previousService);
        }
    }

    private static Dictionary<string, string> ReadLocale(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonProperty[] properties = document.RootElement.EnumerateObject().ToArray();
        string[] duplicateKeys = properties
            .GroupBy(property => property.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(duplicateKeys.Length == 0, $"{Path.GetFileName(path)} has duplicate keys: {string.Join(", ", duplicateKeys)}");

        var locale = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in properties)
        {
            Assert.Equal(JsonValueKind.String, property.Value.ValueKind);
            locale.Add(property.Name, property.Value.GetString()!);
        }

        return locale;
    }

    private static string[] GetPlaceholders(string value) => FormatPlaceholder.Matches(value)
        .Select(match => match.Value)
        .Order(StringComparer.Ordinal)
        .ToArray();

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
