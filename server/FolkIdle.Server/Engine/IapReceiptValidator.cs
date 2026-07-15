using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolkIdle.Server.Engine
{
    public readonly struct IapReceiptValidationResult
    {
        public readonly bool IsValid;
        public readonly string TransactionId;
        public readonly string ProductId;

        public IapReceiptValidationResult(bool isValid, string transactionId, string productId)
        {
            IsValid = isValid;
            TransactionId = transactionId;
            ProductId = productId;
        }

        public static readonly IapReceiptValidationResult Invalid = new IapReceiptValidationResult(false, string.Empty, string.Empty);
    }

    // Modul: swappable store-receipt validator. TransactionId/ProductId are
    // the only fields a caller may trust from this result - the premium
    // currency amount is never part of it and is instead resolved
    // server-side from a fixed ProductId -> diamonds table (see
    // BillingVerificationEngine.ResolvePremiumDiamondsForProduct), so a
    // forged or replayed receipt cannot claim an arbitrary reward even if
    // it manages a plausible-looking TransactionId/ProductId pair.
    // Production deployment must inject a real IIapReceiptValidator that
    // verifies the receipt's signature against Apple's App Store Server API
    // or Google's Play Developer API instead of this mock.
    public interface IIapReceiptValidator
    {
        IapReceiptValidationResult Validate(string base64Receipt);
    }

    // Modul: decodes the receipt as base64(JSON {"transactionId":"...",
    // "productId":"..."}) with no signature check. Deliberately not
    // cryptographically verified - this exists purely so the receipt
    // decode/replay-rejection/credit flow can be exercised end to end
    // before a real store SDK is wired in. NEVER register this
    // implementation outside local development and tests.
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

            return new IapReceiptValidationResult(true, payload.TransactionId, payload.ProductId);
        }
    }
}
