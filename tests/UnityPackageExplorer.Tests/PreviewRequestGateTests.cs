using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class PreviewRequestGateTests
{
    [Fact]
    public void RejectsCompletionFromAnOlderSelection()
    {
        var gate = new PreviewRequestGate();

        var firstSelection = gate.BeginRequest();
        var secondSelection = gate.BeginRequest();

        Assert.False(gate.IsCurrent(firstSelection));
        Assert.True(gate.IsCurrent(secondSelection));
    }
}
