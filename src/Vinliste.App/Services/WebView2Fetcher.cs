using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Vinliste.Core.Http;
using Vinliste.Core.Ratings;

namespace Vinliste.App.Services;

/// <summary>
/// Fetches pages through a hidden Edge (WebView2) browser. Sites such as Falstaff and
/// Wine Enthusiast reject plain HTTP clients by TLS fingerprint and run JavaScript
/// challenges; a real browser engine passes both.
/// </summary>
public sealed class WebView2Fetcher(Dispatcher dispatcher, string userDataFolder) : IHttpFetcher, IDisposable
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ChallengeWait = TimeSpan.FromSeconds(12);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Window? _host;
    private WebView2? _view;
    private bool _initFailed;

    public async Task<FetchResponse> GetAsync(string url, string accept, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operation = dispatcher.InvokeAsync(() => FetchOnUiThreadAsync(url, cancellationToken));
            return await operation.Task.Unwrap().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FetchResponse> FetchOnUiThreadAsync(string url, CancellationToken ct)
    {
        var view = await EnsureViewAsync().ConfigureAwait(true);
        if (view is null)
        {
            return new FetchResponse(0, "The WebView2 runtime is not available.");
        }

        var core = view.CoreWebView2;
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        // Only this fetch's navigation counts: a late event from an earlier, stopped navigation
        // must not complete it. Redirects keep the same navigation id.
        void OnStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) => navigationId ??= e.NavigationId;
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (navigationId == e.NavigationId)
            {
                completed.TrySetResult(e.IsSuccess ? (e.HttpStatusCode == 0 ? 200 : e.HttpStatusCode) : e.HttpStatusCode);
            }
        }

        core.NavigationStarting += OnStarting;
        core.NavigationCompleted += OnCompleted;
        try
        {
            core.Navigate(url);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(NavigationTimeout);
            await using (timeout.Token.Register(() => completed.TrySetCanceled()))
            {
                int status;
                try
                {
                    status = await completed.Task.ConfigureAwait(true);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    core.Stop();
                    return new FetchResponse(0, "Timed out loading page");
                }

                var html = await ReadHtmlAsync(core).ConfigureAwait(true);
                // Bot challenges solve themselves in a real browser; give them a moment.
                var deadline = DateTime.UtcNow + ChallengeWait;
                while (RatingParsers.IsBlocked(200, html) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(true);
                    html = await ReadHtmlAsync(core).ConfigureAwait(true);
                    status = 200;
                }
                return new FetchResponse(status, html);
            }
        }
        finally
        {
            core.NavigationStarting -= OnStarting;
            core.NavigationCompleted -= OnCompleted;
        }
    }

    private static async Task<string> ReadHtmlAsync(CoreWebView2 core)
    {
        var json = await core.ExecuteScriptAsync("document.documentElement ? document.documentElement.outerHTML : ''").ConfigureAwait(true);
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    private async Task<WebView2?> EnsureViewAsync()
    {
        if (_view is not null || _initFailed)
        {
            return _view;
        }
        try
        {
            // A real, rendered window far off-screen: some challenges check visibility and size.
            _host = new Window
            {
                Width = 1024,
                Height = 768,
                Left = -32000,
                Top = -32000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Title = "Vinliste fetcher",
            };
            var view = new WebView2();
            _host.Content = view;
            _host.Show();

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder).ConfigureAwait(true);
            await view.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            view.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            view.CoreWebView2.Settings.IsStatusBarEnabled = false;
            // Images and media are never needed for reading scores; skip them to save time.
            view.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
            view.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
            view.CoreWebView2.WebResourceRequested += (_, e) =>
                e.Response = view.CoreWebView2.Environment.CreateWebResourceResponse(null, 204, "No Content", "");
            _view = view;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _initFailed = true;
            _host?.Close();
            _host = null;
        }
        return _view;
    }

    public void Dispose()
    {
        dispatcher.Invoke(() =>
        {
            _view?.Dispose();
            _host?.Close();
        });
        _gate.Dispose();
    }
}
