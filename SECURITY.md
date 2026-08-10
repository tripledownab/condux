# Security Policy

Security is a core value of Condux. The source is available, so the code is read, audited, and probed. We
treat that scrutiny as a feature and we want to hear about anything you find.

## Reporting a vulnerability

Please report suspected vulnerabilities **privately**, not in a public issue or pull request.

- Email **security@condux.ai** with a description, the affected component, and steps to reproduce.
- Or open a **GitHub private vulnerability report** (Security > Report a vulnerability) on the repository.

We aim to acknowledge a report within **3 business days** and to keep you updated as we investigate. We
practice coordinated disclosure: we agree a timeline with you and credit you unless you prefer to stay
anonymous.

## Scope

In scope: the Condux platform code in this repository (relay, control-plane, consumer, conductor,
dashboard, SDKs) and its default deployment configuration.

Out of scope: findings that require an already-compromised host or account, denial of service by traffic
volume alone, missing security headers with no demonstrated impact, and issues in third-party
dependencies (report those upstream, though we still want to know if we ship a vulnerable version).

## Safe harbor

We will not pursue or support legal action over good-faith security research that follows this policy,
respects privacy, avoids data destruction, and does not degrade the service for others.

## Handling of sensitive data

Condux ingests error data that can contain sensitive values, so the platform scrubs PII and secrets on
the ingest path and again before anything reaches the AI fix engine (the Conductor). If you find a way
around either scrub, or a way for one tenant to reach another tenant's data, treat it as a vulnerability
and report it.
