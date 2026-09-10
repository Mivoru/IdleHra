using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolkIdle.Server.Engine
{
    /// <summary>The outcome of asking a store whether a purchase really happened.</summary>
    public readonly struct StoreApiVerification
    {
        public readonly bool IsVerified;
        public readonly string TransactionId;
        public readonly string ProductId;
        public readonly string ErrorMessage;

        private StoreApiVerification(bool isVerified, string transactionId, string productId, string errorMessage)
        {
            IsVerified = isVerified;
            TransactionId = transactionId;
            ProductId = productId;
            ErrorMessage = errorMessage;
        }

        public static StoreApiVerification Verified(string transactionId, string productId)
            => new StoreApiVerification(true, transactionId, productId, string.Empty);

        public static StoreApiVerification Failed(string errorMessage)
            => new StoreApiVerification(false, string.Empty, string.Empty, errorMessage);
    }

    /// <summary>
    /// Asks Google or Apple whether a purchase happened, instead of asking
    /// whether a blob the client sent verifies against a key.
    /// </summary>
    public interface IStoreApiReceiptVerifier
    {
        /// <summary>
        /// Whether this receipt is a store-API envelope at all. False sends the
        /// caller down the legacy signed-JWS path unchanged.
        /// </summary>
        bool CanHandle(string base64Receipt);

        Task<StoreApiVerification> VerifyAsync(string base64Receipt);
    }

    /// <summary>
    /// THE RECEIPT PATH COULD NEVER HAVE VERIFIED A REAL RECEIPT, AND NOTHING
    /// SAID SO BECAUSE NO CLIENT HAD EVER SENT ONE.
    /// </summary>
    /// <remarks>
    /// Modul: `ProductionIapReceiptValidator.Validate` verifies a bespoke
    /// envelope - `{provider, payload, signature}`, an RSA signature over the
    /// payload, checked against a configured store public key. No store on
    /// earth produces that shape. Google hands a client a purchase token and
    /// Apple hands it a transaction id; neither hands over anything signed with
    /// a key this server could hold, and a client obviously cannot manufacture
    /// one. The scheme was verifiable only by the test that invented it, which
    /// is why it passed for as long as it has existed.
    ///
    /// The two REAL verification calls were already written -
    /// `VerifyViaGooglePlayDeveloperApiAsync` and
    /// `VerifyViaAppleAppStoreServerApiAsync` - and that file's own comment
    /// says they are "not wired into Validate above" and that a deployment
    /// moving off the JWS scheme "would call these directly from
    /// BillingVerificationEngine.VerifyReceiptAsync". This is that call.
    ///
    /// It is STRICTLY STRONGER than what it replaces. A signature check asks
    /// whether some bytes are well formed; this asks the store whether a person
    /// paid. A client that invents a transaction id or replays somebody else's
    /// purchase token is refused by Google or Apple, not by us.
    ///
    /// FAILS CLOSED, EVERY TIME. An envelope that names a store, with no
    /// credentials configured for that store, is REFUSED rather than passed to
    /// the legacy path - because the legacy path would then be asked to check a
    /// signature the envelope does not carry, and "no signature present" must
    /// never resolve to "granted". A deployment with no store credentials sells
    /// nothing, which is the correct behaviour for a deployment that cannot
    /// tell a real purchase from a typed one.
    /// </remarks>
    public sealed class StoreApiReceiptVerifier : IStoreApiReceiptVerifier
    {
        private readonly ProductionIapReceiptValidator _validator;
        private readonly SecretRotationManager _googleServiceAccount;
        private readonly SecretRotationManager _applePrivateKey;
        private readonly string _androidPackageName;
        private readonly string _appleBundleId;
        private readonly string _appleKeyId;
        private readonly string _appleIssuerId;

        public StoreApiReceiptVerifier(
            ProductionIapReceiptValidator validator,
            SecretRotationManager googleServiceAccount,
            SecretRotationManager applePrivateKey,
            string androidPackageName,
            string appleBundleId,
            string appleKeyId,
            string appleIssuerId)
        {
            _validator = validator;
            _googleServiceAccount = googleServiceAccount;
            _applePrivateKey = applePrivateKey;
            _androidPackageName = androidPackageName;
            _appleBundleId = appleBundleId;
            _appleKeyId = appleKeyId;
            _appleIssuerId = appleIssuerId;
        }

        private sealed class Envelope
        {
            public string? provider { get; set; }
            public string? productId { get; set; }
            public string? transactionId { get; set; }
            public string? purchaseToken { get; set; }
        }

        /// <summary>
        /// Reads the envelope, or returns null for anything that is not one.
        /// </summary>
        /// <remarks>
        /// Deliberately strict about `provider`: the legacy JWS envelope also
        /// carries that field, and the discriminator between the two schemes is
        /// that this one has NO `signature` and does have an identifier the
        /// store can be asked about. Guessing wrong in either direction would
        /// send a receipt down a path that cannot check it.
        /// </remarks>
        private static Envelope? Parse(string base64Receipt)
        {
            if (string.IsNullOrWhiteSpace(base64Receipt)) return null;

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(base64Receipt.Trim());
            }
            catch (FormatException)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                // A signature means the legacy scheme, whatever else is present.
                if (root.TryGetProperty("signature", out _)) return null;
                if (!root.TryGetProperty("provider", out var providerElement)) return null;
                if (providerElement.ValueKind != JsonValueKind.String) return null;

                return JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(raw));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public bool CanHandle(string base64Receipt)
        {
            var envelope = Parse(base64Receipt);
            return envelope?.provider is "GooglePlay" or "AppStore";
        }

        public async Task<StoreApiVerification> VerifyAsync(string base64Receipt)
        {
            var envelope = Parse(base64Receipt);
            if (envelope == null) return StoreApiVerification.Failed("Not a store-API receipt envelope.");

            string productId = envelope.productId ?? string.Empty;
            string transactionId = envelope.transactionId ?? string.Empty;

            // Modul: both are required BEFORE any network call. They are what
            // the ledger is keyed on and what the diamond amount is resolved
            // from, so a verified purchase with an empty transaction id would
            // be a purchase that can never be deduplicated - every replay of it
            // would grant again.
            if (productId.Length == 0) return StoreApiVerification.Failed("The receipt names no product.");
            if (transactionId.Length == 0) return StoreApiVerification.Failed("The receipt carries no transaction id.");

            if (envelope.provider == "GooglePlay")
            {
                string purchaseToken = envelope.purchaseToken ?? string.Empty;
                if (purchaseToken.Length == 0)
                {
                    return StoreApiVerification.Failed("A Google Play receipt needs a purchase token.");
                }
                if (_androidPackageName.Length == 0)
                {
                    return StoreApiVerification.Failed("FOLKIDLE_ANDROID_PACKAGE is not configured.");
                }

                var outcome = await _validator.VerifyViaGooglePlayDeveloperApiAsync(
                    _googleServiceAccount, _androidPackageName, productId, purchaseToken);

                return outcome.IsVerified
                    ? StoreApiVerification.Verified(transactionId, productId)
                    : StoreApiVerification.Failed(outcome.ErrorMessage);
            }

            if (_appleBundleId.Length == 0 || _appleKeyId.Length == 0 || _appleIssuerId.Length == 0)
            {
                return StoreApiVerification.Failed("Apple App Store Server API details are not configured.");
            }

            var appleOutcome = await _validator.VerifyViaAppleAppStoreServerApiAsync(
                _applePrivateKey, _appleKeyId, _appleIssuerId, _appleBundleId, transactionId);

            return appleOutcome.IsVerified
                ? StoreApiVerification.Verified(transactionId, productId)
                : StoreApiVerification.Failed(appleOutcome.ErrorMessage);
        }
    }
}
