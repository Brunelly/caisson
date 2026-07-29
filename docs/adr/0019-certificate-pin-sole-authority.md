# 0019: A configured certificate fingerprint pin is the sole TLS-trust authority

## Status

Accepted

## Context

Security review `security-review-5` (finding #1) found that both `RedfishClient.ValidateServerCertificate`
and `RouterOsApiClient.ValidateServerCertificate` checked `sslPolicyErrors == SslPolicyErrors.None` FIRST
and only consulted a configured SHA-256 fingerprint pin afterwards, as a fallback for the untrusted-chain
case. That ordering means the pin constrains only the self-signed case (where it is redundant with the
platform validator) and is never consulted when a peer presents a certificate the host trust store
accepts for that name — precisely the active-MITM case a fingerprint pin exists to defend against. The
defect was present verbatim in both drivers (the RouterOS client is documented as the origin the Redfish
client's policy was ported from).

Neither driver connects to a certificate authority-issued endpoint in normal operation (iLO/CHR both ship
self-signed certificates), so `CheckCertificateRevocation`/`X509RevocationMode.Online` had never been
enabled either — a smaller, related gap once the pin becomes authoritative and a genuinely CA-trusted
chain can still be the accepted path (no pin, no opt-in, but a valid public chain).

## Decision

When a `CertificateThumbprint` is configured, the SHA-256 fingerprint comparison runs FIRST and its
result (true or false) is returned unconditionally — `sslPolicyErrors` is never consulted on that path.
The `sslPolicyErrors == SslPolicyErrors.None` fast-path, and the `AllowUntrustedCertificate` opt-in, are
reachable only when no pin is configured. Revocation checking (`CertificateRevocationCheckMode` /
`CheckCertificateRevocation`) is enabled on the SocketsHttpHandler/SslClientAuthenticationOptions only
for the non-pinned path, since a pinned self-signed certificate has no CA-issued revocation information to
check in the first place.

## Consequences

- A certificate that happens to chain to a trusted root can no longer bypass a configured pin — the pin is
  now a strict allow-list, not a fallback.
- Integration tests that pin against the in-process simulators' self-signed certificates are unaffected;
  tests that rely on `AllowUntrustedCertificate` (no pin) now also exercise `X509RevocationMode.Online`,
  which is a no-op (not a network call) for a certificate with no CRL/AIA extension, so the simulator-backed
  suite stays hermetic and does not require network access.
- Operators who configure BOTH a pin and `AllowUntrustedCertificate` now get pin-only behaviour (the
  opt-in is simply unreachable) — this was already true logically before the fix and is unchanged.
