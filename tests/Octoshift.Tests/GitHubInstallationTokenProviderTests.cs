namespace Octoshift.Tests;

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Octoshift.GitHub;
using Xunit;

/// <summary>
/// <see cref="GitHubAppInstallationTokenProvider"/> guards a shared, expiring credential. It must mint once
/// and serve the cache until the token nears expiry, refresh exactly once no matter how many callers arrive
/// together, surface every exchange failure as the configured auth-config exception, honour cancellation, and
/// stop serving after disposal. And because the fast path reads the cache without the lock, a caller must only
/// ever see a whole token — never a new token string beside a stale expiry — which the reference-published
/// cache guarantees.
/// </summary>
public class GitHubInstallationTokenProviderTests
{
    private static readonly string PrivateKeyPem = RSA.Create(2048).ExportPkcs8PrivateKeyPem();

    private static GitHubAppCredentials Credentials()
        => new("123", 456, PrivateKeyPem, new GitHubActorIdentity("nightshift-bot[app]"));

    private static string TokenResponse(string token, DateTimeOffset expiresAt)
        => $$"""{"token":"{{token}}","expires_at":"{{expiresAt.ToString("o", CultureInfo.InvariantCulture)}}"}""";

    private sealed class RecordingAuditSink : IGitHubTokenAuditSink
    {
        public ConcurrentQueue<GitHubTokenAuditKind> Kinds { get; } = new();

        public ValueTask RecordTokenMintAsync(GitHubTokenAuditRecord record, CancellationToken ct)
        {
            Kinds.Enqueue(record.Kind);
            return ValueTask.CompletedTask;
        }
    }

    private static GitHubAppInstallationTokenProvider Provider(
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string?>?, CancellationToken, Task<GhResult>> runGhAsync,
        Func<DateTimeOffset> clock,
        TimeSpan refreshSkew,
        IGitHubTokenAuditSink auditSink)
        => new(Credentials(), new GitHubAppJwtFactory(), runGhAsync, clock, refreshSkew, auditSink);

    [Fact]
    public async Task FirstCallMints_SecondCallServesTheCacheWithoutAnotherExchange()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        int exchanges = 0;
        var audit = new RecordingAuditSink();

        using var provider = Provider(
            (args, env, ct) =>
            {
                Interlocked.Increment(ref exchanges);
                return Task.FromResult(new GhResult(0, TokenResponse("tok-1", now.AddHours(1)), string.Empty));
            },
            () => now,
            TimeSpan.FromMinutes(3),
            audit);

        GitHubInstallationToken first = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        GitHubInstallationToken second = await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("tok-1", first.Token);
        Assert.Equal("tok-1", second.Token);
        Assert.Equal(1, exchanges);
        Assert.Equal([GitHubTokenAuditKind.Minted], audit.Kinds.ToArray());
    }

    [Fact]
    public async Task NearExpiry_RefreshesAndAuditsTheRefresh()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        int exchanges = 0;
        var audit = new RecordingAuditSink();

        using var provider = Provider(
            (args, env, ct) =>
            {
                int n = Interlocked.Increment(ref exchanges);
                return Task.FromResult(new GhResult(0, TokenResponse($"tok-{n}", clock.Now.AddHours(1)), string.Empty));
            },
            () => clock.Now,
            TimeSpan.FromMinutes(3),
            audit);

        GitHubInstallationToken first = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        Assert.Equal("tok-1", first.Token);

        // Advance to within the refresh skew of the first token's expiry: the next call must refresh.
        clock.Now = first.ExpiresAt.AddMinutes(-2);
        GitHubInstallationToken second = await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("tok-2", second.Token);
        Assert.Equal(2, exchanges);
        Assert.Equal([GitHubTokenAuditKind.Minted, GitHubTokenAuditKind.Refreshed], audit.Kinds.ToArray());
    }

    [Fact]
    public async Task ConcurrentFirstCallers_ProduceExactlyOneExchange()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        int exchanges = 0;
        var gate = new TaskCompletionSource();

        using var provider = Provider(
            async (args, env, ct) =>
            {
                Interlocked.Increment(ref exchanges);
                await gate.Task;
                return new GhResult(0, TokenResponse("tok-1", now.AddHours(1)), string.Empty);
            },
            () => now,
            TimeSpan.FromMinutes(3),
            NullGitHubTokenAuditSink.Instance);

        CancellationToken ct = TestContext.Current.CancellationToken;
        Task<GitHubInstallationToken>[] callers = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => provider.GetTokenAsync(ct), ct))
            .ToArray();

        // Let them all pile onto the refresh lock before the single exchange completes.
        await Task.Delay(50, ct);
        gate.SetResult();
        GitHubInstallationToken[] tokens = await Task.WhenAll(callers);

        Assert.Equal(1, exchanges);
        Assert.All(tokens, t => Assert.Equal("tok-1", t.Token));
    }

    [Fact]
    public async Task UnderChurn_EveryReturnedTokenPairsItsOwnExpiry()
    {
        // Each minted token is paired with a unique expiry; a torn read of the old nullable-struct cache could
        // hand a caller one token's string beside another's expiry. The reference-published cache cannot, so
        // every returned pair must be one that was actually minted.
        //
        // The load is a fixed iteration count behind a start barrier, not a wall-clock deadline: every reader
        // contributes exactly the same number of observations no matter how the thread pool schedules it, so a
        // scheduling stall can never leave the observation set empty. Short-lived tokens keep refreshes and
        // lock-free cache hits interleaving throughout.
        const int readers = 8;
        const int iterationsPerReader = 400;
        var pairs = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        int n = 0;

        using var provider = Provider(
            (args, env, ct) =>
            {
                int i = Interlocked.Increment(ref n);
                DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddMilliseconds(20);
                string token = $"tok-{i}";
                pairs[token] = expiresAt;
                return Task.FromResult(new GhResult(0, TokenResponse(token, expiresAt), string.Empty));
            },
            () => DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            NullGitHubTokenAuditSink.Instance);

        CancellationToken ct = TestContext.Current.CancellationToken;
        var observed = new ConcurrentBag<GitHubInstallationToken>();
        using var barrier = new Barrier(readers);

        async Task Reader()
        {
            barrier.SignalAndWait(ct);
            for (int i = 0; i < iterationsPerReader; i++)
            {
                observed.Add(await provider.GetTokenAsync(ct));
            }
        }

        await Task.WhenAll(Enumerable.Range(0, readers).Select(_ => Task.Run(Reader, ct)));

        Assert.Equal(readers * iterationsPerReader, observed.Count);
        foreach (GitHubInstallationToken token in observed)
        {
            Assert.True(pairs.TryGetValue(token.Token, out DateTimeOffset expectedExpiry), $"unknown token {token.Token}");
            Assert.Equal(expectedExpiry, token.ExpiresAt);
        }
    }

    [Fact]
    public async Task NonZeroExitFromExchange_IsInvalidOperationWithDetail()
    {
        using var provider = Provider(
            (args, env, ct) => Task.FromResult(new GhResult(1, string.Empty, "gh: bad credentials")),
            () => DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(3),
            NullGitHubTokenAuditSink.Instance);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetTokenAsync(TestContext.Current.CancellationToken));
        Assert.Contains("bad credentials", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"expires_at":"2026-01-01T00:00:00Z"}""")]              // no token
    [InlineData("""{"token":"tok","expires_at":"not-a-date"}""")]         // unparseable expiry
    public async Task MalformedExchangeResponse_IsInvalidOperation(string stdout)
    {
        using var provider = Provider(
            (args, env, ct) => Task.FromResult(new GhResult(0, stdout, string.Empty)),
            () => DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(3),
            NullGitHubTokenAuditSink.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_IsPropagated()
    {
        using var provider = Provider(
            (args, env, ct) => Task.FromResult(new GhResult(0, TokenResponse("tok", DateTimeOffset.UtcNow.AddHours(1)), string.Empty)),
            () => DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(3),
            NullGitHubTokenAuditSink.Instance);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetTokenAsync(cts.Token));
    }

    [Fact]
    public async Task AfterDispose_GetTokenThrowsObjectDisposed()
    {
        var provider = Provider(
            (args, env, ct) => Task.FromResult(new GhResult(0, TokenResponse("tok", DateTimeOffset.UtcNow.AddHours(1)), string.Empty)),
            () => DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(3),
            NullGitHubTokenAuditSink.Instance);

        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.GetTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WarmedCacheAfterDispose_StillThrowsObjectDisposed()
    {
        // The dangerous case: a provider whose cache is warm and valid. Because the fast path reads the cache
        // before touching the semaphore, a disposed-check that lived only inside the lock would let a disposed
        // provider keep serving its token forever. The check has to precede the fast path.
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var provider = Provider(
            (args, env, ct) => Task.FromResult(new GhResult(0, TokenResponse("tok-1", now.AddHours(1)), string.Empty)),
            () => now,
            TimeSpan.FromMinutes(3),
            NullGitHubTokenAuditSink.Instance);

        GitHubInstallationToken warm = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        Assert.Equal("tok-1", warm.Token);

        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.GetTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeDuringInFlightRefresh_LetsItFinishAndBlocksLaterCallers()
    {
        // A refresh is holding the lock when Dispose lands, and a second caller is queued behind it. Disposing
        // the SemaphoreSlim here would make the in-flight refresh's finally-Release throw and could fault the
        // queued waiter; the design instead disposes only logically. So the in-flight refresh completes and its
        // Release succeeds, the queued caller wakes, sees the disposed flag, and fails cleanly with
        // ObjectDisposedException — never a raw semaphore error.
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var provider = Provider(
            async (args, env, ct) =>
            {
                entered.TrySetResult();
                await release.Task;
                return new GhResult(0, TokenResponse("tok-1", now.AddHours(1)), string.Empty);
            },
            () => now,
            TimeSpan.FromMinutes(3),
            NullGitHubTokenAuditSink.Instance);

        CancellationToken ct = TestContext.Current.CancellationToken;
        Task<GitHubInstallationToken> inFlight = Task.Run(() => provider.GetTokenAsync(ct), ct);
        await entered.Task; // the refresh holds the lock

        Task<GitHubInstallationToken> queued = Task.Run(() => provider.GetTokenAsync(ct), ct);
        await Task.Delay(50, ct); // let the queued caller block on the lock

        provider.Dispose();
        release.SetResult();

        // The operation that was already minting when Dispose landed finishes cleanly; its Release must not throw.
        GitHubInstallationToken minted = await inFlight;
        Assert.Equal("tok-1", minted.Token);

        // The caller that was still queued behind the lock is refused once it acquires it.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);

        // And every subsequent call is refused too.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.GetTokenAsync(ct));
    }

    private sealed class MutableClock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; set; } = start;
    }
}
