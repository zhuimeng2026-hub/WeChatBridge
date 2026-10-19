using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WeChatBridge.Windows.Activation;
using WeChatBridge.Windows.Core.Activation;
using WeChatBridge.Windows.Panes;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.AppTests;

internal static class LoginRefreshRegression
{
    // Runs inside the smoke test's STA dispatcher and application resources.
    internal static void VerifyAsyncScan()
    {
        using var account = new AccountService(new AsyncLoginTransport(),
            delay: (_, ct) => Task.Delay(1, ct));
        using var controller = new AccountController(account, "test-install");
        var window = new LoginWindow(controller);
        Task? login = null;
        window.Loaded += (_, _) => login = controller.StartAsync();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timeout.Tick += (_, _) => window.Close();
        timeout.Start();
        try
        {
            Assert.True(window.ShowDialog());
            Assert.NotNull(login);
            Assert.True(login.IsCompleted);
            login.GetAwaiter().GetResult();
            Assert.Equal(LoginStage.Done, controller.Stage);
            Assert.True(account.IsSignedIn);
            Assert.Equal("扫码用户", account.Current?.Account.DisplayName);
            Assert.False(window.IsVisible);
        }
        finally
        {
            timeout.Stop();
            window.Close();
        }
    }

    internal static void VerifySessionRefresh()
    {
        using var layout = new TempLayout();
        using var gateway = ActivationGateway.Create(LicensingBootstrap.Create(layout.Installed),
            () => "https://example.invalid/");
        using var gate = new ActivationGate(gateway, new AppSettingsStore(layout.Installed.Config), null);
        var pane = new GeneralPane(gate);
        var window = new Window { Content = pane, Width = 720, Height = 650 };
        window.Show();
        try
        {
            PumpUntil(() => pane.IsLoaded);
            var session = (TextBlock)pane.FindName("SessionInfoText");
            var status = (TextBlock)pane.FindName("LicenceStatusText");
            Assert.Equal(Visibility.Collapsed, session.Visibility);

            var now = DateTimeOffset.UtcNow;
            var tokens = new AuthTokens("access", "refresh", now.AddHours(1),
                new AccountView("buyer", "扫码用户", "", "buyout", now, now, true));
            Task.Run(() => gateway.Account.SignIn(tokens)).GetAwaiter().GetResult();
            PumpUntil(() => session.Visibility == Visibility.Visible && session.Text.Contains("扫码用户"));
            Assert.Contains("扫码用户", status.Text);

            var renamed = tokens with { Account = tokens.Account with { Nickname = "新昵称" } };
            Task.Run(() => gateway.Account.SignIn(renamed)).GetAwaiter().GetResult();
            PumpUntil(() => session.Text.Contains("新昵称"));
            Assert.Contains("新昵称", status.Text);

            Task.Run(gateway.Account.SignOutLocally).GetAwaiter().GetResult();
            PumpUntil(() => session.Visibility == Visibility.Collapsed);
            Assert.Empty(session.Text);
            Assert.DoesNotContain("新昵称", status.Text);

            // Logging in while the pane is detached must appear on re-entry,
            // without a focus change or another server request.
            window.Content = null;
            PumpUntil(() => !pane.IsLoaded);
            gateway.Account.SignIn(tokens);
            window.Content = pane;
            PumpUntil(() => pane.IsLoaded && session.Visibility == Visibility.Visible);
            Assert.Contains("扫码用户", session.Text);
        }
        finally { window.Close(); }
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => frame.Continue = !ready() && DateTime.UtcNow < deadline;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(ready(), "The async login did not finish on the UI dispatcher.");
    }

    private sealed class AsyncLoginTransport : IActivationTransport
    {
        private int _polls;

        public async Task<ApiReply> SendAsync(ApiRequest request, CancellationToken cancellationToken)
        {
            // Real HTTP completes asynchronously; synchronous fake replies hide
            // callbacks that incorrectly touch WPF controls on a worker thread.
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            if (request.Method == "POST")
                return Reply(new { ticket = "async-scan", authorizeUrl = "https://example.invalid/login", expiresAt });
            if (++_polls <= 2)
                return Reply(new { status = "pending", expiresAt });
            return Reply(new
            {
                status = "authorized", accessToken = "test-access", refreshToken = "test-refresh", expiresAt,
                account = new { id = "test-account", nickname = "扫码用户", wechatBound = true }
            });
        }

        private static ApiReply Reply(object body) => new(200, JsonSerializer.Serialize(body));
        public Task<byte[]> GetBytesAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public void Dispose() { }
    }
}
