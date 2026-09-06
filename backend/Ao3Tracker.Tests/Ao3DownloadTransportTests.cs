using System.Net;
using Ao3Tracker.Api.Services.Credentials;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Tests;

/// <summary>
/// What fetching a file rather than a page actually puts on the wire, over the real
/// <see cref="RateLimitedAo3HttpClient"/> and a handler standing in for AO3.
///
/// The fake the rest of the suite uses implements the interface, so it cannot answer any of this:
/// whether the instance's session and User-Agent really travel with a download, whether a body that
/// is not a 200 is kept off the disk, or whether a response with no end is allowed to run forever.
/// Those are decided by the transport, so they are tested against the transport.
/// </summary>
public class Ao3DownloadTransportTests : IDisposable
{
    private const string Url = "https://ao3.test/downloads/1/we_chose_to_wait.epub?updated_at=1767140797";

    private static readonly byte[] Body = "EPUB bytes"u8.ToArray();

    private readonly string _dataDirectory =
        Directory.CreateTempSubdirectory("ship-watcharr-downloads-").FullName;

    private readonly StubArchive _archive = new();
    private readonly StubSessionCache _sessions = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the test is untidy, never a failure.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Writes_the_body_to_the_caller_s_stream()
    {
        _archive.Answers = _ => File(HttpStatusCode.OK, Body);

        using var destination = new MemoryStream();
        var result = await Client().DownloadAsync(Url, destination);

        Assert.True(result.IsSuccess);
        Assert.Equal(Body.Length, result.BytesWritten);
        Assert.Equal(Body, destination.ToArray());
    }

    [Fact]
    public async Task Writes_nothing_at_all_for_a_response_that_is_not_a_200()
    {
        // AO3's explanation of a failure is not a copy of the work. Written to the stream it would
        // become an error page on disk under a name saying it was an EPUB.
        _archive.Answers = _ => File(HttpStatusCode.NotFound, "<html>Not found</html>"u8.ToArray());

        using var destination = new MemoryStream();
        var result = await Client().DownloadAsync(Url, destination);

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal(0, result.BytesWritten);
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public async Task Abandons_a_response_that_runs_past_what_this_instance_will_store()
    {
        // A chunked response has no length until it has finished arriving, so without a ceiling
        // "how much disk does one click cost" is a question only the remote end answers.
        _archive.Answers = _ => File(HttpStatusCode.OK, new byte[4096]);

        using var destination = new MemoryStream();
        var result = await Client(maxDownloadBytes: 1024).DownloadAsync(Url, destination);

        Assert.True(result.ExceededSizeLimit);

        // Reported as a failure, so the caller never names a truncated file a copy of the work.
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Attaches_the_instance_session_and_identity_to_a_file_request()
    {
        _sessions.Session = new Ao3Session("_otwarchive_session=abc123", DateTime.UtcNow, null);
        _archive.Answers = _ => File(HttpStatusCode.OK, Body);

        using var destination = new MemoryStream();
        await Client().DownloadAsync(Url, destination);

        var sent = Assert.Single(_archive.Received);
        Assert.Equal("_otwarchive_session=abc123", sent.Cookie);

        // The same honest header every other request carries. A download is not a request this app
        // makes anonymously while the rest of it identifies itself.
        Assert.Contains("ShipWatcharr", sent.UserAgent);
    }

    [Fact]
    public async Task Identifies_the_instance_even_on_a_download_it_has_no_session_for()
    {
        // Whether AO3's download addresses work logged out is not something the captured page can
        // answer, and this instance is gated on having a login before it drains a queue at all — so
        // the case only arises where a session has lapsed. What must hold either way is that the
        // request still says who is making it: an unidentified download is the one request this
        // project refuses to send, and "we had no cookie to attach" is not an excuse for making it
        // anonymously in both senses.
        _sessions.Session = null;
        _archive.Answers = _ => File(HttpStatusCode.OK, Body);

        using var destination = new MemoryStream();
        await Client().DownloadAsync(Url, destination);

        var sent = Assert.Single(_archive.Received);
        Assert.Null(sent.Cookie);
        Assert.Contains("ShipWatcharr", sent.UserAgent);
        Assert.Contains("emma@example.com", sent.UserAgent);
    }

    [Fact]
    public async Task Fetches_the_same_file_twice_rather_than_serving_it_from_the_page_cache()
    {
        // The response cache exists to stop a *page* being re-read within fifteen minutes. Holding
        // whole files in it would put an EPUB in memory for a quarter of an hour to save a fetch
        // the row naming those bytes has already saved.
        _archive.Answers = _ => File(HttpStatusCode.OK, Body);

        var client = Client();

        using var first = new MemoryStream();
        using var second = new MemoryStream();
        await client.DownloadAsync(Url, first);
        await client.DownloadAsync(Url, second);

        Assert.Equal(2, _archive.Received.Count);
    }

    [Fact]
    public async Task Does_not_spend_a_download_s_deadline_queueing_for_the_rate_gate()
    {
        // The deadline is what stops a socket that goes quiet holding the global gate. Armed where
        // the request is made rather than where the transfer begins, it also runs through the gate
        // wait and the 5-8s spacing — so a request that never received a byte fails as one whose
        // file stopped arriving, and the reader is told AO3 went quiet on a fetch AO3 never saw.
        _archive.Answers = _ => File(HttpStatusCode.OK, Body);

        var client = Client(
            minDelay: TimeSpan.FromMilliseconds(400),
            downloadTimeout: TimeSpan.FromMilliseconds(100));

        using var first = new MemoryStream();
        await client.DownloadAsync(Url, first);

        // The second request owes the spacing, which is four times the deadline it is allowed for
        // the transfer itself.
        using var second = new MemoryStream();
        var result = await client.DownloadAsync(Url, second);

        Assert.True(result.IsSuccess);
        Assert.Equal(Body, second.ToArray());
    }

    [Fact]
    public async Task Still_gives_up_on_a_transfer_that_does_not_finish()
    {
        // The other half: moving where the deadline starts must not stop it applying to what it is
        // for. HttpClient's own timeout cannot cover a transfer — with ResponseHeadersRead it stops
        // applying once the headers are in — so without this a copy that never ends holds the
        // global gate, and with it every other outbound request, for as long as it lasts.
        _archive.Answers = _ => File(HttpStatusCode.OK, Body);

        var client = Client(downloadTimeout: TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.DownloadAsync(Url, new StallingStream()));
    }

    private static HttpResponseMessage File(HttpStatusCode status, byte[] body) =>
        new(status) { Content = new ByteArrayContent(body) };

    /// <summary>
    /// A transfer that never finishes, stood up at the write rather than the read: a body that
    /// stalls is what this is about, and a response constructed in-process has no socket to stall.
    /// </summary>
    private sealed class StallingStream : Stream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            new(Task.Delay(Timeout.Infinite, ct));

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private RateLimitedAo3HttpClient Client(
        long maxDownloadBytes = 64L * 1024 * 1024,
        TimeSpan? minDelay = null,
        TimeSpan? downloadTimeout = null)
    {
        var options = Options.Create(new Ao3HttpClientOptions
        {
            BaseUrl = "https://ao3.test",
            MinDelayBetweenRequests = minDelay ?? TimeSpan.Zero,
            MaxDelayBetweenRequests = minDelay ?? TimeSpan.Zero,
            MaxDownloadBytes = maxDownloadBytes,
            DownloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(5),
        });

        var storagePaths = new StoragePaths(
            _dataDirectory,
            Path.Combine(_dataDirectory, "test.db"),
            Path.Combine(_dataDirectory, "settings.json"),
            Path.Combine(_dataDirectory, "keys"));

        // The real User-Agent provider over a stub contact, as the login transport tests do: what
        // goes in the header is decided by that provider's rules rather than by this test.
        var userAgents = new Ao3UserAgentProvider(
            options,
            InstanceIdentity.LoadOrCreate(storagePaths),
            new StubContacts(() => "emma@example.com"));

        return new RateLimitedAo3HttpClient(
            new Ao3RateGate(options, TimeProvider.System, NullLogger<Ao3RateGate>.Instance),
            new HttpClient(_archive),
            new Ao3LoginHttpClient(new HttpClient(new StubArchive())),
            new MemoryCache(new MemoryCacheOptions()),
            options,
            userAgents,
            _sessions,
            TimeProvider.System,
            NullLogger<RateLimitedAo3HttpClient>.Instance);
    }
}
