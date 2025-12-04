using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace VaultSync.UI.Services
{
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

            ApplyPatch(archivePath, manifestPath, installDir, restart, waitPid);
            return true;
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

        private static void ApplyPatch(string archivePath, string manifestPath, string installDir, bool restart, int? waitPid)
        {
            var logDir = Path.Combine(Path.GetTempPath(), "VaultSync");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "patch-helper.log");

            using var log = new StreamWriter(logPath, append: true) { AutoFlush = true };
            log.WriteLine($"[{DateTime.UtcNow:O}] Starting patch apply. Archive={archivePath}, InstallDir={installDir}, Restart={restart}");

            try
            {
                if (waitPid is { } pid)
                {
                    try
                    {
                        var parent = Process.GetProcessById(pid);
                        if (!parent.HasExited)
                        {
                            log.WriteLine($"Waiting for parent pid {pid} to exit...");
                            parent.WaitForExit(15000);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.WriteLine($"Warning: failed waiting for pid {pid}: {ex.Message}");
                    }
                }

                if (!File.Exists(archivePath))
                    throw new FileNotFoundException("Patch archive not found.", archivePath);

                if (!File.Exists(manifestPath))
                    throw new FileNotFoundException("Patch manifest not found.", manifestPath);

                var manifest = JsonSerializer.Deserialize<PatchManifest>(File.ReadAllText(manifestPath));
                if (manifest is null)
                    throw new InvalidOperationException("Unable to parse patch manifest.");

                var stagingDir = Path.Combine(Path.GetTempPath(), "VaultSync", $"patch-{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingDir);

                try
                {
                    ZipFile.ExtractToDirectory(archivePath, stagingDir);
                    VerifyExtractedFiles(manifest, stagingDir);
                    CopyIntoInstall(manifest, stagingDir, installDir, log);
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

                log.WriteLine("Patch applied successfully.");

                if (restart)
                {
                    RestartUpdatedApp(installDir, log);
                }
            }
            catch (Exception ex)
            {
                log.WriteLine($"Patch apply failed: {ex}");
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

        private static void CopyIntoInstall(PatchManifest manifest, string stagingDir, string installDir, TextWriter log)
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
                log.WriteLine($"Replaced {file.RelativePath}");
            }
        }

        private static void RestartUpdatedApp(string installDir, TextWriter log)
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
                log.WriteLine("Launched updated app.");
            }
            catch (Exception ex)
            {
                log.WriteLine($"Failed to relaunch app: {ex.Message}");
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
