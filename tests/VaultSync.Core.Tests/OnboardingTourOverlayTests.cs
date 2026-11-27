#nullable enable

using System;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class OnboardingTourOverlayTests
{
    [Fact]
    public void Overlay_UsesTransparentCanvasWithNonBlockingHighlightAndCard()
    {
        string path = FindRepositoryFile(
            "src",
            "VaultSync.UI",
            "Views",
            "Controls",
            "OnboardingTourOverlay.axaml");

        XDocument document = XDocument.Load(path);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement root = document.Root!;
        Assert.Null(root.Attribute("Background"));

        XElement canvas = Assert.Single(root.Elements(avalonia + "Canvas"));
        Assert.Null(canvas.Attribute("Background"));

        XElement highlight = Assert.Single(
            canvas.Elements(avalonia + "Border"),
            border => (string?)border.Attribute(x + "Name") == "TargetHighlight");
        Assert.Equal("False", (string?)highlight.Attribute("IsHitTestVisible"));

        XElement tourCard = Assert.Single(
            canvas.Elements(avalonia + "Border"),
            border => (string?)border.Attribute(x + "Name") == "TourCard");
        Assert.Equal("300", (string?)tourCard.Attribute("Width"));
        Assert.NotNull(tourCard.Attribute("Background"));
    }

    [Fact]
    public void MainWindow_OverlaysGuidedSetupAcrossShell()
    {
        XDocument document = XDocument.Load(FindRepositoryFile("src", "VaultSync.UI", "MainWindow.axaml"));
        XElement tour = Assert.Single(document.Descendants(), element => element.Name.LocalName == "OnboardingTourOverlay");
        XElement shell = tour.Parent!;
        XNamespace avalonia = "https://github.com/avaloniaui";
        XElement page = Assert.Single(shell.Elements(avalonia + "Border"), border => (string?)border.Attribute("Grid.Row") == "1");

        Assert.Equal("Auto,*", (string?)shell.Attribute("RowDefinitions"));
        Assert.Equal("2", (string?)tour.Attribute("Grid.RowSpan"));
        Assert.Equal("2", (string?)tour.Attribute("Grid.ColumnSpan"));
        Assert.Null(tour.Attribute("Grid.Row"));
        Assert.Null(page.Attribute("Grid.RowSpan"));
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
