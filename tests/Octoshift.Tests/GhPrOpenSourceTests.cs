namespace Octoshift.Tests;

using Octoshift.Commands;
using Octoshift.GitHub;
using Xunit;

/// <summary>
/// Outbound PR-create source behavior: argument formatting, result parsing, metadata passthrough, audit,
/// and fail-closed identity behavior.
/// </summary>
public class GhPrOpenSourceTests
{
    [Fact]
    public async Task OpenAsync_EmptyMetadata_OmitsOptionalArgsAndParsesPrNumber()
    {
        IReadOnlyList<string>? captured = null;
        var source = new GhPrOpenSource(
            "owner/repo",
            new GitHubActorIdentity("nightshift-bot[app]"),
            new FixedPrOpenMetadataProvider(PrOpenMetadata.Empty),
            new RecordingPrOpenAuditSink(),
            (args, _) =>
            {
                captured = args;
                return Task.FromResult(new GhResult(0, "https://github.com/owner/repo/pull/42\n", string.Empty));
            },
            () => DateTimeOffset.Parse("2026-07-18T00:00:00Z"));

        PrOpenOutcome outcome = await source.OpenAsync("/plan/2/order/op-a", "nightshift/2/op-a", TestContext.Current.CancellationToken);

        Assert.Equal(PrOpenOutcomeKind.Opened, outcome.Kind);
        Assert.Equal(42, outcome.PrNumber);
        Assert.NotNull(captured);
        Assert.Contains("--fill", captured!);
        Assert.Contains("--base", captured!);
        Assert.Contains("main", captured!);
        Assert.Contains("--head", captured!);
        Assert.Contains("nightshift/2/op-a", captured!);
        Assert.DoesNotContain("--label", captured!);
        Assert.DoesNotContain("--reviewer", captured!);
        Assert.DoesNotContain("--milestone", captured!);
    }

    [Fact]
    public async Task OpenAsync_MetadataPresent_PassesLabelsReviewersAndMilestone()
    {
        IReadOnlyList<string>? captured = null;
        var source = new GhPrOpenSource(
            "owner/repo",
            new GitHubActorIdentity("nightshift-bot[app]"),
            new FixedPrOpenMetadataProvider(new PrOpenMetadata(
                Labels: ["auto-merge", "nightshift"],
                Reviewers: ["octocat", "hubot"],
                Milestone: "M1")),
            new RecordingPrOpenAuditSink(),
            (args, _) =>
            {
                captured = args;
                return Task.FromResult(new GhResult(0, "https://github.com/owner/repo/pull/43\n", string.Empty));
            },
            () => DateTimeOffset.Parse("2026-07-18T00:00:00Z"));

        PrOpenOutcome outcome = await source.OpenAsync("/plan/2/order/op-a", "nightshift/2/op-a", TestContext.Current.CancellationToken);

        Assert.Equal(PrOpenOutcomeKind.Opened, outcome.Kind);
        Assert.Equal(43, outcome.PrNumber);
        Assert.NotNull(captured);
        Assert.Contains("--label", captured!);
        Assert.Contains("auto-merge", captured!);
        Assert.Contains("nightshift", captured!);
        Assert.Contains("--reviewer", captured!);
        Assert.Contains("octocat", captured!);
        Assert.Contains("hubot", captured!);
        Assert.Contains("--milestone", captured!);
        Assert.Contains("M1", captured!);
    }

    [Fact]
    public async Task OpenAsync_Success_RecordsAuditRow()
    {
        var audit = new RecordingPrOpenAuditSink();
        var source = new GhPrOpenSource(
            "owner/repo",
            new GitHubActorIdentity("nightshift-bot[app]"),
            new FixedPrOpenMetadataProvider(PrOpenMetadata.Empty),
            audit,
            (_, _) => Task.FromResult(new GhResult(0, "https://github.com/owner/repo/pull/44\n", string.Empty)),
            () => DateTimeOffset.Parse("2026-07-18T01:02:03Z"));

        PrOpenOutcome outcome = await source.OpenAsync("/plan/2/order/op-a", "nightshift/2/op-a", TestContext.Current.CancellationToken);

        Assert.Equal(PrOpenOutcomeKind.Opened, outcome.Kind);
        PrOpenedAuditRecord record = Assert.Single(audit.Records);
        Assert.Equal("nightshift-bot[app]", record.Actor.Value);
        Assert.Equal("/plan/2/order/op-a", record.OrderBase);
        Assert.Equal("nightshift/2/op-a", record.HeadBranch);
        Assert.Equal(44, record.PrNumber);
        Assert.Equal(DateTimeOffset.Parse("2026-07-18T01:02:03Z"), record.OpenedAt);
    }

    [Fact]
    public async Task OpenAsync_GhError_ReturnsFailed()
    {
        var source = new GhPrOpenSource(
            "owner/repo",
            new GitHubActorIdentity("nightshift-bot[app]"),
            (_, _) => Task.FromResult(new GhResult(1, string.Empty, "boom")));

        PrOpenOutcome outcome = await source.OpenAsync("/plan/2/order/op-a", "nightshift/2/op-a", TestContext.Current.CancellationToken);

        Assert.Equal(PrOpenOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(0, outcome.PrNumber);
    }

    [Fact]
    public async Task OpenAsync_AlreadyExists_ReturnsAlreadyExists()
    {
        var source = new GhPrOpenSource(
            "owner/repo",
            new GitHubActorIdentity("nightshift-bot[app]"),
            (_, _) => Task.FromResult(new GhResult(1, string.Empty, "a pull request already exists for branch")));

        PrOpenOutcome outcome = await source.OpenAsync("/plan/2/order/op-a", "nightshift/2/op-a", TestContext.Current.CancellationToken);

        Assert.Equal(PrOpenOutcomeKind.AlreadyExists, outcome.Kind);
    }

    [Fact]
    public async Task CreatePrOpenSource_UnconfiguredIdentity_FailsClosed()
    {
        IPrOpenSource source = ReconcileCommand.CreatePrOpenSource(
            "owner/repo",
            new ThrowingCredentialsSource(),
            NullPrOpenMetadataProvider.Instance,
            NullPrOpenAuditSink.Instance,
            out GitHubAppInstallationTokenProvider? tokenProvider);

        Assert.Null(tokenProvider);
        PrOpenOutcome outcome = await source.OpenAsync("/plan/2/order/op-a", "nightshift/2/op-a", TestContext.Current.CancellationToken);
        Assert.Equal(PrOpenOutcomeKind.Unavailable, outcome.Kind);
    }

    private sealed class FixedPrOpenMetadataProvider : IPrOpenMetadataProvider
    {
        private readonly PrOpenMetadata _metadata;

        public FixedPrOpenMetadataProvider(PrOpenMetadata metadata)
        {
            _metadata = metadata;
        }

        public ValueTask<PrOpenMetadata> GetMetadataAsync(string orderBase, CancellationToken ct)
            => ValueTask.FromResult(_metadata);
    }

    private sealed class RecordingPrOpenAuditSink : IPrOpenAuditSink
    {
        public List<PrOpenedAuditRecord> Records { get; } = [];

        public ValueTask RecordPrOpenedAsync(PrOpenedAuditRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingCredentialsSource : IGitHubAppCredentialsSource
    {
        public GitHubAppCredentials Load()
            => throw new InvalidOperationException("set OCTOSHIFT_GITHUB_APP_CREDENTIALS_PATH to the GitHub App credentials file path.");
    }
}
