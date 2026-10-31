using System.Runtime.InteropServices;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;

namespace PowerToysExtensions.Core;

/// <summary>
/// Microsoft sign-in through the Windows account broker (WAM): single sign-on with the work account that
/// is signed in to Windows, MFA in the Windows dialog, no browser. Tokens are cached encrypted (DPAPI).
/// The app registration needs the redirect URI ms-appx-web://microsoft.aad.brokerplugin/{client id}
/// (platform "Mobile and desktop applications").
/// </summary>
public sealed partial class MicrosoftAuth(string name, Func<string> tenantId, Func<string> clientId, string[] scopes)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPublicClientApplication? _app;
    private string _key = string.Empty;

    public static string RedirectUri(string clientId) => $"ms-appx-web://microsoft.aad.brokerplugin/{clientId}";

    public bool Configured => tenantId().Trim().Length > 0 && clientId().Trim().Length > 0;

    public async Task<string> Token(CancellationToken token = default)
    {
        if (!Configured)
        {
            throw new InvalidOperationException("Enter Tenant ID and Client ID in the settings of this extension");
        }

        await _gate.WaitAsync(token);
        try
        {
            var app = await App();
            var account = (await app.GetAccountsAsync()).FirstOrDefault() ?? PublicClientApplication.OperatingSystemAccount;
            try
            {
                return (await app.AcquireTokenSilent(scopes, account).ExecuteAsync(token)).AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // first sign-in, consent or MFA: the Windows sign-in dialog
                return (await app.AcquireTokenInteractive(scopes).WithAccount(account).ExecuteAsync(token)).AccessToken;
            }
        }
        catch (MsalException ex)
        {
            throw new InvalidOperationException(ex.ErrorCode == "user_canceled" || ex.ErrorCode == "authentication_canceled"
                ? "Sign-in cancelled"
                : $"Microsoft sign-in failed: {ex.Message}{(ex.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase) ? $" – add the redirect URI {RedirectUri(clientId().Trim())} to the app registration" : string.Empty)}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOut()
    {
        var app = await App();
        foreach (var account in await app.GetAccountsAsync())
        {
            await app.RemoveAsync(account);
        }
    }

    private async Task<IPublicClientApplication> App()
    {
        var key = $"{tenantId().Trim()}|{clientId().Trim()}";
        if (_app is not null && key == _key)
        {
            return _app;
        }

        var app = PublicClientApplicationBuilder.Create(clientId().Trim())
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId().Trim())
            .WithRedirectUri(RedirectUri(clientId().Trim()))
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "PowerToys Extensions" })
            .WithParentActivityOrWindow(GetForegroundWindow)
            .Build();
        var storage = new StorageCreationPropertiesBuilder($"msal-{name}.cache", Paths.For("auth")).Build();
        (await MsalCacheHelper.CreateAsync(storage)).RegisterCache(app.UserTokenCache);
        (_app, _key) = (app, key);
        return app;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
}
