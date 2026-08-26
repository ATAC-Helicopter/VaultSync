using System;
using System.Collections.Generic;
using System.Threading;

namespace VaultSync.Core.Services;

internal sealed class BackupCancellationRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<int, CancellationTokenSource> _sources = [];

    public Registration Register(int projectId, CancellationToken callerToken)
    {
        var projectSource = new CancellationTokenSource();
        CancellationTokenSource? previousSource;

        lock (_gate)
        {
            _sources.TryGetValue(projectId, out previousSource);
            _sources[projectId] = projectSource;
        }

        CancelPrevious(previousSource);
        return new Registration(this, projectId, projectSource, callerToken);
    }

    public bool Cancel(int projectId)
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            _sources.TryGetValue(projectId, out source);
        }

        if (source is null)
            return false;

        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void Release(int projectId, CancellationTokenSource source)
    {
        lock (_gate)
        {
            if (_sources.TryGetValue(projectId, out CancellationTokenSource? current) &&
                ReferenceEquals(current, source))
            {
                _sources.Remove(projectId);
            }
        }

        source.Dispose();
    }

    private static void CancelPrevious(CancellationTokenSource? source)
    {
        if (source is null)
            return;

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A completing registration won the race and already released it.
        }
    }

    internal sealed class Registration : IDisposable
    {
        private readonly BackupCancellationRegistry _owner;
        private readonly int _projectId;
        private CancellationTokenSource? _projectSource;
        private CancellationTokenSource? _linkedSource;

        internal Registration(
            BackupCancellationRegistry owner,
            int projectId,
            CancellationTokenSource projectSource,
            CancellationToken callerToken)
        {
            _owner = owner;
            _projectId = projectId;
            _projectSource = projectSource;
            _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                callerToken,
                projectSource.Token);
        }

        public CancellationToken Token =>
            _linkedSource?.Token ?? throw new ObjectDisposedException(nameof(Registration));

        public void Dispose()
        {
            CancellationTokenSource? linkedSource = Interlocked.Exchange(ref _linkedSource, null);
            CancellationTokenSource? projectSource = Interlocked.Exchange(ref _projectSource, null);
            linkedSource?.Dispose();
            if (projectSource is not null)
                _owner.Release(_projectId, projectSource);
        }
    }
}
