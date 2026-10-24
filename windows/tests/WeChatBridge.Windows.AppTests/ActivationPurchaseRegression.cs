using System.Security.Cryptography;
using WeChatBridge.Windows.Activation;
using WeChatBridge.Windows.Core.Activation;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.AppTests;

/// <summary>
/// The purchase window's state machine, driven through the transport seam.
/// <para>
/// The property worth guarding here is the one a customer would notice first:
/// <b>a licence that was never written must never read as a licence</b>. The
/// service answers 409 for several real states — the seller has not confirmed,
/// the licence was revoked, another account holds this machine's registration —
/// and the old client read every one of them as a successful purchase: the
/// window said 「授权成功，感谢购买。」, the gate was told the customer was
/// licensed, and no envelope had ever reached the disk.
/// </para>
/// <para>
/// The counter-test matters just as much: a licence that really was collected
/// still has to end the purchase and tell the gate. Without it, "never lies"
/// would also be satisfied by "never succeeds".
/// </para>
/// </summary>
public sealed class ActivationPurchaseRegressionTests
{
    private const string InstallId = "Zx8Kq3nT7pQ1mR4vW9yB0";
    private const string AnchorDigest = "sha256:8c1fa31d";

    /// <summary>
    /// A refused licence read is a pause, not a purchase and not a failure: the
    /// controller keeps polling, the licensed callback never fires, and the
    /// licence file stays empty.
    /// </summary>
    [Fact]
    public async Task AConflictOnTheLicenceReadIsAPauseAndNotALicence()
    {
        using var layout = new TempLayout();
        using var keys = new SigningKey();
        var transport = new ScriptedTransport();
        transport.Reply(ActivationEndpoint.ActivationRequestPath, 201, "{\"requestId\":\"act_abc123\",\"status\":\"pending\"}");
        transport.Reply(ActivationEndpoint.ActivationStatusPath, 200, "{\"status\":\"confirmed\",\"licenseId\":\"lic_abc123\"}");
        transport.Reply(ActivationEndpoint.ActivationLicencePath, 409,
            "{\"error\":{\"code\":\"conflict\",\"message\":\"授权尚未确认，暂时无法获取许可证。\",\"request_id\":\"\"}}");

        var licensed = false;
        ActivationController? controller = null;
        transport.OnLicenceRead = count =>
        {
            // The second read is the proof: the first one was refused, and the
            // controller came back for another instead of ending the purchase.
            if (count >= 2)
                controller?.Cancel();
        };
        controller = new ActivationController(
            Service(layout, transport, keys),
            InstallId,
            AnchorDigest,
            onLicensed: () => licensed = true,
            schedule: new PollSchedule(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 1.0));

        await controller.RegisterAsync();

        Assert.True(transport.LicenceReads >= 2, "The purchase gave up at the first refused read.");
        Assert.False(licensed, "A licence that was never written must not raise the licensed callback.");
        Assert.NotEqual(PurchaseStage.Done, controller.Stage);
        Assert.False(controller.IsLicensed);
        // The wait is still stoppable: a licence that never becomes readable
        // must not lock the customer into a poll they cannot end.
        Assert.True(controller.IsPolling);
        Assert.Null(new LicenseStore(layout.Installed.Config).Load());
    }

    /// <summary>
    /// The other half of the pair: a licence that really was collected ends the
    /// purchase, says so, and tells the gate.
    /// </summary>
    [Fact]
    public async Task ACollectedLicenceIsTheOnlyThingThatLicenses()
    {
        using var layout = new TempLayout();
        using var keys = new SigningKey();
        var transport = new ScriptedTransport();
        transport.Reply(ActivationEndpoint.ActivationRequestPath, 201, "{\"requestId\":\"act_abc123\",\"status\":\"pending\"}");
        transport.Reply(ActivationEndpoint.ActivationStatusPath, 200, "{\"status\":\"confirmed\",\"licenseId\":\"lic_abc123\"}");
        transport.Reply(ActivationEndpoint.ActivationLicencePath, 200, LicenceBody(keys.Issue(InstallId, DateTimeOffset.UtcNow)));

        var licensed = false;
        using var controller = new ActivationController(
            Service(layout, transport, keys),
            InstallId,
            AnchorDigest,
            onLicensed: () => licensed = true,
            schedule: new PollSchedule(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 1.0));

        await controller.RegisterAsync();

        Assert.Equal(PurchaseStage.Done, controller.Stage);
        Assert.True(licensed, "A collected licence has to tell the gate.");
        Assert.True(controller.IsLicensed);
        var stored = new LicenseStore(layout.Installed.Config).Load();
        Assert.NotNull(stored);
        Assert.Equal(InstallId, stored.Subject);
    }

    private static ActivationService Service(TempLayout layout, ScriptedTransport transport, SigningKey keys)
    {
        var account = new AccountService(transport);
        var now = DateTimeOffset.UtcNow;
        account.SignIn(new AuthTokens(
            "mock_at_1",
            "mock_rt_1",
            now.AddHours(1),
            new AccountView("acc_demo0001", "演示买家", "", "buyout", now, now, true)));
        return new ActivationService(
            new LicenseStore(layout.Installed.Config),
            keys.Verifier(),
            transport,
            account);
    }

    /// <summary>The licence endpoint's body, in the Go dialect: unix-second iat.</summary>
    private static string LicenceBody(LicenseEnvelope envelope)
    {
        var seconds = envelope.IssuedAt.ToUnixTimeSeconds();
        var entitlements = string.Join(",", envelope.Entitlements.Select(bit => $"\"{bit}\""));
        var json = $"{{\"v\":{envelope.SchemaVersion},\"sub\":\"{envelope.Subject}\",\"plan\":\"{envelope.Plan}\"," +
            $"\"iat\":{seconds},\"nbf\":null,\"exp\":null,\"ent\":[{entitlements}],\"sig\":\"{envelope.Signature}\"}}";
        return $"{{\"token\":\"wcb1_demo\",\"envelope\":{json}}}";
    }

    /// <summary>
    /// A key pair of the test's own: the shipped public key has no private half
    /// in this repository, so a licence that verifies has to be signed here.
    /// </summary>
    private sealed class SigningKey : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public LicenseVerifier Verifier() => new(
            LicensePublicKeys.ImportSubjectPublicKeyInfo,
            new Dictionary<int, string>
            {
                [LicenseToken.CurrentSchemaVersion] = Base64Url.Encode(_key.ExportSubjectPublicKeyInfo()),
            });

        public LicenseEnvelope Issue(string subject, DateTimeOffset issuedAt)
        {
            var envelope = new LicenseEnvelope
            {
                SchemaVersion = LicenseToken.CurrentSchemaVersion,
                Subject = subject,
                Plan = "buyout",
                IssuedAt = issuedAt,
                Entitlements = ["EXPORT"],
            };
            var payload = LicenseCanonicalPayload.From(envelope);
            var signature = _key.SignData(
                payload.Bytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return envelope with { Signature = Base64Url.Encode(signature) };
        }

        public void Dispose() => _key.Dispose();
    }

    /// <summary>
    /// One reply per endpoint, repeated for as long as the client keeps asking.
    /// </summary>
    private sealed class ScriptedTransport : IActivationTransport
    {
        private readonly Dictionary<string, ApiReply> _replies = new(StringComparer.Ordinal);

        public int LicenceReads { get; private set; }

        /// <summary>Raised after every licence read, with the number of reads so far.</summary>
        public Action<int>? OnLicenceRead { get; set; }

        public void Reply(string path, int statusCode, string body) =>
            _replies[path] = new ApiReply(statusCode, body);

        public Task<ApiReply> SendAsync(ApiRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_replies.TryGetValue(request.Path, out var reply))
                return Task.FromResult(new ApiReply(500, "{}"));
            if (string.Equals(request.Path, ActivationEndpoint.ActivationLicencePath, StringComparison.Ordinal))
            {
                LicenceReads++;
                OnLicenceRead?.Invoke(LicenceReads);
            }
            return Task.FromResult(reply);
        }

        public Task<byte[]> GetBytesAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Dispose() { }
    }
}
