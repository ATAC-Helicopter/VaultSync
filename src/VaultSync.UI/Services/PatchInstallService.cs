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
        private const string RestartArg = "--restart";
        private const string WaitPidArg = "--waitpid=";

        public static bool TryHandlePatchArgs(string[] args)
        {
            if (!TryParsePatchArgs(args, out var request))
                return false;

            _ = ApplyPatch(request, null, CancellationToken.None);
            return true;
        }

        public static bool TryParsePatchArgs(string[] args, out PatchApplyRequest? request)
        {
            request = null;

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

            request = new PatchApplyRequest(archivePath, manifestPath, installDir, restart, waitPid);
            return true;
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
                        Quote(ApplyArg),
                        Quote(archivePath),
                        Quote(manifestPath),
                        Quote(installDir),
                        Quote(RestartArg),
                        Quote($"{WaitPidArg}{Process.GetCurrentProcess().Id}"));
                }
                else
                {
                    psi.ArgumentList.Add(ApplyArg);
                    psi.ArgumentList.Add(archivePath);
                    psi.ArgumentList.Add(manifestPath);
                    psi.ArgumentList.Add(installDir);
                    psi.ArgumentList.Add(RestartArg);
                    psi.ArgumentList.Add($"{WaitPidArg}{Process.GetCurrentProcess().Id}");
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
                var candidate = Path.Combine(stagingDir, file.RelativePath);
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
                var source = Path.Combine(stagingDir, file.RelativePath);
                var target = Path.Combine(installDir, file.RelativePath);
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
    }
}
