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

**If you have evidence that the issue is being exploited, say so in the first line and use email.**
That case is handled immediately rather than on the acknowledgement target above, because evidence of
real exploitation starts a legal reporting deadline for us that is shorter than our triage window.
Include what the evidence is, even briefly.

## Reporting to authorities

Condux is a manufacturer under the EU Cyber Resilience Act, so from 11 September 2026 we are required
to report an **actively exploited** vulnerability in the software we distribute to the relevant
national CSIRT and to ENISA: an early warning within 24 hours, a fuller notification within 72
hours, and a final report within 14 days of a corrective or mitigating measure becoming available.

The same article also requires us to tell **affected users** about the vulnerability and about any
fix or workaround they can apply themselves. Reporting to a CSIRT does not discharge that, and it is
not at our discretion, so if a version you run is being exploited you will hear from us.

Two things this does not mean. It is not public disclosure, and it does not shorten or override a
coordinated disclosure timeline we agreed with you. It also does not apply to an ordinary report: the
legal threshold is reliable evidence that someone exploited the issue against a real system without
permission, so a proof of concept, a severity score or a reachable weakness does not meet it. We will
tell you if a report of yours crosses that line.

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
