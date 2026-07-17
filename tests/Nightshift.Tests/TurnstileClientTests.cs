namespace Nightshift.Tests;

using System.Net;
using System.Text;
using Nightshift.Turnstile;
using Xunit;

public class TurnstileClientTests
{
    [Fact]
    public async Task WatchAsync_Gone_ThrowsWatchCompactedException()
    {
        using var client = new TurnstileClient(new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone)
            {
                Content = new StringContent("{\"compact_revision\":1234}", Encoding.UTF8, "application/json"),
            })))
        {
            BaseAddress = new Uri("http://localhost"),
            Timeout = Timeout.InfiniteTimeSpan,
        });

        WatchCompactedException ex = await Assert.ThrowsAsync<WatchCompactedException>(
            () => DrainAsync(client.WatchAsync("/plan/", 987, TestContext.Current.CancellationToken)));

        Assert.Equal("/plan/", ex.Prefix);
        Assert.Equal(987, ex.FromExclusive);
        Assert.Equal(1234, ex.CompactRevision);
    }

    private static async Task DrainAsync(IAsyncEnumerable<WatchSignal> stream)
    {
        await foreach (WatchSignal _ in stream)
        {
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _send(request, cancellationToken);
    }
}
