# Status

## Proven by this repository

- The October 22 baseline restored in locked mode, built warning-free and
  passed all 321 then-included tests on documented macOS and disposable
  Windows Server 2025 .NET SDK 10.0.302 toolchains.
- The application is composed from simulation-only projects.
- The HTTP listener is restricted to loopback.
- The workflow persists locally in SQLite and exercises fake external outcomes.

## Deliberately not claimed

- Production readiness
- Independent reproduction
- DoD, federal or commercial certification
- Compatibility with a specific PIV vendor
- Live Active Directory, AD CS, card issuance or Windows-logon capability
- Protection against a malicious local administrator

## Publication status

The October 22 baseline completed dependency, privacy, macOS and Windows
qualification. A later local-host security fix passed a new macOS Release
build, 334 tests and direct HTTP Host checks. Its Windows qualification and
clean public-history publication are still pending. Keep the existing remote
repository private until those gates are closed.
