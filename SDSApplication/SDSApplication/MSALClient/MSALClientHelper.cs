﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Identity.Client;
using Microsoft.IdentityModel.Abstractions;
using System.Diagnostics;

namespace SDSApplication.MSALClient
{
    /// <summary>
    /// Contains methods that initialize and use the MSAL SDK
    /// </summary>
    public class MSALClientHelper
    {
        private static string PCANotInitializedExceptionMessage = "The PublicClientApplication needs to be initialized before calling this method. Use InitializePublicClientAppAsync() to initialize.";

        /// <summary>
        /// As for the Tenant, you can use a name as obtained from the azure portal, e.g. kko365.onmicrosoft.com"
        /// </summary>
        public AzureADB2CConfig AzureADB2CConfig;

        /// <summary>
        /// Gets the authentication result (if available) from MSAL's various operations.
        /// </summary>
        /// <value>
        /// The authentication result.
        /// </value>
        public AuthenticationResult AuthResult { get; private set; }

        /// <summary>
        /// Gets the MSAL public client application instance.
        /// </summary>
        /// <value>
        /// The public client application.
        /// </value>
        public IPublicClientApplication PublicClientApplication { get; private set; }

        /// <summary>
        /// This will determine if the Interactive Authentication should be Embedded or System view
        /// </summary>
        public bool UseEmbedded { get; set; } = false;

        /// <summary>
        /// The PublicClientApplication builder used internally
        /// </summary>
        private PublicClientApplicationBuilder PublicClientApplicationBuilder;

        /// <summary>
        /// Initializes a new instance of the <see cref="MSALClientHelper"/> class.
        /// </summary>
        public MSALClientHelper(AzureADB2CConfig azureADB2CConfig)
        {
            AzureADB2CConfig = azureADB2CConfig;

            this.InitializePublicClientApplicationBuilder();
        }

        /// <summary>
        /// Initializes the MSAL's PublicClientApplication builder from config.
        /// </summary>
        /// <autogeneratedoc />
        private void InitializePublicClientApplicationBuilder()
        {
            this.PublicClientApplicationBuilder = PublicClientApplicationBuilder.Create(AzureADB2CConfig.ClientId)
                .WithExperimentalFeatures() // this is for upcoming logger
                .WithB2CAuthority($"{AzureADB2CConfig.Instance}/tfp/{AzureADB2CConfig.Domain}/{AzureADB2CConfig.SignUpSignInPolicyid}")
                .WithLogging(new IdentityLogger(EventLogLevel.Warning), enablePiiLogging: false);    // This is the currently recommended way to log MSAL message. For more info refer to https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/wiki/logging. Set Identity Logging level to Warning which is a middle ground
        }

        /// <summary>
        /// Initializes the public client application of MSAL.NET with the required information to correctly authenticate the user.
        /// </summary>
        /// <returns></returns>
        public async Task<IAccount> InitializePublicClientAppAsync()
        {
            // Initialize the MSAL library by building a public client application
            this.PublicClientApplication = this.PublicClientApplicationBuilder
                .WithRedirectUri($"msal{PublicClientSingleton.Instance.MSALClientHelper.AzureADB2CConfig.ClientId}://auth")
                .Build();

            return await FetchSignedInUserFromCache().ConfigureAwait(false);
        }

        /// <summary>
        /// Signs in the user and obtains an Access token for a provided set of scopes
        /// </summary>
        /// <param name="scopes"></param>
        /// <returns> Access Token</returns>
        public async Task<string> SignInUserAndAcquireAccessToken(string[] scopes)
        {
            Exception<NullReferenceException>.ThrowOn(() => this.PublicClientApplication == null, PCANotInitializedExceptionMessage);

            var existingUser = await FetchSignedInUserFromCache().ConfigureAwait(false);

            try
            {
                // 1. Try to sign-in the previously signed-in account
                if (existingUser != null)
                {
                    this.AuthResult = await this.PublicClientApplication
                        .AcquireTokenSilent(scopes, existingUser)
                        .ExecuteAsync()
                        .ConfigureAwait(false);
                }
                else
                {
                    this.AuthResult = await SignInUserInteractivelyAsync(scopes);
                }
            }
            catch (MsalUiRequiredException ex)
            {
                // A MsalUiRequiredException happened on AcquireTokenSilentAsync. This indicates you need to call AcquireTokenInteractive to acquire a token interactively
                Debug.WriteLine($"MsalUiRequiredException: {ex.Message}");

                this.AuthResult = await this.PublicClientApplication
                    .AcquireTokenInteractive(scopes)
                    .ExecuteAsync()
                    .ConfigureAwait(false);
            }
            catch (MsalException msalEx)
            {
                Debug.WriteLine($"Error Acquiring Token interactively:{Environment.NewLine}{msalEx}");
            }

            return this.AuthResult.AccessToken;
        }

        /// <summary>
        /// Signs the in user and acquire access token for a provided set of scopes.
        /// </summary>
        /// <param name="scopes">The scopes.</param>
        /// <param name="extraclaims">The extra claims, usually from CAE. We basically handle CAE by sending the user back to Azure AD for
        /// additional processing and requesting a new access token for Graph</param>
        /// <returns></returns>
        public async Task<String> SignInUserAndAcquireAccessToken(string[] scopes, string extraclaims)
        {
            Exception<NullReferenceException>.ThrowOn(() => this.PublicClientApplication == null, PCANotInitializedExceptionMessage);

            try
            {
                // Send the user to Azure AD for re-authentication as a silent acquisition wont resolve any CAE scenarios like an extra claims request
                this.AuthResult = await this.PublicClientApplication.AcquireTokenInteractive(scopes)
                        .WithClaims(extraclaims)
                        .ExecuteAsync()
                        .ConfigureAwait(false);
            }
            catch (MsalException msalEx)
            {
                Debug.WriteLine($"Error Acquiring Token:{Environment.NewLine}{msalEx}");
            }

            return this.AuthResult.AccessToken;
        }

        /// <summary>
        /// Shows a pattern to sign-in a user interactively in applications that are input constrained and would need to fall-back on device code flow.
        /// </summary>
        /// <param name="scopes">The scopes.</param>
        /// <param name="existingAccount">The existing account.</param>
        /// <returns></returns>
        public async Task<AuthenticationResult> SignInUserInteractivelyAsync(string[] scopes, IAccount existingAccount = null)
        {

            Exception<NullReferenceException>.ThrowOn(() => this.PublicClientApplication == null, PCANotInitializedExceptionMessage);

            if (this.PublicClientApplication == null)
                throw new NullReferenceException();

            if (this.PublicClientApplication.IsUserInteractive())
            {
                return await this.PublicClientApplication.AcquireTokenInteractive(scopes)
                    .WithParentActivityOrWindow(PlatformConfig.Instance.ParentWindow)
                    .ExecuteAsync()
                    .ConfigureAwait(false);
            }

            // If the operating system does not have UI (e.g. SSH into Linux), you can fallback to device code, however this
            // flow will not satisfy the "device is managed" CA policy.
            return await this.PublicClientApplication.AcquireTokenWithDeviceCode(scopes, (dcr) =>
            {
                Console.WriteLine(dcr.Message);
                return Task.CompletedTask;
            }).ExecuteAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Removes the first signed-in user's record from token cache
        /// </summary>
        public async Task SignOutUserAsync()
        {
            var existingUser = await FetchSignedInUserFromCache().ConfigureAwait(false);
            await this.SignOutUserAsync(existingUser).ConfigureAwait(false);
        }

        /// <summary>
        /// Removes a given user's record from token cache
        /// </summary>
        /// <param name="user">The user.</param>
        public async Task SignOutUserAsync(IAccount user)
        {
            if (this.PublicClientApplication == null) return;

            await this.PublicClientApplication.RemoveAsync(user).ConfigureAwait(false);
        }

        /// <summary>
        /// Fetches the signed in user from MSAL's token cache (if available).
        /// </summary>
        /// <returns></returns>
        public async Task<IAccount> FetchSignedInUserFromCache()
        {
            Exception<NullReferenceException>.ThrowOn(() => this.PublicClientApplication == null, PCANotInitializedExceptionMessage);

            // get accounts from cache
            IEnumerable<IAccount> accounts = await this.PublicClientApplication.GetAccountsAsync();

            // Error corner case: we should always have 0 or 1 accounts, not expecting > 1
            // This is just an example of how to resolve this ambiguity, which can arise if more apps share a token cache.
            // Note that some apps prefer to use a random account from the cache.
            if (accounts.Count() > 1)
            {
                foreach (var acc in accounts)
                {
                    await this.PublicClientApplication.RemoveAsync(acc);
                }

                return null;
            }

            return accounts.SingleOrDefault();
        }
    }
}