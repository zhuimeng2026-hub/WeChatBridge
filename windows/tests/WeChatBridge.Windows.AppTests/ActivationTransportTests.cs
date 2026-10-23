using System.Net;
using System.Text;
using WeChatBridge.Windows.Activation;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.AppTests;

/// <summary>
/// The transport that talks to the licence service, exercised through an injected
/// handler — no socket is opened and no packet leaves the machine.
/// <para>
/// This file is written again rather than recovered. The original 678-line / 26-case
/// version lived in the worktree <c>D:\WeChatBridge-C</c>, which was deleted on
/// 2026-10-23 along with three untracked test files; it never reached any ref, so
/// there is nothing to port. What is below is derived from the contract in
/// <see cref="IActivationTransport"/> and from the promises the transport's own
/// comments make — the ones worth failing a build over.
/// </para>
/// </summary>
public sealed class ActivationTransportTests
{
    private static readonly Uri BaseAddress = new("https://licence.example/api");

    /// <summary>What the server saw, read inside the handler.</summary>
    private sealed class Capture
    {
        public int Calls;
        public string Method = "";
        public string Path = "";
        public string? Body;
        public string? Authorization;
        public string UserAgent = "";
        public string Accept = "";
        public string ContentType = "";
    }

    /// <summary>
    /// A handler that records the request and then answers however the test says.
    /// The body has to be read here, inside <c>SendAsync</c>: the transport disposes
    /// the request message as soon as it has the reply.
    /// </summary>
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public Capture Captured { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured.Calls++;
            Captured.Method = request.Method.Method;
            Captured.Path = request.RequestUri?.AbsolutePath ?? "";
            Captured.Authorization = request.Headers.Authorization?.ToString();
            Captured.UserAgent = request.Headers.UserAgent.ToString();
            Captured.Accept = string.Join(",", request.Headers.Accept.Select(accept => accept.MediaType));
            Captured.ContentType = request.Content?.Headers.ContentType?.ToString() ?? "";
            Captured.Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(request);
        }
    }

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (HttpActivationTransport Transport, StubHandler Handler) Build(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond,
        Uri? baseAddress = null)
    {
        var handler = new StubHandler(respond);
        return (new HttpActivationTransport(baseAddress ?? BaseAddress, handler), handler);
    }

    private static ApiRequest Request(
        string method = "POST",
        string path = "v1/auth/session",
        string? json = null,
        string? bearer = null) => new(method, path, json, bearer);

    // ── 地址：基址在构造时固定，尾斜杠由它自己补 ──────────────────────────────

    [Fact]
    public void ABaseAddressWithoutATrailingSlashGetsOne()
    {
        var (transport, _) = Build(_ => Task.FromResult(Json(200, "{}")));

        // Without the slash the last segment would be replaced: new Uri("https://h/api", "v1/x")
        // is https://h/v1/x, so the service prefix would silently disappear.
        Assert.EndsWith("/", transport.Client.BaseAddress!.AbsoluteUri);
    }

    [Fact]
    public void ABaseAddressWithATrailingSlashIsLeftAlone()
    {
        var (transport, _) = Build(_ => Task.FromResult(Json(200, "{}")), new Uri("https://licence.example/api/"));

        Assert.Equal("https://licence.example/api/", transport.Client.BaseAddress!.AbsoluteUri);
    }

    [Fact]
    public async Task ThePathIsRelativeToTheBaseAddress()
    {
        var (transport, handler) = Build(_ => Task.FromResult(Json(200, "{}")));

        await transport.SendAsync(Request(path: "v1/licence/ticket"), CancellationToken.None);

        Assert.Equal("/api/v1/licence/ticket", handler.Captured.Path);
    }

    // ── 请求的形状 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheVerbIsSentAsGiven()
    {
        var (transport, handler) = Build(_ => Task.FromResult(Json(200, "{}")));

        await transport.SendAsync(Request("GET"), CancellationToken.None);

        Assert.Equal("GET", handler.Captured.Method);
    }

    [Fact]
    public async Task AJsonBodyIsSentAsUtf8Json()
    {
        var (transport, handler) = Build(_ => Task.FromResult(Json(200, "{}")));

        await transport.SendAsync(Request(json: "{\"ticket\":\"T-1\"}"), CancellationToken.None);

        Assert.Equal("{\"ticket\":\"T-1\"}", handler.Captured.Body);
        Assert.Contains("application/json", handler.Captured.ContentType);
        Assert.Contains("utf-8", handler.Captured.ContentType);
    }

    [Fact]
    public async Task NoBodyMeansNoContent()
    {
        var (transport, handler) = Build(_ => Task.FromResult(Json(200, "{}")));

        await transport.SendAsync(Request(json: null), CancellationToken.None);

        Assert.Null(handler.Captured.Body);
        Assert.Equal("", handler.Captured.ContentType);
    }

    [Fact]
    public async Task ABearerTokenBecomesAnAuthorizationHeader()
    {
        var (transport, handler) = Build(_ => Task.FromResult(Json(200, "{}")));

        await transport.SendAsync(Request(bearer: "wcb-token"), CancellationToken.None);

        Assert.Equal("Bearer wcb-token", handler.Captured.Authorization);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoTokenMeansNoAuthorizationHeader(string? token)
    {
        var (transport, handler) = Build(_ => Task.FromResult(Json(200, "{}")));

        await transport.SendAsync(Request(bearer: token), CancellationToken.None);

        Assert.Null(handler.Captured.Authorization);
    }

    [Fact]
    public async Task TheUserAgentCarriesTheEditionName()
    {
        var (transport, handler) = Build(_ => Task.FromResult(Json(200, "{}")));

        await transport.SendAsync(Request(), CancellationToken.None);

        Assert.Contains("WeChatBridge-Windows", handler.Captured.UserAgent);
        Assert.Contains(Edition.Name, handler.Captured.UserAgent);
    }

    [Fact]
    public async Task JsonIsAccepted()
    {
        var (transport, handler) = Build(_ => Task.FromResult(Json(200, "{}")));

        await transport.SendAsync(Request(), CancellationToken.None);

        Assert.Equal("application/json", handler.Captured.Accept);
    }

    [Fact]
    public async Task OneRequestMeansOneCall()
    {
        var (transport, handler) = Build(_ => Task.FromResult(Json(200, "{}")));

        // The buy-out wording says this build reaches exactly the endpoints named and
        // nothing else. A transport that quietly retried would send three requests
        // for one purchase, and nothing in the UI would show it.
        await transport.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(1, handler.Captured.Calls);
    }

    // ── 响应：传输层不判断成败 ────────────────────────────────────────────────

    [Fact]
    public async Task ASuccessfulCallBecomesASuccessfulReply()
    {
        var (transport, _) = Build(_ => Task.FromResult(Json(200, "{\"ok\":true}")));

        var reply = await transport.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(200, reply.StatusCode);
        Assert.Equal("{\"ok\":true}", reply.Body);
        Assert.True(reply.IsSuccess);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task AFailedCallIsNotJudgedByTheTransport(int status)
    {
        var (transport, _) = Build(_ => Task.FromResult(Json(status, "{\"error\":\"nope\"}")));

        // The transport reports; the callers decide. Throwing here would lose the
        // body, and the body is where the error code and the request id live.
        var reply = await transport.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(status, reply.StatusCode);
        Assert.False(reply.IsSuccess);
    }

    // ── 失败映射：没有响应就是 transport 错误 ──────────────────────────────────

    [Fact]
    public async Task AConnectionFailureBecomesATransportException()
    {
        var (transport, _) = Build(_ => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("connection refused")));

        var error = await Assert.ThrowsAsync<ApiTransportException>(
            () => transport.SendAsync(Request(), CancellationToken.None));

        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Fact]
    public async Task ABodyThatStopsMidStreamBecomesATransportException()
    {
        var (transport, _) = Build(_ => Task.FromException<HttpResponseMessage>(
            new IOException("the response ended early")));

        var error = await Assert.ThrowsAsync<ApiTransportException>(
            () => transport.SendAsync(Request(), CancellationToken.None));

        Assert.IsType<IOException>(error.InnerException);
    }

    [Fact]
    public async Task ACancelledCallStaysCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var (transport, _) = Build(_ =>
        {
            var cancelled = new TaskCompletionSource<HttpResponseMessage>();
            cancelled.TrySetCanceled(cancellation.Token);
            return cancelled.Task;
        });
        await cancellation.CancelAsync();

        // The deadline and the user pressing Cancel both arrive as a cancellation.
        // The transport must not swallow one into the other, or "the server never
        // answered" becomes indistinguishable from "the user gave up".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.SendAsync(Request(), cancellation.Token));
    }

    // ── 取图：二维码走同一个客户端、同一套失败映射 ────────────────────────────

    [Fact]
    public async Task AnImageIsFetchedAsBytes()
    {
        byte[] payload = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        var (transport, handler) = Build(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));

        var bytes = await transport.GetBytesAsync("v1/auth/session/qr.png", CancellationToken.None);

        Assert.True(payload.SequenceEqual(bytes));
        Assert.Equal("GET", handler.Captured.Method);
        Assert.Equal("/api/v1/auth/session/qr.png", handler.Captured.Path);
    }

    [Fact]
    public async Task AFailedImageFetchBecomesAnApiException()
    {
        var (transport, _) = Build(_ => Task.FromResult(Json(503, "{}")));

        var error = await Assert.ThrowsAsync<ApiException>(
            () => transport.GetBytesAsync("v1/auth/session/qr.png", CancellationToken.None));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal(ApiErrorCodes.Internal, error.Code);
    }

    [Fact]
    public async Task AFailedImageFetchBecomesATransportExceptionWhenThereIsNoResponse()
    {
        var (transport, _) = Build(_ => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("connection refused")));

        await Assert.ThrowsAsync<ApiTransportException>(
            () => transport.GetBytesAsync("v1/auth/session/qr.png", CancellationToken.None));
    }

    // ── 封闭 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AfterDisposeTheTransportRefusesToSend()
    {
        var (transport, _) = Build(_ => Task.FromResult(Json(200, "{}")));
        transport.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => transport.SendAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task AfterDisposeTheTransportRefusesToFetchImages()
    {
        var (transport, _) = Build(_ => Task.FromResult(Json(200, "{}")));
        transport.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => transport.GetBytesAsync("v1/auth/session/qr.png", CancellationToken.None));
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var (transport, _) = Build(_ => Task.FromResult(Json(200, "{}")));

        transport.Dispose();
        transport.Dispose();
    }

    [Fact]
    public void TheTimeoutIsInfinite()
    {
        var (transport, _) = Build(_ => Task.FromResult(Json(200, "{}")));

        // Infinite on purpose: HttpClient's own timeout raises the same
        // TaskCanceledException as the user pressing Cancel, and after the fact no
        // catch clause can tell the two apart. The deadline is a linked token
        // source in AccountService and ActivationService instead.
        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, transport.Client.Timeout);
    }
}
