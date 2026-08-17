# Pull request

## Summary

Describe the change and its user or operator impact.

## Why

Explain the problem, evidence, and reason for this approach.

## Changes

- Describe the focused change.

## Architecture and security

- [ ] The two-Container-App and listener/backend isolation remains intact, or
  the approved design change is explained.
- [ ] Authentication, authorization, identity, networking, state, certificate,
  and secret boundaries were reviewed.
- [ ] No token, secret, tenant/subscription/application/principal ID, state,
  tfvars, plan, certificate, or private log is included.
- [ ] Hosted Agent/IQ changes preserve session ownership, citations, evidence
  redaction, and Embedded rollback.
- [ ] Documentation and tests reflect behavior changes.

## Validation

List the exact commands and results:

```text
<commands and results>
```

## Deployment and rollback

- Azure resources affected: `none` / describe
- Terraform state affected: `none` / `infra` / `agent-infra`
- Application Gateway change or restart: `no` / explain approval
- Deployment required: `no` / describe separately gated action
- Rollback: describe, or `documentation-only`

## Reviewer notes

Normal contributions target `uat`. Promotion from `uat` to protected `main`
requires a separate reviewed pull request.
