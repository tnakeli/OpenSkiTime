# Security policy

## Reporting a vulnerability

Report security vulnerabilities privately through [GitHub private vulnerability reporting](https://github.com/tnakeli/OpenSkiTime/security/advisories/new). Do not open a public issue for a suspected vulnerability.

Include the affected component (desktop application, live timing server, website or deployment scripts), the version or commit, steps to reproduce and the impact you expect. Use synthetic data only: never attach credentials, publisher keys, downloaded FIS lists or personal race databases.

The maintainer aims to acknowledge reports within seven days. Fixes are released as a new version; reporters are credited in the advisory unless they ask otherwise.

The same contact is published in machine-readable form at <https://openskiti.me/.well-known/security.txt> and on each live timing server at `/.well-known/security.txt`.

## Supported versions

OpenSkiTime is in pre-release development. Only the latest release and the `master` branch receive security fixes.

## Scope

In scope: the OpenSkiTime desktop application and its live timing control panel, worker and server; the hosted `live.openskiti.me` service; the `openskiti.me` website; and the build, release and deployment automation in this repository.

Out of scope: third-party services such as FIS, ALGE or Open-Meteo, and self-hosted live timing servers operated by others. Report issues in those services to their operators. Volumetric denial-of-service testing against the hosted services is not permitted.
