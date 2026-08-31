#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class VerticalScrollPolicyTests
{
    [Fact]
    public void ImplicitScrollers_DefaultToVerticalOnly()
    {
        string appPath = FindRepositoryFile(
            "src",
            "VaultSync.UI",
            "App.axaml");
        XNamespace avalonia = "https://github.com/avaloniaui";
        XDocument document = XDocument.Load(appPath);

        XElement? defaultScrollViewerStyle = document
            .Descendants(avalonia + "Style")
            .SingleOrDefault(element =>
                (string?)element.Attribute("Selector") == "ScrollViewer");

        bool disablesHorizontalScrolling = defaultScrollViewerStyle?
            .Elements(avalonia + "Setter")
            .Any(element =>
                (string?)element.Attribute("Property") == "HorizontalScrollBarVisibility" &&
                (string?)element.Attribute("Value") == "Disabled") == true;

        Assert.True(
            disablesHorizontalScrolling,
            "The shared ScrollViewer style must keep implicit control scrollers " +
            "vertical-only. Purpose-built horizontal panes opt in locally.");
    }

    [Fact]
    public void VerticalContentScrollers_DeclareTheirHorizontalBehavior()
    {
        string viewsDirectory = FindRepositoryDirectory(
            "src",
            "VaultSync.UI",
            "Views");
        XNamespace avalonia = "https://github.com/avaloniaui";
        var missingPolicies = new List<string>();

        foreach (string path in Directory.EnumerateFiles(
                     viewsDirectory,
                     "*.axaml",
                     SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(path);
            IEnumerable<XElement> verticalScrollers = document
                .Descendants(avalonia + "ScrollViewer")
                .Where(element =>
                    (string?)element.Attribute("VerticalScrollBarVisibility") == "Auto");

            foreach (XElement scroller in verticalScrollers)
            {
                if (scroller.Attribute("HorizontalScrollBarVisibility") is null)
                {
                    missingPolicies.Add(
                        $"{Path.GetRelativePath(viewsDirectory, path)}:{GetLineNumber(scroller)}");
                }
            }
        }

        Assert.True(
            missingPolicies.Count == 0,
            "Vertical ScrollViewer elements must explicitly opt out of horizontal " +
            $"scrolling or document an intentional horizontal pane: {string.Join(", ", missingPolicies)}");
    }

    [Fact]
    public void MetadataConflictComparison_StacksRevisionsVertically()
    {
        string settingsPath = FindRepositoryFile(
            "src",
            "VaultSync.UI",
            "Views",
            "SettingsView.axaml");
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument document = XDocument.Load(settingsPath);

        XElement comparison = document
            .Descendants(avalonia + "StackPanel")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") == "MetadataConflictComparison");
        XElement[] fields = comparison
            .Elements(avalonia + "StackPanel")
            .ToArray();

        Assert.Equal(7, fields.Length);
        Assert.All(fields, field =>
        {
            XElement grid = Assert.Single(field.Elements(avalonia + "Grid"));
            Assert.Equal("Auto,*", (string?)grid.Attribute("ColumnDefinitions"));
            Assert.Equal("Auto,Auto,Auto", (string?)grid.Attribute("RowDefinitions"));
            Assert.DoesNotContain(
                grid.DescendantsAndSelf(),
                element => (string?)element.Attribute("ColumnDefinitions") == "Auto,*,*,*");
        });
    }

    private static int GetLineNumber(XElement element) =>
        ((IXmlLineInfo)element).HasLineInfo()
            ? ((IXmlLineInfo)element).LineNumber
            : 0;

    private static string FindRepositoryDirectory(params string[] relativeParts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. relativeParts]);
            if (Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository directory: {Path.Combine(relativeParts)}");
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file: {Path.Combine(relativeParts)}");
    }
}
