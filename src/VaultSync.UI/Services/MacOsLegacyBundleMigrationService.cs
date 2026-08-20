using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Xml.Linq;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.Services;

internal static class MacOsLegacyBundleMigrationService
{
    private const string CanonicalBundleName = "VaultSync.app";
    private const string CanonicalIdentifier = "com.vaultsync.app";
    private const string TransitionVersion = "1.8.7";

    internal static bool TryMigrateAndRelaunch(string runtimeDirectory)
    {
        if (!OperatingSystem.IsMacOS() || !IsTransitionBuild(AppBuildInformationService.Current.Version))
            return false;

        string? legacyBundle = ResolveLegacyBundle(runtimeDirectory);
        if (legacyBundle is null || IsLinkedDirectory(legacyBundle))
            return false;

        string parent = Directory.GetParent(legacyBundle)?.FullName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(parent))
            return false;

        string canonicalBundle = Path.Combine(parent, CanonicalBundleName);
        string temporaryBundle = Path.Combine(parent, $".{CanonicalBundleName}.migrating-{Guid.NewGuid():N}");

        try
        {
            if (Directory.Exists(canonicalBundle))
            {
                if (IsLinkedDirectory(canonicalBundle) ||
                    !IsCanonicalBundle(canonicalBundle, TransitionVersion))
                {
                    DiagnosticsLogger.Record(
                        $"Legacy macOS bundle migration stopped because {canonicalBundle} already exists and is not the expected target.");
                    return false;
                }

                if (!LaunchBundle(canonicalBundle))
                    return false;

                TryMoveLegacyBundleToTrash(legacyBundle);
                return true;
            }

            RunRequired("/usr/bin/ditto", legacyBundle, temporaryBundle);
            if (IsLinkedDirectory(temporaryBundle))
                throw new InvalidDataException("The staged macOS application is a linked directory.");
            WriteCanonicalInfoPlist(temporaryBundle, TransitionVersion);
            InstallCanonicalIcon(runtimeDirectory, temporaryBundle);
            RunRequired(
                "/usr/bin/codesign",
                "--force",
                "--deep",
                "--sign",
                "-",
                "--identifier",
                CanonicalIdentifier,
                temporaryBundle);

            if (!IsCanonicalBundle(temporaryBundle, TransitionVersion))
                throw new InvalidDataException("The migrated macOS application failed its identity check.");

            Directory.Move(temporaryBundle, canonicalBundle);
            if (!LaunchBundle(canonicalBundle))
                throw new InvalidOperationException("The migrated macOS application could not be launched.");

            TryMoveLegacyBundleToTrash(legacyBundle);
            DiagnosticsLogger.Record($"Migrated legacy macOS application to {canonicalBundle}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticsLogger.Record($"Legacy macOS bundle migration failed: {ex.GetType().Name} - {ex.Message}");
            TryDeleteDirectory(temporaryBundle);
            return false;
        }
    }

    internal static string? ResolveLegacyBundle(string runtimeDirectory)
    {
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
            return null;

        string runtime = Path.GetFullPath(runtimeDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var macOs = new DirectoryInfo(runtime);
        DirectoryInfo? contents = macOs.Parent;
        DirectoryInfo? bundle = contents?.Parent;
        if (contents is null || bundle is null ||
            !string.Equals(macOs.Name, "MacOS", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(contents.Name, "Contents", StringComparison.OrdinalIgnoreCase) ||
            !IsLegacyBundleName(bundle.Name))
        {
            return null;
        }

        return bundle.FullName;
    }

    internal static bool IsLegacyBundleName(string name) =>
        string.Equals(name, "VaultSync-macos-arm64.app", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "VaultSync-macos-x64.app", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTransitionBuild(string version)
    {
        string normalized = (version ?? string.Empty).Trim();
        int metadata = normalized.IndexOf('+', StringComparison.Ordinal);
        if (metadata >= 0)
            normalized = normalized[..metadata];
        return string.Equals(normalized, TransitionVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteCanonicalInfoPlist(string bundle, string version)
    {
        string plistPath = Path.Combine(bundle, "Contents", "Info.plist");
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType(
                "plist",
                "-//Apple//DTD PLIST 1.0//EN",
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd",
                null),
            new XElement(
                "plist",
                new XAttribute("version", "1.0"),
                new XElement(
                    "dict",
                    PlistEntry("CFBundleName", "VaultSync"),
                    PlistEntry("CFBundleDisplayName", "VaultSync"),
                    PlistEntry("CFBundleIdentifier", CanonicalIdentifier),
                    PlistEntry("CFBundleVersion", version),
                    PlistEntry("CFBundleShortVersionString", version),
                    PlistEntry("CFBundlePackageType", "APPL"),
                    PlistEntry("CFBundleExecutable", "VaultSync.UI"),
                    PlistEntry("CFBundleIconFile", "VaultSync"),
                    PlistEntry("LSMinimumSystemVersion", "12.0"))));
        document.Save(plistPath);
    }

    private static object[] PlistEntry(string key, string value) =>
        [new XElement("key", key), new XElement("string", value)];

    private static void InstallCanonicalIcon(string runtimeDirectory, string bundle)
    {
        string source = Path.Combine(runtimeDirectory, "migration", "VaultSync.icns");
        if (!File.Exists(source))
            return;

        string resources = Path.Combine(bundle, "Contents", "Resources");
        Directory.CreateDirectory(resources);
        File.Copy(source, Path.Combine(resources, "VaultSync.icns"), overwrite: true);
    }

    private static bool IsCanonicalBundle(string bundle, string version)
    {
        try
        {
            string plistPath = Path.Combine(bundle, "Contents", "Info.plist");
            XDocument document = XDocument.Load(plistPath);
            XElement[] values = document.Descendants("dict").Elements().ToArray();
            string? Read(string key)
            {
                for (int index = 0; index + 1 < values.Length; index++)
                {
                    if (values[index].Name.LocalName == "key" && values[index].Value == key)
                        return values[index + 1].Value;
                }
                return null;
            }

            return string.Equals(Read("CFBundleIdentifier"), CanonicalIdentifier, StringComparison.Ordinal) &&
                   string.Equals(Read("CFBundleShortVersionString"), version, StringComparison.Ordinal) &&
                   File.Exists(Path.Combine(bundle, "Contents", "MacOS", "VaultSync.UI"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    private static bool LaunchBundle(string bundle)
    {
        try
        {
            using Process? process = Process.Start(CreateProcess("/usr/bin/open", "-n", bundle));
            return process is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiagnosticsLogger.Record($"Could not launch migrated macOS application: {ex.Message}");
            return false;
        }
    }

    private static void RunRequired(string executable, params string[] arguments)
    {
        using Process? process = Process.Start(CreateProcess(executable, arguments));
        if (process is null)
            throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{Path.GetFileName(executable)} timed out.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}.");
    }

    private static ProcessStartInfo CreateProcess(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    private static void TryMoveLegacyBundleToTrash(string legacyBundle)
    {
        try
        {
            string trash = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".Trash");
            Directory.CreateDirectory(trash);
            string name = Path.GetFileNameWithoutExtension(legacyBundle);
            string destination = Path.Combine(trash, $"{name}-pre-1.8.7-{DateTime.UtcNow:yyyyMMddHHmmss}.app");
            Directory.Move(legacyBundle, destination);
            DiagnosticsLogger.Record($"Moved the legacy macOS application to Trash: {destination}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLogger.Record($"The migrated app launched, but the legacy macOS bundle could not be moved to Trash: {ex.Message}");
        }
    }

    private static bool IsLinkedDirectory(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            DiagnosticsLogger.Record($"Could not remove incomplete macOS migration staging: {ex.Message}");
        }
    }
}
