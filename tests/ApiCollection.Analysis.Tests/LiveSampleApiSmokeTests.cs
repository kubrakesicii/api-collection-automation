using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Snapshots;

namespace ApiCollection.Analysis.Tests;

/// <summary>
/// The real <c>sample-api/</c> is free to change: endpoints may be added, removed or edited at any time. These checks therefore state only
/// what must hold for ANY sample API (never a count or a list). Exact expectations live in the frozen copy under <c>fixtures/SampleApi</c>.
/// </summary>
public sealed class LiveSampleApiSmokeTests
{
    private static string LiveSampleApi()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ApiCollection.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "sample-api", "SampleApi");
    }

    [Fact]
    public void Whatever_endpoints_the_live_sample_api_has_it_is_analysed_without_failures_and_with_unique_identities()
    {
        var result = new EndpointDiscoveryEngine().DiscoverFromDirectory(LiveSampleApi());

        Assert.Empty(result.Failures);
        Assert.NotEmpty(result.Endpoints);

        // A duplicate identity makes Create throw, so this also proves every endpoint can be told apart.
        var snapshot = AnalysisSnapshot.Create(result.Endpoints);
        Assert.Equal(result.Endpoints.Count, snapshot.Endpoints.Count);
    }
}
