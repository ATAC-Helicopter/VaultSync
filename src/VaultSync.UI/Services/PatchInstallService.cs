using System;
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

namespace VaultSync.UI.Services
{
    public sealed class PatchApplyRequest
    {
        public PatchApplyRequest(string archivePath, string manifestPath, string installDir, bool restart, int? waitPid)
        {
            ArchivePath = archivePath;
            ManifestPath = manifestPath;
            InstallDir = installDir;
            Restart = restart;
            WaitPid = waitPid;
        }

        public string ArchivePath { get; }
        public string ManifestPath { get; }
        public string InstallDir { get; }
        public bool Restart { get; }
        public int? WaitPid { get; }
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
        private const string RequestHashArg = "--request-sha256=";
        private const string RestartArg = "--restart";
        private const string WaitPidArg = "--waitpid=";

        public static bool TryHandlePatchArgs(string[] args)
        {
            if (!TryParsePatchArgs(args, out var request, out _))
                return false;

            _ = ApplyPatch(request, null, CancellationToken.None);
            return true;
        }

        public static bool TryParsePatchArgs(string[] args, out PatchApplyRequest? request)
            => TryParsePatchArgs(args, out request, out _);

        private static bool TryParsePatchArgs(string[] args, out PatchApplyRequest? request, out string? expectedRequestHash)
        {
            request = null;
            expectedRequestHash = null;

            if (args.Length >= 2 && string.Equals(args[0], ApplyRequestArg, StringComparison.OrdinalIgnoreCase))
            {
                var requestPath = args[1];
                if (!File.Exists(requestPath))
                    return false;

                var requestHashArg = args.FirstOrDefault(a => a.StartsWith(RequestHashArg, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(requestHashArg))
                    return false;

                expectedRequestHash = requestHashArg.Substring(RequestHashArg.Length).Trim();
                if (expectedRequestHash.Length != 64)
                    return false;

                if (!IsUnderTrustedPatchTempRoot(requestPath))
                    return false;

                try
                {
                    var requestBytes = File.ReadAllBytes(requestPath);
                    var actualHash = ComputeSha256(requestBytes);
                    if (!actualHash.Equals(expectedRequestHash, StringComparison.OrdinalIgnoreCase))
                        return false;

                    var parsed = JsonSerializer.Deserialize<PatchApplyRequest>(requestBytes);
                    if (!TryNormalizeRequest(parsed, out request, out _))
                        return false;

                    if (!IsUnderTrustedPatchTempRoot(request.ManifestPath))
                        return false;

                    return true;
                }
                catch
                {
                    return false;
                }
            }

            if (args.Length < 4 || !string.Equals(args[0], ApplyArg, StringComparison.OrdinalIgnoreCase))
                return false;

            var archivePath = args[1];
            var manifestPath = args[2];
            var installDir = args[3];
            var restart = args.Any(a => string.Equals(a, RestartArg, StringComparison.OrdinalIgnoreCase));
            var waitArg = args.FirstOrDefault(a => a.StartsWith(WaitPidArg, StringComparison.OrdinalIgnoreCase));
            int? waitPid = null;

            if (!string.IsNullOrWhiteSpace(waitArg))
            {
                var raw = waitArg.Substring(WaitPidArg.Length);
                if (int.TryParse(raw, out var parsed))
                {
                    waitPid = parsed;
                }
            }

            return TryNormalizeRequest(
                new PatchApplyRequest(archivePath, manifestPath, installDir, restart, waitPid),
                out request,
                out _);
        }

        public static Task<PatchApplyResult> ApplyPatchAsync(
            PatchApplyRequest request,
            Action<string>? onLog,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => ApplyPatch(request, onLog, cancellationToken), cancellationToken);
        }

        public static bool TryLaunchPatchInstaller(PatchPlan plan, string archivePath, out string? error)
        {
            try
            {
                var processPath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
                {
                    error = "Cannot locate current executable.";
                    return false;
                }

                var installDir = AppContext.BaseDirectory;
                var helperDir = PrepareHelperDirectory(installDir);
                var helperExe = Path.Combine(helperDir, Path.GetFileName(processPath));
                if (!File.Exists(helperExe))
                {
                    File.Copy(processPath, helperExe, overwrite: true);
                }

                var manifestPath = Path.Combine(helperDir, $"{Path.GetFileNameWithoutExtension(archivePath)}.manifest.json");
                var manifestJson = JsonSerializer.Serialize(plan.Manifest);
                File.WriteAllText(manifestPath, manifestJson, Encoding.UTF8);
                var requestPath = Path.Combine(helperDir, $"{Path.GetFileNameWithoutExtension(archivePath)}.apply-request.json");
                var request = new PatchApplyRequest(
                    archivePath,
                    manifestPath,
                    installDir,
                    restart: true,
                    waitPid: Process.GetCurrentProcess().Id);
                var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request);
                File.WriteAllBytes(requestPath, requestBytes);
                var requestHash = ComputeSha256(requestBytes);

                var needsElevation = NeedsElevation(installDir);

                // When elevation is needed (Program Files installs), we must use the shell + arguments string.
                var psi = new ProcessStartInfo
                {
                    FileName = helperExe,
                    WorkingDirectory = helperDir,
                    UseShellExecute = needsElevation,
                    Verb = needsElevation ? "runas" : string.Empty
                };

                if (needsElevation)
                {
                    psi.Arguments = string.Join(" ",
                        Quote(ApplyRequestArg),
                        Quote(requestPath),
                        Quote(RequestHashArg + requestHash));
                }
                else
                {
                    psi.ArgumentList.Add(ApplyRequestArg);
                    psi.ArgumentList.Add(requestPath);
                    psi.ArgumentList.Add(RequestHashArg + requestHash);
                }

                var started = Process.Start(psi);
                if (started is null)
                {
                    error = "Failed to start patch helper.";
                    return false;
                }

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string PrepareHelperDirectory(string installDir)
        {
            var root = Path.Combine(Path.GetTempPath(), "VaultSync");
            Directory.CreateDirectory(root);

            var helperDir = Path.Combine(root, "patch-helper");
            try
            {
                if (Directory.Exists(helperDir))
                {
                    Directory.Delete(helperDir, recursive: true);
                }
            }
            catch
            {
                helperDir = Path.Combine(root, $"patch-helper-{Guid.NewGuid():N}");
            }

            Directory.CreateDirectory(helperDir);
            CopyInstallToHelper(installDir, helperDir);
            return helperDir;
        }

        private static void CopyInstallToHelper(string installDir, string helperDir)
        {
            foreach (var dir in Directory.GetDirectories(installDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(installDir, dir);
                var targetDir = Path.Combine(helperDir, relative);
                Directory.CreateDirectory(targetDir);
            }

            foreach (var file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(installDir, file);
                var destination = Path.Combine(helperDir, relative);
                var destinationDir = Path.GetDirectoryName(destination);
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
            var logDir = Path.Combine(Path.GetTempPath(), "VaultSync");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "patch-helper.log");

            using var log = new StreamWriter(logPath, append: true) { AutoFlush = true };
            void LogLine(string message)
            {
                var line = $"[{DateTime.UtcNow:O}] {message}";
                log.WriteLine(line);
                onLog?.Invoke(line);
            }

            LogLine($"Starting patch apply. Archive={request.ArchivePath}, InstallDir={request.InstallDir}, Restart={request.Restart}");

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryNormalizeRequest(request, out var normalizedRequest, out var normalizeError))
                    throw new InvalidOperationException($"Invalid patch apply request: {normalizeError}");

                request = normalizedRequest!;

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
                var manifest = JsonSerializer.Deserialize<PatchManifest>(File.ReadAllText(request.ManifestPath));
                if (manifest is null)
                    throw new InvalidOperationException("Unable to parse patch manifest.");
                VerifyBaseVersionCompatibility(manifest);
                VerifyArchivePreflight(request.ArchivePath, manifest);

                var stagingDir = Path.Combine(Path.GetTempPath(), "VaultSync", $"patch-{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingDir);

                try
                {
                    LogLine("Extracting patch archive.");
                    ZipFile.ExtractToDirectory(request.ArchivePath, stagingDir);
                    LogLine("Verifying extracted files.");
                    VerifyExtractedFiles(manifest, stagingDir);
                    LogLine("Copying updated files.");
                    CopyIntoInstall(manifest, stagingDir, request.InstallDir, LogLine);
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
            foreach (var file in manifest.Files)
            {
                var candidate = CombineUnderRoot(stagingDir, file.RelativePath, "manifest file path");
                if (!File.Exists(candidate))
                    throw new FileNotFoundException($"Patched file missing: {file.RelativePath}", candidate);

                if (file.Size > 0)
                {
                    var size = new FileInfo(candidate).Length;
                    if (size != file.Size)
                        throw new InvalidOperationException($"File size mismatch for {file.RelativePath}. Expected {file.Size}, got {size}.");
                }

                if (!string.IsNullOrWhiteSpace(file.Sha256))
                {
                    var hash = ComputeSha256(candidate);
                    if (!hash.Equals(file.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Checksum mismatch for {file.RelativePath}.");
                }
            }
        }

        private static void CopyIntoInstall(PatchManifest manifest, string stagingDir, string installDir, Action<string> logLine)
        {
            foreach (var file in manifest.Files)
            {
                var source = CombineUnderRoot(stagingDir, file.RelativePath, "manifest source path");
                var target = CombineUnderRoot(installDir, file.RelativePath, "manifest target path");
                var targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(source, target, overwrite: true);
                logLine($"Replaced {file.RelativePath}");
            }
        }

        private static void RestartUpdatedApp(string installDir, Action<string> logLine)
        {
            var exe = Path.Combine(installDir, "VaultSync.UI.exe");
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
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(stream);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string ComputeSha256(byte[] payload)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(payload);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void VerifyArchivePreflight(string archivePath, PatchManifest manifest)
        {
            var info = new FileInfo(archivePath);
            if (!info.Exists)
                throw new FileNotFoundException("Patch archive not found.", archivePath);

            if (manifest.ArchiveSize > 0 && info.Length != manifest.ArchiveSize)
            {
                throw new InvalidOperationException(
                    $"Patch archive size mismatch. Expected {manifest.ArchiveSize}, got {info.Length}.");
            }

            if (!string.IsNullOrWhiteSpace(manifest.ArchiveSha256))
            {
                var actual = ComputeSha256(archivePath);
                var expected = manifest.ArchiveSha256.Trim();
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Patch archive checksum mismatch.");
            }
        }

        private static void VerifyBaseVersionCompatibility(PatchManifest manifest)
        {
            var currentVersion = GetCurrentVersionString();
            if (!PatchUpdateService.TryValidateAllowedBaseVersions(
                    manifest,
                    currentVersion,
                    out _,
                    out _,
                    out var statusCode,
                    out var message))
            {
                throw new InvalidOperationException($"Patch manifest rejected for helper apply ({statusCode}): {message}");
            }
        }

        private static string GetCurrentVersionString()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informationalVersion = assembly
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
                var normalizedPath = Path.GetFullPath(path);
                var root = Path.Combine(Path.GetTempPath(), "VaultSync");
                var normalizedRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool NeedsElevation(string installDir)
        {
            if (!OperatingSystem.IsWindows())
                return false;

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var underProgramFiles = (!string.IsNullOrWhiteSpace(programFiles) &&
                                     installDir.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase)) ||
                                    (!string.IsNullOrWhiteSpace(programFilesX86) &&
                                     installDir.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase));

            if (!underProgramFiles)
                return false;

            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return !principal.IsInRole(WindowsBuiltInRole.Administrator);
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
            out PatchApplyRequest? normalized,
            out string? error)
        {
            normalized = null;
            error = null;

            if (request is null)
            {
                error = "Request payload is empty.";
                return false;
            }

            if (!TryNormalizeFilePath(request.ArchivePath, out var archivePath, out error))
                return false;

            if (!TryNormalizeFilePath(request.ManifestPath, out var manifestPath, out error))
                return false;

            if (!TryNormalizeDirectoryPath(request.InstallDir, out var installDir, out error))
                return false;

            if (request.WaitPid is <= 0)
            {
                error = "Invalid wait PID.";
                return false;
            }

            normalized = new PatchApplyRequest(
                archivePath!,
                manifestPath!,
                installDir!,
                request.Restart,
                request.WaitPid);
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

            var trimmed = path.Trim();
            try
            {
                var fullPath = Path.GetFullPath(trimmed);
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

            var trimmed = path.Trim();
            try
            {
                var fullPath = Path.GetFullPath(trimmed);
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

            var sanitizedRelative = relativePath.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);

            if (Path.IsPathFullyQualified(sanitizedRelative))
                throw new InvalidOperationException($"Invalid {context}: absolute paths are not allowed ({relativePath}).");

            var candidate = Path.GetFullPath(Path.Combine(root, sanitizedRelative));
            var normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Invalid {context}: path traversal detected ({relativePath}).");

            return candidate;
        }
    }
}
