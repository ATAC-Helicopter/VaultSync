using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.Services
{
    public sealed class PatchApplyRequest
    {
        public PatchApplyRequest(
            string archivePath,
            string manifestPath,
            string installDir,
            bool restart,
            int? waitPid,
            string? handoffPath = null)
        {
            ArchivePath = archivePath;
            ManifestPath = manifestPath;
            InstallDir = installDir;
            Restart = restart;
            WaitPid = waitPid;
            HandoffPath = handoffPath;
        }

        public string ArchivePath { get; }
        public string ManifestPath { get; }
        public string InstallDir { get; }
        public bool Restart { get; }
        public int? WaitPid { get; }
        public string? HandoffPath { get; }
    }

    public sealed class PatchApplyResult
    {
        public PatchApplyResult(bool success, string? errorMessage, string logPath)
        {
            Success = success;
            ErrorMessage = errorMessage;
            LogPath = logPath;
        }

        public bool Success { get; }
        public string? ErrorMessage { get; }
        public string LogPath { get; }
    }

    /// <summary>
    /// Applies a downloaded patch archive to the current install.
    /// Can run as a helper process when invoked with --apply-patch.
    /// </summary>
    internal static class PatchInstallService
    {
        private const string ApplyArg = "--apply-patch";
        private const string ApplyRequestArg = "--apply-patch-request";
        private const string HeadlessArg = "--headless-patch";
        private const string RequestHashArg = "--request-sha256=";
        private const string RestartArg = "--restart";
        private const string TempRootName = "VaultSync";
        private const string WaitPidArg = "--waitpid=";

        private enum PatchElevationKind
        {
            None,
            WindowsRunAs,
            LinuxPkexec
        }

        public static bool TryHandlePatchArgs(string[] args)
        {
            if (!TryParsePatchArgs(args, out PatchApplyRequest? request, out _))
                return false;

            PatchApplyResult result = ApplyPatch(request, null, CancellationToken.None);
            Environment.ExitCode = result.Success ? 0 : 1;
            return true;
        }

        public static bool IsHeadlessPatchInvocation(string[] args)
            => args.Any(a => string.Equals(a, HeadlessArg, StringComparison.OrdinalIgnoreCase));

        public static bool TryParsePatchArgs(string[] args, [NotNullWhen(true)] out PatchApplyRequest? request)
            => TryParsePatchArgs(args, out request, out _);

        private static bool TryParsePatchArgs(
            string[] args,
            [NotNullWhen(true)] out PatchApplyRequest? request,
            out string? expectedRequestHash)
        {
            request = null;
            expectedRequestHash = null;

            if (args.Length >= 2 && string.Equals(args[0], ApplyRequestArg, StringComparison.OrdinalIgnoreCase))
                return TryParseHashedPatchRequest(args, out request, out expectedRequestHash);

            if (args.Length < 4 || !string.Equals(args[0], ApplyArg, StringComparison.OrdinalIgnoreCase))
                return false;

            string archivePath = args[1];
            string manifestPath = args[2];
            string installDir = args[3];
            bool restart = args.Any(a => string.Equals(a, RestartArg, StringComparison.OrdinalIgnoreCase));
            string? waitArg = args.FirstOrDefault(a => a.StartsWith(WaitPidArg, StringComparison.OrdinalIgnoreCase));
            int? waitPid = null;

            if (!string.IsNullOrWhiteSpace(waitArg))
            {
                string raw = waitArg.Substring(WaitPidArg.Length);
                if (int.TryParse(raw, out int parsed))
                {
                    waitPid = parsed;
                }
            }

            return TryNormalizeRequest(
                new PatchApplyRequest(archivePath, manifestPath, installDir, restart, waitPid),
                out request,
                out _);
        }

        private static bool TryParseHashedPatchRequest(
            string[] args,
            [NotNullWhen(true)] out PatchApplyRequest? request,
            out string? expectedRequestHash)
        {
            request = null;
            expectedRequestHash = null;
            string requestPath = args[1];
            if (!File.Exists(requestPath) || !IsUnderTrustedPatchTempRoot(requestPath))
                return false;

            string? requestHashArg = args.FirstOrDefault(a => a.StartsWith(RequestHashArg, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(requestHashArg))
                return false;

            expectedRequestHash = requestHashArg[RequestHashArg.Length..].Trim();
            if (expectedRequestHash.Length != 64)
                return false;

            try
            {
                byte[] requestBytes = File.ReadAllBytes(requestPath);
                if (!ComputeSha256(requestBytes).Equals(expectedRequestHash, StringComparison.OrdinalIgnoreCase))
                    return false;

                PatchApplyRequest? parsed = JsonSerializer.Deserialize<PatchApplyRequest>(requestBytes);
                if (!TryNormalizeRequest(parsed, out PatchApplyRequest? normalized, out _))
                    return false;

                request = normalized;
                return IsUnderTrustedPatchTempRoot(normalized.ManifestPath);
            }
            catch
            {
                return false;
            }
        }

        public static Task<PatchApplyResult> ApplyPatchAsync(
            PatchApplyRequest request,
            Action<string>? onLog,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => ApplyPatch(request, onLog, cancellationToken), cancellationToken);
        }

        public static async Task<(bool Success, string? Error)> LaunchPatchInstallerAsync(
            PatchPlan plan,
            string archivePath,
            CancellationToken cancellationToken)
        {
            try
            {
                string? processPath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
                {
                    return (false, "Cannot locate current executable.");
                }

                string runtimeDir = AppContext.BaseDirectory;
                string installDir = ResolveInstallRoot(runtimeDir);
                string helperDir = PrepareHelperDirectory(runtimeDir);
                string helperExe = Path.Combine(helperDir, Path.GetFileName(processPath));
                if (!File.Exists(helperExe))
                {
                    File.Copy(processPath, helperExe, overwrite: true);
                }

                string archiveBaseName = Path.GetFileNameWithoutExtension(archivePath);
                string manifestPath = Path.Combine(helperDir, $"{archiveBaseName}.manifest.json");
                string manifestJson = JsonSerializer.Serialize(plan.Manifest);
                await File.WriteAllTextAsync(manifestPath, manifestJson, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                string requestPath = Path.Combine(helperDir, $"{archiveBaseName}.apply-request.json");
                PatchElevationKind elevationKind = GetElevationKind(installDir);
                string? handoffPath = elevationKind == PatchElevationKind.LinuxPkexec
                    ? Path.Combine(helperDir, $"handoff-{Guid.NewGuid():N}.ready")
                    : null;
                var request = new PatchApplyRequest(
                    archivePath,
                    manifestPath,
                    installDir,
                    restart: elevationKind != PatchElevationKind.LinuxPkexec,
                    waitPid: Environment.ProcessId,
                    handoffPath: handoffPath);
                byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(request);
                await File.WriteAllBytesAsync(requestPath, requestBytes, cancellationToken).ConfigureAwait(false);
                string requestHash = ComputeSha256(requestBytes);

                ProcessStartInfo psi = CreatePatchInstallerStartInfo(
                    elevationKind,
                    helperExe,
                    helperDir,
                    requestPath,
                    requestHash);

                var started = Process.Start(psi);
                if (started is null)
                {
                    return (false, "Failed to start patch helper.");
                }

                if (handoffPath is not null)
                {
                    bool authenticated = await WaitForLinuxPatchHandoffAsync(
                        started,
                        handoffPath,
                        cancellationToken).ConfigureAwait(false);
                    if (!authenticated)
                    {
                        int? exitCode = started.HasExited ? started.ExitCode : null;
                        return (false, exitCode.HasValue
                            ? $"Patch authorization was cancelled or failed with code {exitCode.Value}."
                            : "Patch authorization did not complete.");
                    }
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static ProcessStartInfo CreatePatchInstallerStartInfo(
            PatchElevationKind elevationKind,
            string helperExe,
            string helperDir,
            string requestPath,
            string requestHash)
        {
            // Windows elevation uses ShellExecute; Linux elevation runs the copied helper through pkexec.
            var startInfo = new ProcessStartInfo
            {
                FileName = elevationKind == PatchElevationKind.LinuxPkexec
                    ? FindExecutable("pkexec") ?? "pkexec"
                    : helperExe,
                WorkingDirectory = helperDir,
                UseShellExecute = elevationKind == PatchElevationKind.WindowsRunAs,
                Verb = elevationKind == PatchElevationKind.WindowsRunAs ? "runas" : string.Empty
            };

            if (elevationKind == PatchElevationKind.WindowsRunAs)
            {
                startInfo.Arguments = string.Join(" ",
                    Quote(ApplyRequestArg),
                    Quote(requestPath),
                    Quote(RequestHashArg + requestHash));
                return startInfo;
            }

            if (elevationKind == PatchElevationKind.LinuxPkexec)
            {
                EnsureUnixExecutable(helperExe);
                startInfo.ArgumentList.Add(helperExe);
            }

            startInfo.ArgumentList.Add(ApplyRequestArg);
            startInfo.ArgumentList.Add(requestPath);
            startInfo.ArgumentList.Add(RequestHashArg + requestHash);
            if (elevationKind == PatchElevationKind.LinuxPkexec)
                startInfo.ArgumentList.Add(HeadlessArg);
            return startInfo;
        }

        internal static async Task<bool> WaitForLinuxPatchHandoffAsync(
            Process process,
            string handoffPath,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(handoffPath))
                    return true;
                if (process.HasExited)
                    return false;

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        public static bool CanLaunchProtectedPatchInstall(string installDir)
        {
            if (string.IsNullOrWhiteSpace(installDir))
                return false;

            if (CanWriteInstallDir(installDir))
                return true;

            return GetElevationKind(installDir) != PatchElevationKind.None;
        }

        internal static string ResolveInstallRoot(string runtimeDirectory)
        {
            string normalized = Path.GetFullPath(runtimeDirectory);
            if (!OperatingSystem.IsMacOS())
                return normalized;

            var macOsDirectory = new DirectoryInfo(normalized.TrimEnd(Path.DirectorySeparatorChar));
            DirectoryInfo? contentsDirectory = macOsDirectory.Parent;
            DirectoryInfo? bundleDirectory = contentsDirectory?.Parent;
            if (!string.Equals(macOsDirectory.Name, "MacOS", StringComparison.Ordinal) ||
                !string.Equals(contentsDirectory?.Name, "Contents", StringComparison.Ordinal) ||
                bundleDirectory is null ||
                !bundleDirectory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            return bundleDirectory.FullName;
        }

        private static string PrepareHelperDirectory(string installDir)
        {
            string root = GetTrustedPatchRoot();
            string helperDir = Path.Combine(root, $"patch-helper-{Guid.NewGuid():N}");
            Directory.CreateDirectory(helperDir);
            RestrictDirectoryToCurrentUser(helperDir);
            CopyInstallToHelper(installDir, helperDir);
            return helperDir;
        }

        private static void CopyInstallToHelper(string installDir, string helperDir)
        {
            foreach (string dir in Directory.GetDirectories(installDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(installDir, dir);
                string targetDir = Path.Combine(helperDir, relative);
                Directory.CreateDirectory(targetDir);
            }

            foreach (string file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(installDir, file);
                string destination = Path.Combine(helperDir, relative);
                string? destinationDir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                File.Copy(file, destination, overwrite: true);
            }
        }

        private static PatchApplyResult ApplyPatch(
            PatchApplyRequest request,
            Action<string>? onLog,
            CancellationToken cancellationToken)
        {
            string logDir = GetTrustedPatchRoot();
            string logPath = Path.Combine(logDir, "patch-helper.log");

            using var log = new StreamWriter(logPath, append: true) { AutoFlush = true };
            void LogLine(string message)
            {
                string line = $"[{DateTime.UtcNow:O}] {message}";
                log.WriteLine(line);
                onLog?.Invoke(line);
            }

            LogLine($"Starting patch apply. Archive={request.ArchivePath}, InstallDir={request.InstallDir}, Restart={request.Restart}");

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryNormalizeRequest(request, out PatchApplyRequest? normalizedRequest, out string? normalizeError))
                    throw new InvalidOperationException($"Invalid patch apply request: {normalizeError}");

                request = normalizedRequest!;

                if (!string.IsNullOrWhiteSpace(request.HandoffPath))
                {
                    File.WriteAllText(request.HandoffPath, "ready", Encoding.ASCII);
                    LogLine("Patch helper authorization completed; parent shutdown is now safe.");
                }

                if (request.WaitPid is { } pid)
                {
                    try
                    {
                        var parent = Process.GetProcessById(pid);
                        if (!parent.HasExited)
                        {
                            LogLine($"Waiting for parent pid {pid} to exit...");
                            parent.WaitForExit(15000);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogLine($"Warning: failed waiting for pid {pid}: {ex.Message}");
                    }
                }

                if (!File.Exists(request.ArchivePath))
                    throw new FileNotFoundException("Patch archive not found.", request.ArchivePath);

                if (!File.Exists(request.ManifestPath))
                    throw new FileNotFoundException("Patch manifest not found.", request.ManifestPath);

                LogLine("Loading patch manifest.");
                PatchManifest? manifest = JsonSerializer.Deserialize<PatchManifest>(File.ReadAllText(request.ManifestPath));
                if (manifest is null)
                    throw new InvalidOperationException("Unable to parse patch manifest.");
                if (!PatchUpdateService.TryValidatePatchManifest(manifest, out string? manifestStatus, out string? manifestError))
                    throw new InvalidOperationException($"Patch manifest rejected ({manifestStatus}): {manifestError}");
                VerifyBaseVersionCompatibility(manifest);
                VerifyArchivePreflight(request.ArchivePath, manifest);

                string stagingDir = Path.Combine(GetTrustedPatchRoot(), $"patch-{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingDir);
                RestrictDirectoryToCurrentUser(stagingDir);

                try
                {
                    VerifyArchiveContents(manifest, request.ArchivePath);
                    LogLine("Extracting patch archive.");
                    SafeZipExtractor.ExtractToDirectory(request.ArchivePath, stagingDir);
                    LogLine("Verifying extracted files.");
                    VerifyExtractedFiles(manifest, stagingDir);
                    LogLine("Installing updated files with rollback protection.");
                    CopyIntoInstall(
                        manifest,
                        stagingDir,
                        request.InstallDir,
                        LogLine,
                        () => VerifyInstalledBundleIdentity(request.InstallDir, manifest.TargetVersion));
                }
                finally
                {
                    try
                    {
                        Directory.Delete(stagingDir, recursive: true);
                    }
                    catch
                    {
                        // best effort cleanup
                    }
                }

                LogLine("Patch applied successfully.");

                if (request.Restart)
                {
                    RestartUpdatedApp(request.InstallDir, LogLine);
                }

                return new PatchApplyResult(true, null, logPath);
            }
            catch (Exception ex)
            {
                LogLine($"Patch apply failed: {ex}");
                return new PatchApplyResult(false, ex.Message, logPath);
            }
        }

        private static void VerifyExtractedFiles(PatchManifest manifest, string stagingDir)
        {
            foreach (PatchFileEntry file in manifest.Files)
            {
                string candidate = CombineUnderRoot(stagingDir, file.RelativePath, "manifest file path");
                if (!File.Exists(candidate))
                    throw new FileNotFoundException($"Patched file missing: {file.RelativePath}", candidate);

                if (file.Size > 0)
                {
                    long size = new FileInfo(candidate).Length;
                    if (size != file.Size)
                        throw new InvalidOperationException($"File size mismatch for {file.RelativePath}. Expected {file.Size}, got {size}.");
                }

                if (!string.IsNullOrWhiteSpace(file.Sha256))
                {
                    string hash = ComputeSha256(candidate);
                    if (!hash.Equals(file.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Checksum mismatch for {file.RelativePath}.");
                }
            }
        }

        internal static void CopyIntoInstall(
            PatchManifest manifest,
            string stagingDir,
            string installDir,
            Action<string> logLine,
            Action? validateInstalled = null)
        {
            string transactionDir = Path.Combine(GetTrustedPatchRoot(), $"patch-rollback-{Guid.NewGuid():N}");
            string backupDir = Path.Combine(transactionDir, "backup");
            var operations = new List<PatchInstallOperation>();
            var createdDirectories = new List<string>();

            Directory.CreateDirectory(backupDir);
            RestrictDirectoryToCurrentUser(transactionDir);

            try
            {
                foreach (PatchFileEntry file in manifest.Files)
                {
                    string source = CombineUnderRoot(stagingDir, file.RelativePath, "manifest source path");
                    string target = CombineUnderRoot(installDir, file.RelativePath, "manifest target path");
                    SafeZipExtractor.EnsureNoLinkedPathComponents(stagingDir, source);
                    SafeZipExtractor.EnsureNoLinkedPathComponents(installDir, target);

                    bool targetExisted = File.Exists(target);
                    string? backup = null;
                    if (targetExisted)
                    {
                        backup = CombineUnderRoot(backupDir, file.RelativePath, "rollback backup path");
                        string? backupParent = Path.GetDirectoryName(backup);
                        if (!string.IsNullOrWhiteSpace(backupParent))
                            Directory.CreateDirectory(backupParent);

                        File.Copy(target, backup, overwrite: false);
                    }

                    operations.Add(new PatchInstallOperation(file.RelativePath, source, target, backup, targetExisted));
                }

                foreach (PatchInstallOperation operation in operations)
                {
                    string? targetDir = Path.GetDirectoryName(operation.Target);
                    if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
                    {
                        CreateDirectoryTree(targetDir, installDir, createdDirectories);
                    }

                    ReplaceFromSource(operation.Source, operation.Target);
                    operation.Applied = true;
                    logLine($"Replaced {operation.RelativePath}");
                }

                validateInstalled?.Invoke();
            }
            catch (Exception installError)
            {
                var rollbackErrors = RollBackInstall(operations, createdDirectories);
                if (rollbackErrors.Count > 0)
                {
                    rollbackErrors.Insert(0, installError);
                    throw new AggregateException("Patch installation failed and rollback was incomplete.", rollbackErrors);
                }

                throw;
            }
            finally
            {
                try
                {
                    Directory.Delete(transactionDir, recursive: true);
                }
                catch
                {
                    // Best effort cleanup. A failed cleanup only leaves rollback data in the trusted runtime directory.
                }
            }
        }

        internal static void VerifyInstalledBundleIdentity(string installDir, string targetVersion)
        {
            if (!OperatingSystem.IsMacOS() || !installDir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return;

            string plistPath = Path.Combine(installDir, "Contents", "Info.plist");
            if (!File.Exists(plistPath))
                throw new InvalidDataException("Updated macOS bundle is missing Contents/Info.plist.");

            XDocument plist = XDocument.Load(plistPath, LoadOptions.None);
            string shortVersion = ReadPlistString(plist, "CFBundleShortVersionString");
            string bundleVersion = ReadPlistString(plist, "CFBundleVersion");
            string executable = ReadPlistString(plist, "CFBundleExecutable");
            if (!string.Equals(shortVersion, targetVersion, StringComparison.Ordinal) ||
                !string.Equals(bundleVersion, targetVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Updated macOS bundle identity does not match target {targetVersion}.");
            }

            string executablePath = Path.Combine(installDir, "Contents", "MacOS", executable);
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executablePath))
                throw new InvalidDataException("Updated macOS bundle executable is missing.");
        }

        private static string ReadPlistString(XDocument plist, string key)
        {
            XElement? keyElement = plist.Descendants("key")
                .FirstOrDefault(element => string.Equals(element.Value, key, StringComparison.Ordinal));
            XElement? valueElement = keyElement?.ElementsAfterSelf().FirstOrDefault();
            return string.Equals(valueElement?.Name.LocalName, "string", StringComparison.Ordinal)
                ? valueElement?.Value ?? string.Empty
                : string.Empty;
        }

        private static void ReplaceFromSource(string source, string target)
        {
            string temporary = target + $".vaultsync-patch-{Guid.NewGuid():N}.tmp";
            try
            {
                File.Copy(source, temporary, overwrite: false);
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private static List<Exception> RollBackInstall(
            IReadOnlyList<PatchInstallOperation> operations,
            IReadOnlyList<string> createdDirectories)
        {
            var errors = new List<Exception>();
            foreach (PatchInstallOperation operation in operations.Reverse())
            {
                if (!operation.Applied)
                    continue;

                RollBackOperation(operation, errors);
            }

            foreach (string directory in createdDirectories.Reverse())
                RemoveCreatedDirectory(directory, errors);

            return errors;
        }

        private static void RollBackOperation(PatchInstallOperation operation, List<Exception> errors)
        {
            try
            {
                if (operation.TargetExisted)
                {
                    if (string.IsNullOrWhiteSpace(operation.Backup) || !File.Exists(operation.Backup))
                        throw new FileNotFoundException($"Rollback backup is missing for {operation.RelativePath}.", operation.Backup);

                    ReplaceFromSource(operation.Backup, operation.Target);
                }
                else if (File.Exists(operation.Target))
                {
                    File.Delete(operation.Target);
                }
            }
            catch (Exception ex)
            {
                errors.Add(new IOException($"Failed to roll back {operation.RelativePath}.", ex));
            }
        }

        private static void RemoveCreatedDirectory(string directory, List<Exception> errors)
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch (Exception ex)
            {
                errors.Add(new IOException($"Failed to remove patch-created directory {directory}.", ex));
            }
        }

        private static void CreateDirectoryTree(string directory, string installDir, List<string> createdDirectories)
        {
            var missing = new Stack<string>();
            string? current = directory;
            string normalizedRoot = Path.GetFullPath(installDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            while (!string.IsNullOrWhiteSpace(current) && !Directory.Exists(current))
            {
                if (string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    break;

                missing.Push(current);
                current = Path.GetDirectoryName(current);
            }

            while (missing.Count > 0)
            {
                string created = missing.Pop();
                Directory.CreateDirectory(created);
                createdDirectories.Add(created);
            }
        }

        private sealed class PatchInstallOperation
        {
            public PatchInstallOperation(
                string relativePath,
                string source,
                string target,
                string? backup,
                bool targetExisted)
            {
                RelativePath = relativePath;
                Source = source;
                Target = target;
                Backup = backup;
                TargetExisted = targetExisted;
            }

            public string RelativePath
            {
                get;
            }

            public string Source
            {
                get;
            }

            public string Target
            {
                get;
            }

            public string? Backup
            {
                get;
            }

            public bool TargetExisted
            {
                get;
            }

            public bool Applied
            {
                get;
                set;
            }
        }

        private static void RestartUpdatedApp(string installDir, Action<string> logLine)
        {
            if (OperatingSystem.IsMacOS() && installDir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    UseShellExecute = false,
                    ArgumentList = { "-n", installDir }
                });
                logLine($"Restarted application bundle {installDir}");
                return;
            }

            string exe = Path.Combine(installDir, "VaultSync.UI.exe");
            if (!File.Exists(exe))
            {
                exe = Path.Combine(installDir, "VaultSync.UI");
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = installDir,
                UseShellExecute = true
            };

            try
            {
                Process.Start(psi);
                logLine("Launched updated app.");
            }
            catch (Exception ex)
            {
                logLine($"Failed to relaunch app: {ex.Message}");
            }
        }

        private static string ComputeSha256(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(stream);
            return HashService.FormatSha256Lower(bytes);
        }

        private static string ComputeSha256(byte[] payload)
        {
            return HashService.FormatSha256Lower(SHA256.HashData(payload));
        }

        private static void VerifyArchivePreflight(string archivePath, PatchManifest manifest)
        {
            var info = new FileInfo(archivePath);
            if (!info.Exists)
                throw new FileNotFoundException("Patch archive not found.", archivePath);

            if (info.Length != manifest.ArchiveSize)
            {
                throw new InvalidOperationException(
                    $"Patch archive size mismatch. Expected {manifest.ArchiveSize}, got {info.Length}.");
            }

            string actual = ComputeSha256(archivePath);
            string expected = manifest.ArchiveSha256.Trim();
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Patch archive checksum mismatch.");
        }

        private static void VerifyArchiveContents(PatchManifest manifest, string archivePath)
        {
            var expected = manifest.Files.ToDictionary(
                file => SafeZipExtractor.GetSafeEntryRelativePath(file.RelativePath).Replace('\\', '/'),
                file => file,
                StringComparer.OrdinalIgnoreCase);
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                string relative = SafeZipExtractor.GetSafeEntryRelativePath(entry.FullName).Replace('\\', '/');
                if (!found.Add(relative))
                    throw new InvalidDataException($"Patch archive contains duplicate file '{relative}'.");
                if (!expected.TryGetValue(relative, out PatchFileEntry? file))
                    throw new InvalidDataException($"Patch archive contains unexpected file '{relative}'.");
                if (entry.Length != file.Size)
                    throw new InvalidDataException($"Patch archive file size mismatch for '{relative}'.");
            }

            if (found.Count != expected.Count)
            {
                string missing = expected.Keys.First(path => !found.Contains(path));
                throw new InvalidDataException($"Patch archive is missing manifest file '{missing}'.");
            }
        }

        private static void VerifyBaseVersionCompatibility(PatchManifest manifest)
        {
            string currentVersion = GetCurrentVersionString();
            if (!PatchUpdateService.TryValidateAllowedBaseVersions(
                    manifest,
                    currentVersion,
                    out _,
                    out _,
                    out string? statusCode,
                    out string? message))
            {
                throw new InvalidOperationException($"Patch manifest rejected for helper apply ({statusCode}): {message}");
            }
        }

        private static string GetCurrentVersionString()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string? informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informationalVersion))
                return informationalVersion.Trim();

            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        private static bool IsUnderTrustedPatchTempRoot(string path)
        {
            try
            {
                string normalizedPath = Path.GetFullPath(path);
                string root = GetTrustedPatchRoot();
                string normalizedRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string GetTrustedPatchRoot()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
                throw new InvalidOperationException("Current-user application data directory is unavailable.");

            string root = Path.Combine(appData, TempRootName, "patch-runtime");
            Directory.CreateDirectory(root);
            RestrictDirectoryToCurrentUser(root);
            return root;
        }

        private static void RestrictDirectoryToCurrentUser(string path)
        {
            if (OperatingSystem.IsWindows())
                return;

            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        private static PatchElevationKind GetElevationKind(string installDir)
        {
            if (CanWriteInstallDir(installDir))
                return PatchElevationKind.None;

            if (OperatingSystem.IsLinux())
                return string.IsNullOrWhiteSpace(FindExecutable("pkexec"))
                    ? PatchElevationKind.None
                    : PatchElevationKind.LinuxPkexec;

            if (!OperatingSystem.IsWindows())
                return PatchElevationKind.None;

            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator)
                ? PatchElevationKind.None
                : PatchElevationKind.WindowsRunAs;
        }

        private static bool CanWriteInstallDir(string installDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(installDir))
                    return false;

                Directory.CreateDirectory(installDir);
                string testPath = Path.Combine(installDir, $".vaultsync-write-test-{Guid.NewGuid():N}");
                File.WriteAllBytes(testPath, []);
                File.Delete(testPath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? FindExecutable(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string[] candidates =
            [
                Path.Combine("/usr/bin", name),
                Path.Combine("/bin", name),
                Path.Combine("/usr/local/bin", name),
                Path.Combine("/sbin", name),
                Path.Combine("/usr/sbin", name)
            ];

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
                return null;

            foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(entry, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }

            return null;
        }

        private static void EnsureUnixExecutable(string filePath)
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                return;

            try
            {
                File.SetUnixFileMode(
                    filePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch
            {
                // Best effort; Process.Start will report a launch failure if execution is still blocked.
            }
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "\"\"";

            if (value.Contains(' ') || value.Contains('\t'))
                return $"\"{value}\"";

            return value;
        }

        private static bool TryNormalizeRequest(
            PatchApplyRequest? request,
            [NotNullWhen(true)] out PatchApplyRequest? normalized,
            out string? error)
        {
            normalized = null;
            error = null;

            if (request is null)
            {
                error = "Request payload is empty.";
                return false;
            }

            if (!TryNormalizeFilePath(request.ArchivePath, out string? archivePath, out error))
                return false;

            if (!TryNormalizeFilePath(request.ManifestPath, out string? manifestPath, out error))
                return false;

            if (!TryNormalizeDirectoryPath(request.InstallDir, out string? installDir, out error))
                return false;

            if (request.WaitPid is <= 0)
            {
                error = "Invalid wait PID.";
                return false;
            }

            string? handoffPath = null;
            if (!string.IsNullOrWhiteSpace(request.HandoffPath) &&
                (!TryNormalizeFilePath(request.HandoffPath, out handoffPath, out error) ||
                 !IsUnderTrustedPatchTempRoot(handoffPath!)))
            {
                error ??= "Patch handoff path is outside the trusted patch directory.";
                return false;
            }

            normalized = new PatchApplyRequest(
                archivePath!,
                manifestPath!,
                installDir!,
                request.Restart,
                request.WaitPid,
                handoffPath);
            return true;
        }

        private static bool TryNormalizeFilePath(string? path, out string? normalized, out string? error)
        {
            normalized = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "File path is missing.";
                return false;
            }

            string trimmed = path.Trim();
            try
            {
                string fullPath = Path.GetFullPath(trimmed);
                if (!Path.IsPathFullyQualified(fullPath))
                {
                    error = "File path must be absolute.";
                    return false;
                }

                normalized = fullPath;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryNormalizeDirectoryPath(string? path, out string? normalized, out string? error)
        {
            normalized = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Install directory is missing.";
                return false;
            }

            string trimmed = path.Trim();
            try
            {
                string fullPath = Path.GetFullPath(trimmed);
                if (!Path.IsPathFullyQualified(fullPath))
                {
                    error = "Install directory must be absolute.";
                    return false;
                }

                normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string CombineUnderRoot(string root, string relativePath, string context)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new InvalidOperationException($"Invalid {context}: path is empty.");

            string sanitizedRelative = relativePath.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);

            if (Path.IsPathFullyQualified(sanitizedRelative))
                throw new InvalidOperationException($"Invalid {context}: absolute paths are not allowed ({relativePath}).");

            string candidate = Path.GetFullPath(Path.Combine(root, sanitizedRelative));
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Invalid {context}: path traversal detected ({relativePath}).");

            return candidate;
        }
    }
}
