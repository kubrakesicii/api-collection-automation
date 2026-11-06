namespace ApiCollection.Analysis.Discovery;

/// <summary>
/// Collects what went wrong during discovery. A <b>failure</b> may hide endpoints (an unresolved route, a source file that does
/// not parse), so the result cannot be trusted to be complete; a <b>warning</b> only means an endpoint's details are less complete.
/// Failures are also listed as warnings so existing consumers keep seeing them.
/// </summary>
internal sealed class DiscoveryLog
{
    public List<string> Warnings { get; } = [];

    public List<string> Failures { get; } = [];

    public void Fail(string message)
    {
        Failures.Add(message);
        Warnings.Add(message);
    }
}
