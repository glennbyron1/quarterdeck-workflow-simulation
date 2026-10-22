# Evolution from V1 to V3

This record explains the engineering progression without publishing the private
lab implementation or operational evidence.

| Version | Primary purpose | What it established | What changed next |
|---|---|---|---|
| V1 | Feasibility prototype | A Windows/PIV direction, first lifecycle concepts and early lab integration | Separate product workflow from infrastructure experiments; improve usability and evidence |
| V2 | Integrated lab prototype | V2 Razor GUI, SQLite persistence, request-bound authorization, audit behavior and live lab testing | Reduce repeated setup, make trust boundaries explicit and isolate simulation from live adapters |
| V3 | Requirements-led refinement | Reused V2 foundations, added stricter provenance, recovery, commissioned-state and trust-boundary controls | Publish only the server-free simulation while retaining live infrastructure privately |

The versions are stepping stones rather than three competing products. V3 did
not justify rebuilding working V2 behavior. It retained proven components and
changed or retested only the boundaries that materially changed.

## Why the public application is a derivative

Publishing a raw V1 or V2 repository would mix useful portfolio material with
lab-specific integration code, environment references and historical evidence.
This derivative preserves the operator experience and the most defensible
simulation behavior while intentionally omitting live infrastructure paths.
