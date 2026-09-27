namespace UnityPackageExplorer.Core;

/// <summary>Prevents an older asynchronous preview request from replacing a newer selection.</summary>
public sealed class PreviewRequestGate
{
    private long _currentRequest;

    public long BeginRequest() => Interlocked.Increment(ref _currentRequest);

    public bool IsCurrent(long requestId) => Interlocked.Read(ref _currentRequest) == requestId;
}
