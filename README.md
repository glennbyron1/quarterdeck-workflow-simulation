# Quarterdeck Workflow Simulation

Quarterdeck Workflow Simulation is a local, server-free demonstration of a
controlled PIV credential lifecycle. It presents the V2-inspired operator
interface while running only synthetic identities, a fake authority and a
local SQLite workflow store.

It is intended to show how requirements, authorization boundaries, recovery
behavior and evidence can be designed before a workflow is connected to Active
Directory, a certificate authority or physical cards.

## Safety boundary

This repository contains no live AD, AD CS, Broker, PIV, card-printer or
Windows-logon adapter. The web host binds only to loopback. Scenario roles are
not authenticated people. Do not enter real identity data, passwords, PINs,
PUKs, keys or other secrets.

## Run locally

Requirements:

- Windows, macOS or Linux
- .NET SDK 10.0.302, selected by `global.json`

```powershell
dotnet restore Quarterdeck.Simulation.slnx --locked-mode
dotnet build Quarterdeck.Simulation.slnx --no-restore -warnaserror
dotnet test Quarterdeck.Simulation.slnx --no-build
dotnet run --project src/Quarterdeck.App/Quarterdeck.App.csproj
```

On macOS, `/var` is a symbolic path. The store intentionally rejects reparse
paths, so give tests a real repository-local temporary directory:

```bash
mkdir -p artifacts/tmp
TMPDIR="$PWD/artifacts/tmp" dotnet test Quarterdeck.Simulation.slnx --no-build
```

Open `http://127.0.0.1:5080`. The application does not listen on the LAN.

## What the demonstration covers

- HR creates a synthetic request.
- IT resolves a synthetic account and prepares a credential request.
- HR verifies identity and approves the exact request version.
- IT uses a fake authority, records verification and records delivery.
- Returned, rejected and name-change paths remain visible.
- SQLite persistence, audit events, backup and crash recovery are exercised.
- Separation-of-duty and stale-approval rules are enforced by the domain model.

## Evolution

- **V1** established the first complete prototype and proved the basic PIV and
  Windows integration direction in a lab.
- **V2** added the stronger Razor operator experience, SQLite persistence,
  request-bound authorization, audit behavior and extensive live-lab evidence.
- **V3** retained the useful V2 design, tightened trust boundaries and separated
  simulation from live adapters. This public repository extracts that safe
  simulation slice.

See [the evolution record](docs/EVOLUTION.md), [public requirements](requirements/README.md)
and [extraction manifest](docs/EXTRACTION-MANIFEST.md). The latest clean-build
record is in [build and test evidence](docs/BUILD-TEST-EVIDENCE.md).

## Status and claims

This is a research and portfolio demonstration, not a production credential
issuance system and not a claim of DoD, federal or commercial certification.
Lab results demonstrate only the tested configurations described in the public
evidence and documentation.

The original research repositories remain private. This public derivative was
selected from the author's personal R&D and reviewed, reorganized and cleaned
with AI assistance. The architecture, requirements, decisions, lab execution
and final acceptance remain author-directed.

## License

No open-source license has been granted yet. See [NOTICE](NOTICE.md). Source is
published for inspection and portfolio review while licensing is decided.
