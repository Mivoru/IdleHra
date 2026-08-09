using System;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public enum OAuthProviderType
    {
        None = 0,
        Google = 1,
        Apple = 2
    }

    public readonly struct OAuthTokenValidationResult
    {
        public readonly bool IsValid;
        public readonly OAuthProviderType ProviderType;
        public readonly string ExternalProviderId;

        public OAuthTokenValidationResult(bool isValid, OAuthProviderType providerType, string externalProviderId)
        {
            IsValid = isValid;
            ProviderType = providerType;
            ExternalProviderId = externalProviderId;
        }

        public static readonly OAuthTokenValidationResult Invalid = new OAuthTokenValidationResult(false, OAuthProviderType.None, string.Empty);
    }

    // Modul: swappable OAuth provider-token validator. Every call site in
    // AuthenticationEngine depends only on this interface, not on which
    // implementation is registered, so a real Google tokeninfo-endpoint
    // call or Apple JWKS-based JWT verification can be swapped in later
    // without touching the account-linking logic itself.
    public interface IOAuthTokenValidator
    {
        OAuthTokenValidationResult Validate(string providerToken);
    }

    /// <summary>
    /// Refuses every token, and is what a real deployment gets.
    ///
    /// THE MOCK VALIDATOR WAS REGISTERED IN PRODUCTION. Its own comment says
    /// "NEVER register this implementation outside local development and
    /// tests", and `Program.cs` registered it unconditionally - so the live
    /// server accepted `mock:Google:{id}` from anybody, with no cryptography,
    /// on `/api/v1/auth/login`, which is unauthenticated by design.
    ///
    /// That was not yet exploitable, and only by luck: nothing in the web
    /// client can link an OAuth identity, so `TryLoginByOAuthAsync` never finds
    /// a row and answers 404. The moment OAuth ships - which is exactly what a
    /// Google Play launch means - every linked account becomes signable-into by
    /// anyone who knows its provider id. A Google `sub` is not a secret; every
    /// app you have ever signed into with Google has it.
    ///
    /// So the production registration FAILS CLOSED, the same way the admin
    /// endpoint's missing key means "closed" rather than "default password".
    /// OAuth does not work in production until somebody writes a validator that
    /// actually verifies a signature (Google tokeninfo, Apple JWKS) - which is
    /// no loss, because it did not work SAFELY before.
    /// </summary>
    public sealed class DisabledOAuthTokenValidator : IOAuthTokenValidator
    {
        public OAuthTokenValidationResult Validate(string providerToken)
            => OAuthTokenValidationResult.Invalid;
    }

    // Modul: accepts tokens of the exact shape "mock:{providerType}:{externalId}"
    // (e.g. "mock:Google:114823..."). Deliberately not cryptographically
    // verified - this exists purely so the account-linking and login-
    // recovery flow can be exercised end to end before a real provider SDK
    // is wired in. NEVER register this implementation outside local
    // development and tests.
    public sealed class MockOAuthTokenValidator : IOAuthTokenValidator
    {
        public OAuthTokenValidationResult Validate(string providerToken)
        {
            if (string.IsNullOrEmpty(providerToken))
            {
                return OAuthTokenValidationResult.Invalid;
            }

            string[] parts = providerToken.Split(':', 3);
            if (parts.Length != 3 || parts[0] != "mock")
            {
                return OAuthTokenValidationResult.Invalid;
            }

            if (!Enum.TryParse(parts[1], ignoreCase: true, out OAuthProviderType providerType) || providerType == OAuthProviderType.None)
            {
                return OAuthTokenValidationResult.Invalid;
            }

            string externalId = parts[2];
            if (string.IsNullOrEmpty(externalId))
            {
                return OAuthTokenValidationResult.Invalid;
            }

            return new OAuthTokenValidationResult(true, providerType, externalId);
        }
    }
}
