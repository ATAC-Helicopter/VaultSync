using System;
using System.Collections.Generic;
using System.Linq;
using VaultSync.Core.Config;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed class DestinationQuotaPlanner
{
    public static IReadOnlyList<DestinationQuotaPlan> BuildPlans(
        IEnumerable<BackupDestination> destinations,
        IEnumerable<Backup> backups)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        ArgumentNullException.ThrowIfNull(backups);

        var backupList = backups.ToList();
        var plans = new List<DestinationQuotaPlan>();

        foreach (BackupDestination destination in destinations)
        {
            string destinationId = DestinationIdentityService.GetId(destination);
            string normalizedPath = DestinationIdentityService.NormalizeDestinationPath(destination.Path);
            var matchingBackups = backupList
                .Where(backup => string.Equals(
                    DestinationIdentityService.NormalizeDestinationPath(backup.DestinationPath),
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(backup => backup.CreatedUtc)
                .ThenByDescending(backup => backup.TotalBytes)
                .ThenBy(backup => backup.Id)
                .ToList();

            long storedBytes = matchingBackups.Sum(backup => backup.TotalBytes);
            long? softQuotaBytes = NormalizeQuotaBytes(destination.SoftQuotaBytes);
            int warningPercent = Math.Clamp(destination.QuotaWarningPercent, 50, 99);

            if (!softQuotaBytes.HasValue)
            {
                plans.Add(new DestinationQuotaPlan(
                    destinationId,
                    destination.Path ?? string.Empty,
                    storedBytes,
                    null,
                    warningPercent,
                    null,
                    false,
                    false,
                    0,
                    0,
                    false));
                continue;
            }

            long warningBytes = (long)Math.Floor(softQuotaBytes.Value * (warningPercent / 100d));
            if (warningBytes <= 0)
                warningBytes = softQuotaBytes.Value;

            bool exceedsWarning = storedBytes >= warningBytes;
            bool exceedsQuota = storedBytes > softQuotaBytes.Value;
            long reclaimTargetBytes = Math.Max(0, storedBytes - warningBytes);

            int candidateCount = 0;
            long candidateBytes = 0L;
            if (reclaimTargetBytes > 0)
            {
                foreach (Backup? candidate in matchingBackups.Where(backup => !backup.IsProtected))
                {
                    candidateCount++;
                    candidateBytes += candidate.TotalBytes;
                    if (candidateBytes >= reclaimTargetBytes)
                        break;
                }
            }

            bool canReachWarningThreshold = reclaimTargetBytes == 0 || candidateBytes >= reclaimTargetBytes;

            plans.Add(new DestinationQuotaPlan(
                destinationId,
                destination.Path ?? string.Empty,
                storedBytes,
                softQuotaBytes,
                warningPercent,
                warningBytes,
                exceedsWarning,
                exceedsQuota,
                reclaimTargetBytes,
                candidateCount,
                canReachWarningThreshold));
        }

        return plans;
    }

    private static long? NormalizeQuotaBytes(long? value) =>
        value.HasValue && value.Value > 0 ? value.Value : null;
}

public sealed record DestinationQuotaPlan(
    string DestinationId,
    string DestinationPath,
    long StoredBytes,
    long? SoftQuotaBytes,
    int WarningPercent,
    long? WarningBytes,
    bool ExceedsWarningThreshold,
    bool ExceedsQuota,
    long SuggestedReclaimBytes,
    int SuggestedCandidateCount,
    bool CanReachWarningThreshold);
