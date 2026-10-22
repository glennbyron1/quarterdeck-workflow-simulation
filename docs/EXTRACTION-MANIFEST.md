# Public extraction manifest

## Source baseline

- Private source: Quarterdeck V3 research baseline (repository identity and
  revision withheld)
- Relationship: public derivative; history was not copied
- UI lineage: V2-inspired Razor application retained and adapted during V3

## Included

- Domain and authorization logic required by the simulation
- Simulation application boundary
- Simulation-only Razor host and pages
- SQLite simulation persistence, backup and recovery logic
- Fake authority journal
- Simulation, presentation and boundary tests
- Recovery crash-probe test tool
- Public requirements and evolution narrative

## Excluded

- Live Active Directory and LDAP adapters
- AD CS orchestration and certificate templates
- Broker, Broker client and named-pipe implementation
- Windows identity and service-host integrations
- PIV/card/token operations
- Lab IP addresses, hostnames, credentials and private evidence
- Commissioning packages, operational runbooks and private environment state

## Public-specific changes

- Removed the live workflow schema embedded-resource reference.
- Renamed visible phase labels to describe a public workflow simulation.
- Added a simulation-only solution and test project.
- Added public safety, provenance, requirement and dependency documentation.

An exact file hash manifest is generated as `docs/SOURCE-SHA256.txt` after the
final public review.
