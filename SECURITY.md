# Security policy

## Supported scope

Only the latest commit on the default branch is supported for review. This is a
local simulation and must not be exposed to an untrusted network or used with
real identity data or secrets.

## Report a concern

Please use GitHub's private vulnerability reporting feature if it is enabled.
Do not include real credentials, private keys, personal data or exploit traffic
against any live Quarterdeck lab system.

## Security properties expected here

- Loopback-only web binding.
- No references to live AD, CA, Broker, PIV or Windows-logon adapters.
- Synthetic identities and a fake authority only.
- Request/version-bound approvals and immutable authorization inputs.
- Fail-closed behavior on stale state, ambiguous recovery and persistence
  failure.
- No secrets or private operational evidence committed to the repository.
