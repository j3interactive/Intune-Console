using System.Windows;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace IntuneConsole.App.Auth;

/// <summary>Signs the admin in with their own Entra account through the Windows account broker (WAM).
/// No client secret: the app registration is a public client, and Graph calls are delegated, so the
/// admin's Intune role decides what they can do.</summary>
public sealed class MsalAuth
{
    private const string Graph = "https://graph.microsoft.com/";

    /// <summary>".default" asks for every Graph permission an admin has consented to on the app
    /// registration. New permissions (added in later versions) work as soon as consent is granted,
    /// with no change here and no per-user consent prompts. See README for the list.</summary>
    public static readonly string[] Scopes = [Graph + ".default"];

    private readonly IPublicClientApplication _app;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAccount? _account;

    public string? UserName => _account?.Username;

    public MsalAuth(string tenantId, string clientId, IntPtr parentWindow)
    {
        _app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .WithDefaultRedirectUri()
            .WithParentActivityOrWindow(() => parentWindow)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
            {
                Title = "Intune Troubleshooting Console",
            })
            .Build();
    }

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AuthenticationResult result;
            var account = _account ?? (await _app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault()
                          ?? PublicClientApplication.OperatingSystemAccount;
            try
            {
                result = await _app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
            }
            catch (MsalUiRequiredException)
            {
                // Interactive sign-in has to run on the UI thread.
                result = await Application.Current.Dispatcher.InvokeAsync(() =>
                    _app.AcquireTokenInteractive(Scopes)
                        .WithPrompt(Prompt.SelectAccount)
                        .ExecuteAsync(ct)).Task.Unwrap().ConfigureAwait(false);
            }
            _account = result.Account;
            return result.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync()
    {
        foreach (var a in await _app.GetAccountsAsync())
            await _app.RemoveAsync(a);
        _account = null;
    }
}
