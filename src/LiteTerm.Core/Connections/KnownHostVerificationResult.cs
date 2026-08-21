namespace LiteTerm.Core.Connections;

public enum KnownHostVerificationStatus
{
    Unknown,
    Trusted,
    Mismatch
}

public sealed record KnownHostVerificationResult(
    KnownHostVerificationStatus Status,
    KnownHostEntry? ExpectedHost);
