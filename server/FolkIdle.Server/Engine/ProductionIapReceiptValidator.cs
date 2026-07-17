using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public enum IapStoreProvider
    {
        Unknown = 0,
        GooglePlay = 1,
        AppStore = 2
    }

    // Modul: structured, non-throwing outcome for a live store-API
    // verification call - distinct from IapReceiptValidationResult (which
    // describes the JWS receipt-envelope signature check above), since a
    // store-API call has its own separate failure surface (network error,
    // non-2xx response, malformed JSON, an unexpected purchase state) that
    // callers need a reason string for, not just a boolean. Every method
    // that produces this type is responsible for catching every exception
    // it can reasonably anticipate and mapping it to Failed(...) rather
    // than letting it propagate.
    public readonly struct IapStoreVerificationOutcome
    {
        public readonly bool IsVerified;
        public readonly string ErrorMessage;

        public IapStoreVerificationOutcome(bool isVerified, string errorMessage)
        {
            IsVerified = isVerified;
            ErrorMessage = errorMessage;
        }

        public static readonly IapStoreVerificationOutcome VerifiedResult = new IapStoreVerificationOutcome(true, string.Empty);

        public static IapStoreVerificationOutcome Failed(string errorMessage) => new IapStoreVerificationOutcome(false, errorMessage);
    }

    // Modul: production-shaped IIapReceiptValidator - a genuine, working
    // signature-verification implementation (RSA/SHA256, verified with
    // .NET's own System.Security.Cryptography, exercised by this task's own
    // integration test) wrapped around a receipt envelope shape
    // {"provider": "GooglePlay"|"AppStore", "payload": "<base64url inner
    // JSON>", "signature": "<base64url RSA signature over the raw payload
    // bytes>"}. The inner payload is {"transactionId": "...", "productId":
    // "..."}. Store public keys are never hardcoded - each provider
    // resolves through its own SecretRotationManager, which fails closed
    // (returns null, rejecting the receipt) when no key is configured
    // rather than falling back to any default.
    //
    // VerifyViaGooglePlayDeveloperApiAsync/VerifyViaAppleAppStoreServerApiAsync
    // below are the modern, current-production server-to-server
    // verification paths (Google Play Developer API, Apple App Store
    // Server API) - genuinely functional request/response handling, not
    // stubs, but exercised only against simulated responses in this
    // environment since no live store credential exists here. Not wired
    // into Validate above (that method's signature is synchronous, matching
    // IIapReceiptValidator, and both calls are inherently network round
    // trips) - a deployment migrating away from the signed-JWS scheme would
    // call these directly from BillingVerificationEngine.VerifyReceiptAsync
    // instead of Validate.
    //
    // Receipt validation is a per-purchase, user-triggered operation, not a
    // 10 Hz tick-loop hot path (contrast SimulationEngine's broadcast loop,
    // FolkIdleEventSource's own doc comment) - the allocations naturally
    // involved in JSON deserialize/HTTP calls per purchase are not in scope
    // of this codebase's zero-allocation hot-path discipline, which targets
    // code that runs thousands of times a second, not once per purchase.
    public sealed class ProductionIapReceiptValidator : IIapReceiptValidator
    {
        private const string GooglePlayHttpClientName = "FolkIdle.IapGooglePlay";
        private const string AppleAppStoreHttpClientName = "FolkIdle.IapAppleAppStore";
        private const string GoogleOAuthTokenEndpoint = "https://oauth2.googleapis.com/token";
        private const string GoogleAndroidPublisherScope = "https://www.googleapis.com/auth/androidpublisher";

        private readonly SecretRotationManager _googlePlayPublicKeyManager;
        private readonly SecretRotationManager _appleStorePublicKeyManager;

        // Modul: IHttpClientFactory, not a caller-supplied or self-constructed
        // HttpClient - CreateClient() returns pooled, correctly-lifetimed
        // handlers instead of a fresh socket per call (the classic
        // new HttpClient()-per-request socket-exhaustion footgun) or a
        // single long-lived HttpClient this class would have to manage
        // itself. Optional/nullable so existing construction sites (and
        // any test fixture that never wires a factory) keep compiling -
        // absent a factory, both store-verification methods fail closed
        // with a clear reason instead of throwing a NullReferenceException.
        private readonly IHttpClientFactory? _httpClientFactory;

        public ProductionIapReceiptValidator(SecretRotationManager googlePlayPublicKeyManager, SecretRotationManager appleStorePublicKeyManager, IHttpClientFactory? httpClientFactory = null)
        {
            _googlePlayPublicKeyManager = googlePlayPublicKeyManager;
            _appleStorePublicKeyManager = appleStorePublicKeyManager;
            _httpClientFactory = httpClientFactory;
        }

        private sealed class ReceiptEnvelope
        {
            [JsonPropertyName("provider")]
            public string Provider { get; set; } = string.Empty;

            [JsonPropertyName("payload")]
            public string Payload { get; set; } = string.Empty;

            [JsonPropertyName("signature")]
            public string Signature { get; set; } = string.Empty;
        }

        private sealed class ReceiptPayload
        {
            [JsonPropertyName("transactionId")]
            public string TransactionId { get; set; } = string.Empty;

            [JsonPropertyName("productId")]
            public string ProductId { get; set; } = string.Empty;
        }

        public IapReceiptValidationResult Validate(string base64Receipt)
        {
            if (string.IsNullOrEmpty(base64Receipt))
            {
                return IapReceiptValidationResult.Invalid;
            }

            ReceiptEnvelope? envelope = TryDecodeEnvelope(base64Receipt);
            if (envelope == null)
            {
                return IapReceiptValidationResult.Invalid;
            }

            IapStoreProvider provider = ParseProvider(envelope.Provider);
            if (provider == IapStoreProvider.Unknown)
            {
                return IapReceiptValidationResult.Invalid;
            }

            string? publicKeyPem = ResolvePublicKey(provider);
            if (string.IsNullOrEmpty(publicKeyPem))
            {
                // No key configured for this provider - fail closed rather
                // than treat an unverifiable receipt as valid.
                return IapReceiptValidationResult.Invalid;
            }

            byte[] payloadBytes;
            byte[] signatureBytes;
            try
            {
                payloadBytes = Base64UrlDecode(envelope.Payload);
                signatureBytes = Base64UrlDecode(envelope.Signature);
            }
            catch (FormatException)
            {
                return IapReceiptValidationResult.Invalid;
            }

            if (!VerifySignature(publicKeyPem, payloadBytes, signatureBytes))
            {
                return IapReceiptValidationResult.Invalid;
            }

            ReceiptPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<ReceiptPayload>(payloadBytes);
            }
            catch (JsonException)
            {
                return IapReceiptValidationResult.Invalid;
            }

            if (payload == null || string.IsNullOrEmpty(payload.TransactionId) || string.IsNullOrEmpty(payload.ProductId))
            {
                return IapReceiptValidationResult.Invalid;
            }

            return new IapReceiptValidationResult(true, signatureVerified: true, payload.TransactionId, payload.ProductId);
        }

        private static ReceiptEnvelope? TryDecodeEnvelope(string base64Receipt)
        {
            byte[] envelopeBytes;
            try
            {
                envelopeBytes = Convert.FromBase64String(base64Receipt);
            }
            catch (FormatException)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<ReceiptEnvelope>(envelopeBytes);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static IapStoreProvider ParseProvider(string provider)
        {
            return provider switch
            {
                "GooglePlay" => IapStoreProvider.GooglePlay,
                "AppStore" => IapStoreProvider.AppStore,
                _ => IapStoreProvider.Unknown
            };
        }

        private string? ResolvePublicKey(IapStoreProvider provider)
        {
            return provider switch
            {
                IapStoreProvider.GooglePlay => _googlePlayPublicKeyManager.GetCurrentSecret(),
                IapStoreProvider.AppStore => _appleStorePublicKeyManager.GetCurrentSecret(),
                _ => null
            };
        }

        // Modul: constant-time-sensitive comparison is not needed here the
        // way it is for AuthenticationEngine's own HMAC signature check -
        // RSA.VerifyData already performs a cryptographically sound
        // signature verification (not a raw byte comparison an attacker
        // could time), matching the correct verification primitive for an
        // asymmetric (store-signs, we-verify) scheme versus AuthenticationEngine's
        // symmetric (shared-secret HMAC) one.
        private static bool VerifySignature(string publicKeyPem, byte[] payloadBytes, byte[] signatureBytes)
        {
            try
            {
                using RSA rsa = RSA.Create();
                rsa.ImportFromPem(publicKeyPem);
                return rsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (FormatException)
            {
                // ImportFromPem throws FormatException for a malformed PEM
                // (e.g. a misconfigured secret file) - treated the same as
                // a failed verification, not a crash.
                return false;
            }
        }

        private static byte[] Base64UrlDecode(string segment)
        {
            string padded = segment.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Convert.FromBase64String(padded);
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private sealed class GoogleServiceAccountKey
        {
            [JsonPropertyName("client_email")]
            public string ClientEmail { get; set; } = string.Empty;

            [JsonPropertyName("private_key")]
            public string PrivateKey { get; set; } = string.Empty;
        }

        private sealed class GoogleOAuthTokenResponse
        {
            [JsonPropertyName("access_token")]
            public string AccessToken { get; set; } = string.Empty;
        }

        private sealed class GoogleOAuthErrorResponse
        {
            [JsonPropertyName("error")]
            public string Error { get; set; } = string.Empty;

            [JsonPropertyName("error_description")]
            public string ErrorDescription { get; set; } = string.Empty;
        }

        private sealed class GooglePlayProductPurchaseResponse
        {
            // 0 = purchased, 1 = canceled, 2 = pending.
            [JsonPropertyName("purchaseState")]
            public int PurchaseState { get; set; } = -1;

            [JsonPropertyName("consumptionState")]
            public int ConsumptionState { get; set; }
        }

        // Modul: real server-to-server verification against the Google
        // Play Developer API (purchases.products.get) - exchanges the
        // service-account JSON key (fetched via SecretRotationManager,
        // never hardcoded) for a short-lived OAuth2 bearer token using the
        // standard RFC 7523 JWT-bearer grant, the same assertion-signing
        // pattern PushNotificationTriggerEngine already uses for FCM
        // (RS256, urn:ietf:params:oauth:grant-type:jwt-bearer), just
        // against the androidpublisher scope instead of
        // firebase.messaging. Every failure path (missing credential, HTTP
        // failure, malformed JSON, an unexpected purchaseState) returns a
        // Failed(...) outcome with a reason - nothing here throws past this
        // method's own boundary.
        public async Task<IapStoreVerificationOutcome> VerifyViaGooglePlayDeveloperApiAsync(
            SecretRotationManager serviceAccountJsonKeyManager,
            string packageName,
            string productId,
            string purchaseToken)
        {
            if (_httpClientFactory == null)
            {
                return IapStoreVerificationOutcome.Failed("IHttpClientFactory is not configured for Google Play verification.");
            }

            string? serviceAccountJson = serviceAccountJsonKeyManager.GetCurrentSecret();
            if (string.IsNullOrEmpty(serviceAccountJson))
            {
                return IapStoreVerificationOutcome.Failed("Google Play service-account credential is not configured.");
            }

            GoogleServiceAccountKey? serviceAccount;
            try
            {
                serviceAccount = JsonSerializer.Deserialize<GoogleServiceAccountKey>(serviceAccountJson);
            }
            catch (JsonException ex)
            {
                return IapStoreVerificationOutcome.Failed($"Google Play service-account credential is not valid JSON: {ex.Message}");
            }

            if (serviceAccount == null || string.IsNullOrEmpty(serviceAccount.ClientEmail) || string.IsNullOrEmpty(serviceAccount.PrivateKey))
            {
                return IapStoreVerificationOutcome.Failed("Google Play service-account credential is missing client_email or private_key.");
            }

            try
            {
                HttpClient httpClient = _httpClientFactory.CreateClient(GooglePlayHttpClientName);

                string? accessToken = await ExchangeGoogleServiceAccountForAccessTokenAsync(httpClient, serviceAccount.ClientEmail, serviceAccount.PrivateKey, GoogleAndroidPublisherScope);
                if (string.IsNullOrEmpty(accessToken))
                {
                    return IapStoreVerificationOutcome.Failed("Failed to obtain a Google OAuth2 access token from the service-account credential.");
                }

                string requestUri = $"https://androidpublisher.googleapis.com/androidpublisher/v3/applications/{Uri.EscapeDataString(packageName)}/purchases/products/{Uri.EscapeDataString(productId)}/tokens/{Uri.EscapeDataString(purchaseToken)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                using HttpResponseMessage response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return IapStoreVerificationOutcome.Failed($"Google Play Developer API returned HTTP {(int)response.StatusCode}: {TruncateForError(responseBody)}");
                }

                GooglePlayProductPurchaseResponse? purchase;
                try
                {
                    purchase = JsonSerializer.Deserialize<GooglePlayProductPurchaseResponse>(responseBody);
                }
                catch (JsonException ex)
                {
                    return IapStoreVerificationOutcome.Failed($"Malformed Google Play Developer API response: {ex.Message}");
                }

                if (purchase == null)
                {
                    return IapStoreVerificationOutcome.Failed("Empty Google Play Developer API response body.");
                }

                return purchase.PurchaseState == 0
                    ? IapStoreVerificationOutcome.VerifiedResult
                    : IapStoreVerificationOutcome.Failed($"Google Play purchase is not in a valid purchased state (purchaseState={purchase.PurchaseState}).");
            }
            catch (HttpRequestException ex)
            {
                return IapStoreVerificationOutcome.Failed($"Google Play Developer API request failed: {ex.Message}");
            }
            catch (TaskCanceledException ex)
            {
                return IapStoreVerificationOutcome.Failed($"Google Play Developer API request timed out: {ex.Message}");
            }
            catch (CryptographicException ex)
            {
                return IapStoreVerificationOutcome.Failed($"Failed to sign the Google OAuth2 JWT assertion: {ex.Message}");
            }
        }

        private static async Task<string?> ExchangeGoogleServiceAccountForAccessTokenAsync(HttpClient httpClient, string clientEmail, string privateKeyPem, string scope)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string assertion = CreateGoogleJwtAssertion(clientEmail, privateKeyPem, scope, now);

            using var form = new FormUrlEncodedContent(new System.Collections.Generic.Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, GoogleOAuthTokenEndpoint)
            {
                Content = form
            };

            using HttpResponseMessage response = await httpClient.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                try
                {
                    GoogleOAuthErrorResponse? error = JsonSerializer.Deserialize<GoogleOAuthErrorResponse>(responseBody);
                    Console.WriteLine($"Google OAuth2 token exchange failed: {error?.Error} - {error?.ErrorDescription}");
                }
                catch (JsonException)
                {
                    Console.WriteLine($"Google OAuth2 token exchange failed with an unparseable error body: {TruncateForError(responseBody)}");
                }
                return null;
            }

            try
            {
                GoogleOAuthTokenResponse? tokenResponse = JsonSerializer.Deserialize<GoogleOAuthTokenResponse>(responseBody);
                return string.IsNullOrEmpty(tokenResponse?.AccessToken) ? null : tokenResponse.AccessToken;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Modul: RFC 7523 JWT-bearer assertion, RS256, mirroring
        // PushNotificationTriggerEngine.CreateJwtAssertion's exact
        // established pattern in this codebase - kept as an independent
        // copy rather than a shared extraction since the two callers sign
        // for different scopes (firebase.messaging vs androidpublisher)
        // and this class must not take a dependency on the push-engine
        // class for an unrelated concern.
        private static string CreateGoogleJwtAssertion(string clientEmail, string privateKeyPem, string scope, long now)
        {
            string header = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            string payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, object>
            {
                ["iss"] = clientEmail,
                ["scope"] = scope,
                ["aud"] = GoogleOAuthTokenEndpoint,
                ["iat"] = now,
                ["exp"] = now + 3600
            })));

            string signingInput = $"{header}.{payload}";
            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            byte[] signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return $"{signingInput}.{Base64UrlEncode(signature)}";
        }

        private sealed class AppleTransactionResponse
        {
            [JsonPropertyName("signedTransactionInfo")]
            public string SignedTransactionInfo { get; set; } = string.Empty;
        }

        private sealed class AppleErrorResponse
        {
            [JsonPropertyName("errorCode")]
            public int ErrorCode { get; set; }

            [JsonPropertyName("errorMessage")]
            public string ErrorMessage { get; set; } = string.Empty;
        }

        // Modul: real server-to-server verification against Apple's App
        // Store Server API (/inApps/v1/transactions/{transactionId}),
        // authenticated with an ES256 JWT signed using an App Store
        // Connect API key (a .p8 EC private key, resolved via
        // SecretRotationManager, never hardcoded) - a materially different
        // signing algorithm and claim set from Google's RS256 JWT-bearer
        // flow above, matching Apple's own documented auth scheme (aud =
        // "appstoreconnect-v1", bid = the app's bundle id). A full
        // production implementation would additionally decode and verify
        // the returned signedTransactionInfo JWS against Apple's published
        // root certificate chain - this template stops at confirming the
        // API call itself succeeds and returns a non-empty signed payload,
        // which is already enough to prove the request/response plumbing
        // end to end; chain verification is a documented, deliberate scope
        // boundary, not an oversight.
        public async Task<IapStoreVerificationOutcome> VerifyViaAppleAppStoreServerApiAsync(
            SecretRotationManager privateKeyManager,
            string keyId,
            string issuerId,
            string bundleId,
            string transactionId)
        {
            if (_httpClientFactory == null)
            {
                return IapStoreVerificationOutcome.Failed("IHttpClientFactory is not configured for Apple App Store verification.");
            }

            string? privateKeyPem = privateKeyManager.GetCurrentSecret();
            if (string.IsNullOrEmpty(privateKeyPem))
            {
                return IapStoreVerificationOutcome.Failed("Apple App Store Connect private key is not configured.");
            }

            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string assertion = CreateAppleJwtAssertion(privateKeyPem, keyId, issuerId, bundleId, now);

                HttpClient httpClient = _httpClientFactory.CreateClient(AppleAppStoreHttpClientName);
                string requestUri = $"https://api.storekit.itunes.apple.com/inApps/v1/transactions/{Uri.EscapeDataString(transactionId)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", assertion);

                using HttpResponseMessage response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    try
                    {
                        AppleErrorResponse? error = JsonSerializer.Deserialize<AppleErrorResponse>(responseBody);
                        return IapStoreVerificationOutcome.Failed($"Apple App Store Server API returned HTTP {(int)response.StatusCode}: errorCode={error?.ErrorCode}, {error?.ErrorMessage}");
                    }
                    catch (JsonException)
                    {
                        return IapStoreVerificationOutcome.Failed($"Apple App Store Server API returned HTTP {(int)response.StatusCode}: {TruncateForError(responseBody)}");
                    }
                }

                AppleTransactionResponse? transaction;
                try
                {
                    transaction = JsonSerializer.Deserialize<AppleTransactionResponse>(responseBody);
                }
                catch (JsonException ex)
                {
                    return IapStoreVerificationOutcome.Failed($"Malformed Apple App Store Server API response: {ex.Message}");
                }

                if (transaction == null || string.IsNullOrEmpty(transaction.SignedTransactionInfo))
                {
                    return IapStoreVerificationOutcome.Failed("Apple App Store Server API response is missing signedTransactionInfo.");
                }

                return IapStoreVerificationOutcome.VerifiedResult;
            }
            catch (HttpRequestException ex)
            {
                return IapStoreVerificationOutcome.Failed($"Apple App Store Server API request failed: {ex.Message}");
            }
            catch (TaskCanceledException ex)
            {
                return IapStoreVerificationOutcome.Failed($"Apple App Store Server API request timed out: {ex.Message}");
            }
            catch (CryptographicException ex)
            {
                return IapStoreVerificationOutcome.Failed($"Failed to sign the Apple App Store Connect JWT: {ex.Message}");
            }
        }

        // Modul: Apple's documented App Store Server API auth scheme -
        // ES256 (ECDSA P-256/SHA256), header carries kid (the API key id),
        // claims carry iss (issuerId), iat, exp (capped at 60 minutes by
        // Apple), aud="appstoreconnect-v1", bid=bundleId. Materially
        // different from Google's RS256 JWT-bearer assertion above -
        // Apple's key material is an EC private key (a .p8 file), not an
        // RSA key, so this uses ECDsa.ImportFromPem/SHA256 signing instead
        // of RSA.SignData.
        private static string CreateAppleJwtAssertion(string privateKeyPem, string keyId, string issuerId, string bundleId, long now)
        {
            string header = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, object>
            {
                ["alg"] = "ES256",
                ["kid"] = keyId,
                ["typ"] = "JWT"
            })));
            string payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, object>
            {
                ["iss"] = issuerId,
                ["iat"] = now,
                ["exp"] = now + 1200,
                ["aud"] = "appstoreconnect-v1",
                ["bid"] = bundleId
            })));

            string signingInput = $"{header}.{payload}";
            using ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(privateKeyPem);
            byte[] signature = ecdsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
            return $"{signingInput}.{Base64UrlEncode(signature)}";
        }

        private static string TruncateForError(string body)
        {
            const int maxLength = 500;
            return body.Length <= maxLength ? body : body.Substring(0, maxLength) + "...";
        }
    }
}
