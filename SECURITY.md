# Security Policy

JWT Sentinel demonstrates security controls and includes preview Azure
capabilities. Please report suspected vulnerabilities privately and avoid
testing against infrastructure you do not own or have permission to assess.

## Supported versions

| Version | Support |
| --- | --- |
| Current `main` branch | Supported |
| Current `uat` branch | Best-effort pre-release support |
| Older commits, forks, or deployments | Not supported |

## Reporting a vulnerability

Do not disclose a vulnerability in a public issue, discussion, pull request,
log, screenshot, or evaluation artifact. Contact the maintainer privately using
the contact methods on the [GitHub profile](https://github.com/passadis). If a
private channel is temporarily unavailable, open a public issue requesting a
private contact channel without including vulnerability details.

Include, when available:

- the affected commit and component;
- prerequisites and a minimal reproduction using synthetic data;
- expected and observed behavior;
- security impact and affected trust boundary; and
- suggested mitigation.

Never send active bearer tokens, client secrets, Terraform state, populated
tfvars, certificates, connection strings, tenant data, or production logs. If a
credential may have been exposed, revoke or rotate it before reporting.

You should receive an acknowledgement within three business days and an initial
assessment within seven business days. Timelines for remediation and disclosure
depend on severity, reproducibility, preview-service dependencies, and provider
coordination.

## Scope and safe testing

Security research must use infrastructure you own or are explicitly authorized
to test. Do not attempt denial of service, access another user's data, weaken a
running gateway, bypass cost controls, or publish retrieved private content.

Particularly sensitive boundaries include:

- Application Gateway JWT validation and listener/routing-rule attachment;
- SentinelGate ingress and injected-identity parsing;
- BFF token forwarding and fixed-origin enforcement;
- Hosted Agent session ownership and evidence-broker authorization;
- Foundry IQ corpus boundaries and citation integrity; and
- Terraform state, Entra applications, certificates, and managed identities.
