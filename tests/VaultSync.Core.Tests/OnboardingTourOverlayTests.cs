#nullable enable

using System;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class OnboardingTourOverlayTests
{
    [Fact]
    public void Overlay_IsCardSizedSoGuidedViewsRemainInteractive()
    {
        string path = FindRepositoryFile(
            "src",
            "VaultSync.UI",
            "Views",
            "Controls",
            "OnboardingTourOverlay.axaml");

        XDocument document = XDocument.Load(path);
        XNamespace avalonia = "https://github.com/avaloniaui";

        XElement root = document.Root!;
        Assert.Equal("Right", (string?)root.Attribute("HorizontalAlignment"));
        Assert.Equal("Bottom", (string?)root.Attribute("VerticalAlignment"));
        Assert.Equal("430", (string?)root.Attribute("Width"));
        Assert.Null(root.Attribute("Background"));

        XElement tourCard = Assert.Single(root.Elements(avalonia + "Border"));
        Assert.NotNull(tourCard.Attribute("Background"));
    }

    [Fact]
    public void MainWindow_ReservesSeparateSpaceForGuidedSetup()
    {
        XDocument document = XDocument.Load(FindRepositoryFile("src", "VaultSync.UI", "MainWindow.axaml"));
        XElement tour = Assert.Single(document.Descendants(), element => element.Name.LocalName == "OnboardingTourOverlay");
        XElement shell = tour.Parent!;
        XNamespace avalonia = "https://github.com/avaloniaui";
        XElement page = Assert.Single(shell.Elements(avalonia + "Border"), border => (string?)border.Attribute("Grid.Row") == "1");

        Assert.Equal("Auto,*,Auto", (string?)shell.Attribute("RowDefinitions"));
        Assert.Equal("2", (string?)tour.Attribute("Grid.Row"));
        Assert.Null(tour.Attribute("Grid.RowSpan"));
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
