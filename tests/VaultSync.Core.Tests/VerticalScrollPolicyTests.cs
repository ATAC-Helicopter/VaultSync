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
}
