using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public readonly struct IapReceiptValidationResult
    {
        public readonly bool IsValid;

        // Modul: a distinct, explicitly-checked flag rather than folding
        // this into IsValid - BillingVerificationEngine.VerifyReceiptAsync
        // gates currency-granting on this field specifically (see its own
        // "mandatory signature-verification step" comment), so the
        // requirement is visible at the call site that enforces it, not
        // just buried inside whichever validator implementation happens to
        // be registered. MockIapReceiptValidator sets this true
        // unconditionally (it never performs a real check, by design - see
        // its own doc comment); ProductionIapReceiptValidator sets it only
        // when the receipt's signature actually verified against a
        // configured store public key.
        public readonly bool SignatureVerified;
        public readonly string TransactionId;
        public readonly string ProductId;

        public IapReceiptValidationResult(bool isValid, bool signatureVerified, string transactionId, string productId)
        {
            IsValid = isValid;
            SignatureVerified = signatureVerified;
            TransactionId = transactionId;
            ProductId = productId;
        }

        public static readonly IapReceiptValidationResult Invalid = new IapReceiptValidationResult(false, false, string.Empty, string.Empty);
    }

    // Modul: swappable store-receipt validator. TransactionId/ProductId are
    // the only fields a caller may trust from this result - the premium
    // currency amount is never part of it and is instead resolved
    // server-side from a fixed ProductId -> diamonds table (see
    // BillingVerificationEngine.ResolvePremiumDiamondsForProduct), so a
    // forged or replayed receipt cannot claim an arbitrary reward even if
    // it manages a plausible-looking TransactionId/ProductId pair.
    // Production deployment must inject a real IIapReceiptValidator (see
    // ProductionIapReceiptValidator) that verifies the receipt's signature
    // against the store's public key instead of this mock.
    public interface IIapReceiptValidator
    {
        IapReceiptValidationResult Validate(string base64Receipt);
    }

    // Modul: decodes the receipt as base64(JSON {"transactionId":"...",
    // "productId":"..."}) with no signature check - SignatureVerified is
    // set true unconditionally, a deliberate bypass, not an oversight, so
    // this mock can exercise the decode/replay-rejection/credit flow
    // without also requiring a real store key pair. Deliberately not
    // cryptographically verified. NEVER register this implementation
    // outside local development and tests.
    public sealed class MockIapReceiptValidator : IIapReceiptValidator
    {
        private sealed class MockReceiptPayload
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

            byte[] jsonBytes;
            try
            {
                jsonBytes = Convert.FromBase64String(base64Receipt);
            }
            catch (FormatException)
            {
                return IapReceiptValidationResult.Invalid;
            }

            MockReceiptPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<MockReceiptPayload>(jsonBytes);
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
    }
}
