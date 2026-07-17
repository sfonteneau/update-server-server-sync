// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.UpdateServices.WebServices.DssAuthentication;
using Microsoft.UpdateServices.WebServices.ServerSync;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Source
{
    /// <summary>
    /// Implements authentication with an upstream update server.
    /// <para>
    /// Use the ClientAuthenticator to obtain an access token for accessing metadata and content on an upstream update server.
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// var authenticator = new ClientAuthenticator(Endpoint.Default);
    /// var accessToken = await authenticator.Authenticate();
    /// </code>
    /// </example>
    class ClientAuthenticator
    {
        private const int TransientRequestAttempts = 5;

        private readonly TimeSpan RequestTimeout;
        /// <summary>
        /// Gets the update server endpoint this instance of ClientAuthenticator authenticates with.
        /// </summary>
        public readonly Endpoint UpstreamEndpoint;

        /// <summary>
        /// Initializes a new instance of the ClientAuthenticator class to authenticate with the specified endpoint.
        /// </summary>
        /// <param name="endpoint">The endpoint to authenticate with.</param>
        public ClientAuthenticator(Endpoint endpoint)
            : this(endpoint, new Guid().ToString(), new Guid(), UpstreamServerClient.DefaultRequestTimeout)
        {
        }

        /// <summary>
        /// Initializes a new instance of the ClientAuthenticator that authenticates with the official
        /// Microsoft upstream update server.
        /// </summary>
        public ClientAuthenticator()
            : this(Endpoint.Default, new Guid().ToString(), new Guid(), UpstreamServerClient.DefaultRequestTimeout)
        {
        }

        /// <summary>
        /// Account name used when authenticating. If null, a random GUID string is used.
        /// </summary>
        private readonly string AccountName = null;

        /// <summary>
        /// Account GUID used for authenticating. If null, a random GUID is used
        /// </summary>
        private readonly Guid? AccountGuid = null;

        /// <summary>
        /// Initializes a new instance of the ClientAuthenticator class to authenticate with the specified endpoint, using
        /// specified credentials.
        /// </summary>
        /// <param name="endpoint">The endpoint to authenticate with.</param>
        /// <param name="accountName">Account name.</param>
        /// <param name="accountGuid">Account GUID.</param>
        public ClientAuthenticator(Endpoint endpoint, string accountName, Guid accountGuid)
            : this(endpoint, accountName, accountGuid, UpstreamServerClient.DefaultRequestTimeout)
        {
        }

        public ClientAuthenticator(Endpoint endpoint, string accountName, Guid accountGuid, TimeSpan requestTimeout)
        {
            if (requestTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(requestTimeout), "The upstream request timeout must be greater than zero.");
            }

            UpstreamEndpoint = endpoint;
            AccountGuid = accountGuid;
            RequestTimeout = requestTimeout;

            if (!string.IsNullOrEmpty(accountName))
            {
                AccountName = accountName;
            }
            else
            {
                AccountName = new Guid().ToString();
            }
        }

        private System.ServiceModel.BasicHttpBinding CreateHttpBinding()
        {
            return new System.ServiceModel.BasicHttpBinding()
            {
                OpenTimeout = RequestTimeout,
                CloseTimeout = RequestTimeout,
                ReceiveTimeout = RequestTimeout,
                SendTimeout = RequestTimeout
            };
        }

        /// <summary>
        /// Retries transient HTTP/WCF transport failures. A fresh generated WCF client is created by
        /// the supplied operation on every attempt because a client whose channel faulted cannot be
        /// relied on for the next request.
        /// </summary>
        private static async Task<T> ExecuteWithTransientRetry<T>(Func<Task<T>> operation, string operationName)
        {
            Exception lastException = null;

            for (var attempt = 1; attempt <= TransientRequestAttempts; attempt++)
            {
                try
                {
                    return await operation();
                }
                catch (Exception ex) when (IsTransientTransportFailure(ex))
                {
                    lastException = ex;
                    if (attempt == TransientRequestAttempts)
                    {
                        break;
                    }

                    var delay = TimeSpan.FromSeconds(1 << (attempt - 1));
                    System.Diagnostics.Trace.TraceWarning(
                        $"Transient upstream failure during {operationName}; " +
                        $"retrying in {delay.TotalSeconds:0} second(s) " +
                        $"(attempt {attempt}/{TransientRequestAttempts}): " +
                        ex.GetBaseException().Message);
                    await Task.Delay(delay);
                }
            }

            throw new System.ServiceModel.CommunicationException(
                $"{operationName} failed after {TransientRequestAttempts} transient attempt(s).",
                lastException);
        }

        private static bool IsTransientTransportFailure(Exception exception)
        {
            // SOAP faults are valid server responses and must keep their existing semantic handling.
            if (ContainsSoapFault(exception))
            {
                return false;
            }

            if (exception is TimeoutException ||
                exception is System.ServiceModel.CommunicationException ||
                exception is HttpRequestException ||
                exception is IOException)
            {
                return true;
            }

            return exception.InnerException != null &&
                !ReferenceEquals(exception.InnerException, exception) &&
                IsTransientTransportFailure(exception.InnerException);
        }

        private static bool ContainsSoapFault(Exception exception)
        {
            if (exception is System.ServiceModel.FaultException)
            {
                return true;
            }

            return exception.InnerException != null &&
                !ReferenceEquals(exception.InnerException, exception) &&
                ContainsSoapFault(exception.InnerException);
        }

        /// <summary>
        /// Performs authentication with an upstream update server, using a previously issued service access token.
        /// </summary>
        /// <remarks>
        /// Refreshing an old token with this method is faster than obtaining a new token as it requires fewer server roundtrips.
        /// 
        /// If the access cookie does not expire within 30 minutes, the function succeeds and the old token is returned.
        /// </remarks>
        /// <param name="cachedAccessToken">The previously issued access token.</param>
        /// <returns>The new ServiceAccessToken</returns>
        public async Task<ServiceAccessToken> Authenticate(ServiceAccessToken cachedAccessToken)
        {
            if (cachedAccessToken == null)
            {
                return await Authenticate();
            }

            ServiceAccessToken newAccessToken = new()
            {
                AuthCookie = cachedAccessToken.AuthCookie,
                AccessCookie = cachedAccessToken.AccessCookie,
                AuthenticationInfo = cachedAccessToken.AuthenticationInfo
            };

            // Check if the cached access cookie expires in the next 30 minutes; if not, return the new token
            // with this cookie
            if (!newAccessToken.ExpiresIn(TimeSpan.FromMinutes(30)))
            {
                return newAccessToken;
            }

            bool restartAuthenticationRequired = false;

            // Get a new access cookie
            try
            {
                newAccessToken.AccessCookie = await GetServerAccessCookie(newAccessToken.AuthCookie);
            }
            catch (UpstreamServerException ex)
            {
                if (ex.ErrorCode == UpstreamServerErrorCode.InvalidAuthorizationCookie)
                {
                    // The authorization cookie is expired or invalid. Restart the authentication protocol
                    restartAuthenticationRequired = true;
                }
                else
                {
                    throw ex;
                }
            }

            return restartAuthenticationRequired ? await Authenticate() : newAccessToken;
        }

        /// <summary>
        /// Performs authentication with an upstream update service.
        /// </summary>
        /// <returns>A new access token.</returns>
        public async Task<ServiceAccessToken> Authenticate()
        {
            ServiceAccessToken newAccessToken = new();

            newAccessToken.AuthenticationInfo = (await GetAuthenticationInfo()).ToList();
            newAccessToken.AuthCookie = await GetAuthorizationCookie(newAccessToken.AuthenticationInfo[0]);
            newAccessToken.AccessCookie = await GetServerAccessCookie(newAccessToken.AuthCookie);

            return newAccessToken;
        }

        /// <summary>
        /// Retrieves authentication information from a WSUS server.
        /// </summary>
        /// <returns>List of supported authentication methods</returns>
        private async Task<AuthPlugInInfo[]> GetAuthenticationInfo()
        {
            var upstreamEndpoint = new System.ServiceModel.EndpointAddress(UpstreamEndpoint.ServerSyncURI);
            var authConfigResponse = await ExecuteWithTransientRetry(
                async () =>
                {
                    var httpBinding = CreateHttpBinding();
                    if (upstreamEndpoint.Uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                    {
                        httpBinding.Security.Mode = System.ServiceModel.BasicHttpSecurityMode.Transport;
                    }

                    // Create a fresh client for every attempt. WCF channels may remain faulted after
                    // a premature HTTP response and cannot safely be reused.
                    IServerSyncWebService serverSyncClient =
                        new ServerSyncWebServiceClient(httpBinding, upstreamEndpoint);
                    return await serverSyncClient.GetAuthConfigAsync(new GetAuthConfigRequest());
                },
                "upstream authentication configuration request");

            if (authConfigResponse == null)
            {
                throw new Exception("Authentication config response was null.");
            }
            else if (authConfigResponse.GetAuthConfigResponse1.GetAuthConfigResult.AuthInfo == null)
            {
                throw new Exception("Authentication config payload was null.");
            }

            return authConfigResponse.GetAuthConfigResponse1.GetAuthConfigResult.AuthInfo;
        }

        /// <summary>
        /// Retrieves an authentication cookie from a DSS service.
        /// </summary>
        /// <returns>An authentication cookie</returns>
        private async Task<UpdateServices.WebServices.DssAuthentication.AuthorizationCookie> GetAuthorizationCookie(AuthPlugInInfo authInfo)
        {
            var upstreamEndpoint = new System.ServiceModel.EndpointAddress(UpstreamEndpoint.GetAuthenticationEndpointFromRelativeUrl(authInfo.ServiceUrl));

            // Issue the request. All accounts are allowed, so we just generate a random account guid and name
            var cookieRequest = new GetAuthorizationCookieRequest
            {
                GetAuthorizationCookie = new GetAuthorizationCookieRequestBody
                {
                    accountGuid = AccountName,
                    accountName = AccountGuid.ToString()
                }
            };

            var getAuthCookieResponse = await ExecuteWithTransientRetry(
                async () =>
                {
                    var httpBinding = CreateHttpBinding();
                    if (upstreamEndpoint.Uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                    {
                        httpBinding.Security.Mode = System.ServiceModel.BasicHttpSecurityMode.Transport;
                    }

                    IDSSAuthWebService authenticationService =
                        new DSSAuthWebServiceClient(httpBinding, upstreamEndpoint);
                    return await authenticationService.GetAuthorizationCookieAsync(cookieRequest);
                },
                "upstream authorization-cookie request");

            if (getAuthCookieResponse == null ||
                getAuthCookieResponse.GetAuthorizationCookieResponse1.GetAuthorizationCookieResult.CookieData == null)
            {
                throw new Exception("Failed to get authorization token. Response or cookie is null.");
            }

            return getAuthCookieResponse.GetAuthorizationCookieResponse1.GetAuthorizationCookieResult;
        }

        /// <summary>
        /// Retrieves a server access cookie based on an authentication cookie.
        /// </summary>
        /// <param name="authCookie">The auth cookie to use when requesting the access cookie</param>
        /// <returns>An access cookie</returns>
        private async Task<Cookie> GetServerAccessCookie(UpdateServices.WebServices.DssAuthentication.AuthorizationCookie authCookie)
        {
            var upstreamEndpoint = new System.ServiceModel.EndpointAddress(UpstreamEndpoint.ServerSyncURI);

            // Create an access cookie request using the authentication cookie parameter.
            var cookieRequest = new GetCookieRequest
            {
                GetCookie = new GetCookieRequestBody()
                {
                    authCookies = new UpdateServices.WebServices.ServerSync.AuthorizationCookie[] 
                    { 
                        new UpdateServices.WebServices.ServerSync.AuthorizationCookie()
                        {
                            CookieData = authCookie.CookieData,
                            PlugInId = authCookie.PlugInId
                        }
                    },
                    oldCookie = null,
                    protocolVersion = "1.7"
                }
            };

            cookieRequest.GetCookie.authCookies = new UpdateServices.WebServices.ServerSync.AuthorizationCookie[] { new UpdateServices.WebServices.ServerSync.AuthorizationCookie() };
            cookieRequest.GetCookie.authCookies[0].CookieData = authCookie.CookieData;
            cookieRequest.GetCookie.authCookies[0].PlugInId = authCookie.PlugInId;
            cookieRequest.GetCookie.oldCookie = null;
            cookieRequest.GetCookie.protocolVersion = "1.7";

            GetCookieResponse cookieResponse;
            try
            {
                cookieResponse = await ExecuteWithTransientRetry(
                    async () =>
                    {
                        var httpBinding = CreateHttpBinding();
                        if (upstreamEndpoint.Uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                        {
                            httpBinding.Security.Mode = System.ServiceModel.BasicHttpSecurityMode.Transport;
                        }

                        IServerSyncWebService serverSyncClient =
                            new ServerSyncWebServiceClient(httpBinding, upstreamEndpoint);
                        return await serverSyncClient.GetCookieAsync(cookieRequest);
                    },
                    "upstream access-cookie request");
            }
            catch (System.ServiceModel.FaultException ex)
            {
                throw new UpstreamServerException(ex);
            }

            if (cookieResponse == null ||
                cookieResponse.GetCookieResponse1.GetCookieResult.EncryptedData == null)
            {
                throw new Exception("Failed to get access cookie. Response or cookie is null.");
            }

            return cookieResponse.GetCookieResponse1.GetCookieResult;
        }
    }
}
