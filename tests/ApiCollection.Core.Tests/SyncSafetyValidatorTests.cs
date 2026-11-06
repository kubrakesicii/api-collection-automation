using System.Globalization;
using ApiCollection.Core.Diff;
using ApiCollection.Core.Sync;

namespace ApiCollection.Core.Tests;

public sealed class SyncSafetyValidatorTests
{
    /// <summary>A change set with the given numbers of endpoints per status (identities are synthetic but unique and ordered).</summary>
    private static ChangeSet Changes(int added = 0, int modified = 0, int removed = 0, int unchanged = 0)
    {
        var endpoints = new List<EndpointDiff>();
        void Add(ChangeStatus status, int count, string prefix)
        {
            for (var i = 0; i < count; i++)
            {
                endpoints.Add(new EndpointDiff
                {
                    Id = $"GET /api/{prefix}{i:00}",
                    Status = status,
                    HttpMethod = "GET",
                    Route = $"api/{prefix}{i:00}",
                    ControllerName = prefix,
                    ActionName = $"Action{i}"
                });
            }
        }

        Add(ChangeStatus.Added, added, "a");
        Add(ChangeStatus.Modified, modified, "m");
        Add(ChangeStatus.Removed, removed, "r");
        Add(ChangeStatus.Unchanged, unchanged, "u");
        return new ChangeSet
        {
            Summary = new ChangeSetSummary(added, modified, removed, unchanged),
            Endpoints = endpoints.OrderBy(e => e.Id, StringComparer.Ordinal).ToList()
        };
    }

    private static SyncDecision Validate(
        ChangeSet changes, SyncSafetyOptions? options = null, BaselineCollectionState baseline = BaselineCollectionState.Verified,
        string[]? failures = null, string[]? warnings = null) =>
        SyncSafetyValidator.Validate(
            new SyncSafetyInput
            {
                Changes = changes,
                BaselineCollection = baseline,
                DiscoveryFailures = failures ?? [],
                DiscoveryWarnings = warnings ?? []
            },
            options);

    // ---- outcomes ----

    [Fact]
    public void Nothing_changed_means_nothing_to_write()
    {
        var decision = Validate(Changes(unchanged: 5));

        Assert.Equal(SyncOutcome.NoChanges, decision.Outcome);
        Assert.Empty(decision.Violations);
        Assert.Empty(decision.Plan);
    }

    [Fact]
    public void A_safe_change_is_allowed_and_planned_in_identity_order()
    {
        var decision = Validate(Changes(added: 1, modified: 1, removed: 1, unchanged: 7));

        Assert.Equal(SyncOutcome.Allowed, decision.Outcome);
        Assert.Empty(decision.Violations);
        Assert.Equal(
            [(SyncAction.Create, "GET /api/a00"), (SyncAction.Update, "GET /api/m00"), (SyncAction.Delete, "GET /api/r00")],
            decision.Plan.Select(p => (p.Action, p.Id)));
        Assert.Equal(new ChangeSetSummary(1, 1, 1, 7), decision.Summary);
        Assert.Equal(new SyncSafetyOptions(), decision.Limits);
    }

    [Fact]
    public void The_first_sync_has_no_baseline_so_nothing_can_be_deleted_and_everything_is_created()
    {
        var decision = Validate(Changes(added: 20), baseline: BaselineCollectionState.NotApplicable);

        Assert.Equal(SyncOutcome.Allowed, decision.Outcome);
        Assert.All(decision.Plan, p => Assert.Equal(SyncAction.Create, p.Action));
    }

    [Fact]
    public void The_first_sync_of_an_api_without_endpoints_has_nothing_to_do()
    {
        Assert.Equal(SyncOutcome.NoChanges, Validate(Changes(), baseline: BaselineCollectionState.NotApplicable).Outcome);
    }

    // ---- fail closed on discovery problems ----

    [Fact]
    public void Discovering_nothing_while_the_baseline_has_endpoints_is_a_failed_analysis_that_no_option_can_override()
    {
        var everythingGoneIsAllowedByLimits = new SyncSafetyOptions { MaxRemovedPercent = 100, MaxRemovedCount = 1000, AllowIncompleteDiscovery = true };

        var decision = Validate(Changes(removed: 5), everythingGoneIsAllowedByLimits);

        Assert.Equal(SyncOutcome.Blocked, decision.Outcome);
        var violation = Assert.Single(decision.Violations);
        Assert.Equal((SyncViolationCode.DiscoveryFailed, false), (violation.Code, violation.CanOverride));
        Assert.Contains("baseline has 5", violation.Message);
    }

    [Fact]
    public void Discovery_failures_block_until_accepted_explicitly_and_stay_visible_afterwards()
    {
        string[] failures = ["Orders.GetAll: route template is not a string literal and was skipped."];
        var changes = Changes(modified: 1, unchanged: 9);

        var blocked = Validate(changes, failures: failures);
        Assert.Equal(SyncOutcome.Blocked, blocked.Outcome);
        var violation = Assert.Single(blocked.Violations);
        Assert.Equal((SyncViolationCode.DiscoveryIncomplete, true), (violation.Code, violation.CanOverride));
        Assert.Contains("Orders.GetAll", violation.Message);

        var accepted = Validate(changes, new SyncSafetyOptions { AllowIncompleteDiscovery = true }, failures: failures);
        Assert.Equal(SyncOutcome.Allowed, accepted.Outcome);
        Assert.Empty(accepted.Violations);
        Assert.Contains(accepted.Notes, n => n.Contains("accepted explicitly") && n.Contains("Orders.GetAll"));
    }

    [Fact]
    public void Several_failures_are_summarised_with_a_count()
    {
        var decision = Validate(Changes(unchanged: 3), failures: ["first problem", "second problem", "third problem"]);

        var message = Assert.Single(decision.Violations).Message;
        Assert.Contains("3 discovery failure(s)", message);
        Assert.Contains("first problem (and 2 more)", message);
    }

    [Fact]
    public void Failures_block_even_when_nothing_would_change()
    {
        var decision = Validate(Changes(unchanged: 3), failures: ["a route could not be resolved"]);

        Assert.Equal(SyncOutcome.Blocked, decision.Outcome);
        Assert.Empty(decision.Plan);
        Assert.Equal(SyncViolationCode.DiscoveryIncomplete, Assert.Single(decision.Violations).Code);
    }

    [Fact]
    public void Warnings_that_only_reduce_detail_are_noted_but_do_not_block()
    {
        var decision = Validate(Changes(modified: 1, unchanged: 4), warnings: ["type 'X' was not found", "[FromForm] is not supported"]);

        Assert.Equal(SyncOutcome.Allowed, decision.Outcome);
        Assert.Empty(decision.Violations);
        Assert.Contains(decision.Notes, n => n.StartsWith("2 discovery warning(s) only make endpoint details less complete"));
    }

    // ---- the stored collection must match the baseline ----

    [Theory]
    [InlineData(BaselineCollectionState.Changed, SyncViolationCode.BaselineCollectionChanged)]
    [InlineData(BaselineCollectionState.Missing, SyncViolationCode.BaselineCollectionMissing)]
    [InlineData(BaselineCollectionState.NoReference, SyncViolationCode.BaselineUnverifiable)]
    [InlineData(BaselineCollectionState.NameMismatch, SyncViolationCode.BaselineUnverifiable)]
    public void A_baseline_that_cannot_be_tied_to_the_stored_collection_blocks_and_no_option_overrides_it(
        BaselineCollectionState state, SyncViolationCode code)
    {
        var everythingAllowed = new SyncSafetyOptions { MaxRemovedPercent = 100, MaxRemovedCount = 1000, AllowIncompleteDiscovery = true };

        var decision = Validate(Changes(modified: 1, unchanged: 4), everythingAllowed, state);

        Assert.Equal(SyncOutcome.Blocked, decision.Outcome);
        var violation = Assert.Single(decision.Violations);
        Assert.Equal((code, false), (violation.Code, violation.CanOverride));
    }

    [Theory]
    [InlineData(BaselineCollectionState.Verified)]
    [InlineData(BaselineCollectionState.NotApplicable)]
    public void A_verified_or_absent_baseline_does_not_block(BaselineCollectionState state) =>
        Assert.Equal(SyncOutcome.Allowed, Validate(Changes(modified: 1, unchanged: 4), baseline: state).Outcome);

    [Fact]
    public void An_edited_collection_blocks_even_when_there_is_nothing_to_sync()
    {
        var decision = Validate(Changes(unchanged: 5), baseline: BaselineCollectionState.Changed);

        Assert.Equal(SyncOutcome.Blocked, decision.Outcome);
    }

    // ---- mass deletion ----

    [Theory]
    [InlineData(3, 7, true)]    // 3 of 10 = 30%: not more than the limit
    [InlineData(4, 6, false)]   // 40%
    [InlineData(1, 9, true)]
    [InlineData(10, 0, false)]  // everything (also fails the discovery check)
    public void Deleting_more_than_the_percentage_limit_is_blocked(int removed, int unchanged, bool allowed) =>
        Assert.Equal(allowed, Validate(Changes(removed: removed, unchanged: unchanged)).Outcome == SyncOutcome.Allowed);

    [Theory]
    [InlineData(10, true)]   // 10 of 100 = 10%, exactly the count limit
    [InlineData(11, false)]  // 11 of 100 = 11%, over the count limit although far below 30%
    public void Deleting_more_than_the_absolute_limit_is_blocked_even_when_the_share_is_small(int removed, bool allowed) =>
        Assert.Equal(allowed, Validate(Changes(removed: removed, unchanged: 100 - removed)).Outcome == SyncOutcome.Allowed);

    [Fact]
    public void The_deletion_limits_can_be_raised_or_tightened_explicitly()
    {
        var changes = Changes(removed: 6, unchanged: 4);

        Assert.Equal(SyncOutcome.Blocked, Validate(changes).Outcome);
        Assert.Equal(SyncOutcome.Allowed, Validate(changes, new SyncSafetyOptions { MaxRemovedPercent = 60 }).Outcome);
        Assert.Equal(SyncOutcome.Blocked, Validate(Changes(removed: 1, unchanged: 9), new SyncSafetyOptions { MaxRemovedCount = 0 }).Outcome);
        Assert.Equal(SyncOutcome.Blocked, Validate(Changes(removed: 1, unchanged: 9), new SyncSafetyOptions { MaxRemovedPercent = 0 }).Outcome);

        var violation = Assert.Single(Validate(changes).Violations);
        Assert.Equal((SyncViolationCode.MassDeletion, true), (violation.Code, violation.CanOverride));
    }

    [Fact]
    public void The_deletion_message_names_the_numbers_and_limits_independent_of_the_machine_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            var message = Assert.Single(Validate(Changes(removed: 4, unchanged: 6)).Violations).Message;

            Assert.Contains("4 of 10 endpoint(s) (40%)", message);
            Assert.Contains("10 endpoint(s) and 30%", message);
            Assert.Contains("--max-removed", message);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Deleting_nothing_never_trips_the_deletion_limits()
    {
        var strictest = new SyncSafetyOptions { MaxRemovedCount = 0, MaxRemovedPercent = 0 };

        Assert.Equal(SyncOutcome.Allowed, Validate(Changes(added: 3, modified: 3, unchanged: 3), strictest).Outcome);
    }

    // ---- combinations ----

    [Fact]
    public void Every_problem_is_reported_not_just_the_first()
    {
        var decision = Validate(Changes(removed: 5), baseline: BaselineCollectionState.Changed, failures: ["broken route"]);

        Assert.Equal(SyncOutcome.Blocked, decision.Outcome);
        Assert.Equal(
            [SyncViolationCode.BaselineCollectionChanged, SyncViolationCode.DiscoveryFailed, SyncViolationCode.DiscoveryIncomplete, SyncViolationCode.MassDeletion],
            decision.Violations.Select(v => v.Code));
    }

    [Fact]
    public void The_decision_is_deterministic()
    {
        var first = Validate(Changes(added: 2, modified: 2, removed: 1, unchanged: 5), warnings: ["w"]);
        var second = Validate(Changes(added: 2, modified: 2, removed: 1, unchanged: 5), warnings: ["w"]);

        Assert.Equal(first.Plan, second.Plan);
        Assert.Equal(first.Notes, second.Notes);
        Assert.Equal(first.Outcome, second.Outcome);
    }
}
