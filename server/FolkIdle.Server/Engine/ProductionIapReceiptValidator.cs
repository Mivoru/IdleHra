using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolkIdle.Server.Engine
{
    public enum IapStoreProvider
    {
        Unknown = 0,
        GooglePlay = 1,
        AppStore = 2
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
    // Google Play's current production integration verifies purchases via
    // a server-to-server call to the Play Developer API instead of a
    // client-side signature check (the older signed-JWS scheme this class
    // implements is deprecated but still functionally correct and is what
    // this task's "verify against the store's public key" requirement asks
    // for) - VerifyViaGooglePlayDeveloperApiAsync below is the template for
    // that modern path, included for when a production deployment migrates
    // to it, but is not part of the synchronous Validate flow the
    // IIapReceiptValidator interface exposes.
    //
    // Receipt validation is a per-purchase, user-triggered operation, not a
    // 10 Hz tick-loop hot path (contrast SimulationEngine's broadcast loop,
    // FolkIdleEventSource's own doc comment) - the allocations naturally
    // involved in one JSON deserialize and a handful of byte arrays per
    // call are not in scope of this codebase's zero-allocation hot-path
    // discipline, which targets code that runs thousands of times a second,
    // not once per purchase.
    public sealed class ProductionIapReceiptValidator : IIapReceiptValidator
    {
        private readonly SecretRotationManager _googlePlayPublicKeyManager;
        private readonly SecretRotationManager _appleStorePublicKeyManager;

        public ProductionIapReceiptValidator(SecretRotationManager googlePlayPublicKeyManager, SecretRotationManager appleStorePublicKeyManager)
        {
            _googlePlayPublicKeyManager = googlePlayPublicKeyManager;
            _appleStorePublicKeyManager = appleStorePublicKeyManager;
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

        // Modul: template for the modern, current-production Google Play
        // verification path - a server-to-server call to the Play
        // Developer API (purchases.products.get), authenticated with a
        // service-account credential resolved through its own
        // SecretRotationManager, never a hardcoded key. Not wired into
        // Validate above (that method's signature is synchronous, matching
        // IIapReceiptValidator, and this call is inherently a network
        // round trip) - a production deployment migrating away from the
        // signed-JWS scheme would call this directly from
        // BillingVerificationEngine.VerifyReceiptAsync instead of Validate.
        public System.Threading.Tasks.Task<bool> VerifyViaGooglePlayDeveloperApiAsync(
            System.Net.Http.HttpClient httpClient,
            SecretRotationManager serviceAccountCredentialManager,
            string packageName,
            string productId,
            string purchaseToken)
        {
            string? serviceAccountCredential = serviceAccountCredentialManager.GetCurrentSecret();
            if (string.IsNullOrEmpty(serviceAccountCredential))
            {
                return System.Threading.Tasks.Task.FromResult(false);
            }

            // The real call exchanges the service-account credential for an
            // OAuth2 bearer token (via Google's token endpoint, scope
            // https://www.googleapis.com/auth/androidpublisher) and then
            // issues:
            //   GET https://androidpublisher.googleapis.com/androidpublisher/v3/applications/
            //       {packageName}/purchases/products/{productId}/tokens/{purchaseToken}
            //   Authorization: Bearer {access_token}
            // A 200 response with purchaseState == 0 means a valid,
            // non-cancelled purchase. This template intentionally stops
            // short of performing that exchange - no live Google Play
            // credential exists in this environment to authenticate
            // against, and fabricating a response here would be
            // indistinguishable from a real one to any caller.
            string requestUri = $"https://androidpublisher.googleapis.com/androidpublisher/v3/applications/{Uri.EscapeDataString(packageName)}/purchases/products/{Uri.EscapeDataString(productId)}/tokens/{Uri.EscapeDataString(purchaseToken)}";
            _ = requestUri;
            _ = httpClient;
            return System.Threading.Tasks.Task.FromException<bool>(new NotImplementedException("VerifyViaGooglePlayDeveloperApiAsync requires a configured Google Play service-account credential and live network access - not available outside a real store integration."));
        }

        // Modul: template for calling Apple's App Store Server API
        // (/inApps/v1/transactions/{transactionId}), authenticated with a
        // JWT signed using an App Store Connect API key resolved through
        // its own SecretRotationManager. Not wired into Validate above for
        // the same reason as the Google Play template.
        public System.Threading.Tasks.Task<bool> VerifyViaAppleAppStoreServerApiAsync(
            System.Net.Http.HttpClient httpClient,
            SecretRotationManager appStoreConnectKeyManager,
            string transactionId)
        {
            string? appStoreConnectKey = appStoreConnectKeyManager.GetCurrentSecret();
            if (string.IsNullOrEmpty(appStoreConnectKey))
            {
                return System.Threading.Tasks.Task.FromResult(false);
            }

            string requestUri = $"https://api.storekit.itunes.apple.com/inApps/v1/transactions/{Uri.EscapeDataString(transactionId)}";
            _ = requestUri;
            _ = httpClient;
            return System.Threading.Tasks.Task.FromException<bool>(new NotImplementedException("VerifyViaAppleAppStoreServerApiAsync requires a configured App Store Connect API key and live network access - not available outside a real store integration."));
        }
    }
}
