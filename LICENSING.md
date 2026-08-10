# Licensing

Condux is **source-available** under the **Functional Source License (FSL-1.1-ALv2)**. The full text is
in [LICENSE](LICENSE); this page is the plain-language summary, and the LICENSE controls if they differ.

## What you can do

- Read, self-host, and modify the source for any purpose other than a Competing Use.
- Run Condux for your own team or company, internally or in production, at no cost.
- Use it for non-commercial education and research.
- Provide professional services around it to someone who uses Condux under these terms.

## The one restriction

You may not make Condux available to others as a commercial product or service that competes with
Condux (a hosted Condux-as-a-service, or a product with the same or substantially similar
functionality). That Competing Use is the only thing the license withholds.

## It becomes Apache-2.0 after two years

Every release automatically converts to the Apache License, Version 2.0 on the second anniversary of the
day it is made available. Each version becomes fully permissive open source on a rolling two-year
schedule, so nothing is locked away forever.

## How Condux is funded

Condux is a commercial product. Development is paid for by:

- **Condux Cloud** (condux.ai): the managed, hosted service with paid tiers (Free, Team, Business,
  Enterprise).
- **Enterprise self-host**: support, SSO, and advanced features for teams that run Condux themselves.

Self-hosting the source is free. The paid tiers pay for hosted convenience and enterprise features, not
for the right to run the code.

## Third-party dependencies

Condux's own code is FSL. Its dependencies stay under their own permissive licenses, and CI enforces an
allowlist so a copyleft or otherwise incompatible license cannot enter the tree unnoticed.

## Why FSL

FSL is the source-available license the direct incumbent (Sentry) uses. It keeps the code open to read,
self-host, and learn from, protects the project from a cloud provider reselling it, and guarantees each
release turns into permissive open source in two years. The decision is recorded in
[ADR-0013](docs/adr/0013-source-available-fsl-license.md).
