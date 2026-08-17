# Contributing to JWT Sentinel

Thank you for helping improve JWT Sentinel. Contributions should preserve the
two-Container-App trust boundary, fail-closed gateway behavior, least-privilege
identities, and isolation between the Stage 1 and Hosted Agent Terraform states.

By participating, you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before starting

- Search existing issues and pull requests before proposing duplicate work.
- Open an issue for a substantial feature, architecture change, new Azure
  dependency, or behavior that changes security or cost.
- Report vulnerabilities through [SECURITY.md](SECURITY.md), never in a public
  issue.
- Never include tenant, subscription, application, principal, or tester IDs;
  tokens; secrets; Terraform state; tfvars; plans; certificates; deployment
  logs; or user data in a contribution.

## Standard contribution workflow

1. Fork `passadis/jwt-guard-v2` on GitHub.
2. Clone your fork and add the upstream repository:

   ```powershell
   git clone https://github.com/<your-user>/jwt-guard-v2.git
   Set-Location jwt-guard-v2
   git remote add upstream https://github.com/passadis/jwt-guard-v2.git
   ```

3. Synchronize with the integration branch and create a focused branch:

   ```powershell
   git fetch upstream
   git switch -c feature/<short-description> upstream/uat
   ```

4. Make small, reviewable changes. Update documentation and tests with behavior.
5. Run the relevant local checks.
6. Commit with a concise message and push your branch to your fork:

   ```powershell
   git add <reviewed-files>
   git commit -m "Describe the change"
   git push -u origin feature/<short-description>
   ```

7. Open a pull request from your fork to `passadis/jwt-guard-v2:uat`.

Normal contributions target `uat`. The protected `main` branch receives only
a reviewed promotion pull request after the integration branch is validated.

## Local validation

Run the smallest checks relevant to the change. For a complete repository pass:

```powershell
terraform -chdir=infra fmt -check -recursive
terraform -chdir=infra init -backend=false
terraform -chdir=infra validate

dotnet test tests/SentinelApp.Tests/SentinelApp.Tests.csproj -c Release
dotnet test tests/SentinelGate.Tests/SentinelGate.Tests.csproj -c Release
dotnet test `
  tests/SentinelHostedAgent.Tests/SentinelHostedAgent.Tests.csproj `
  -c Release

./scripts/test-static.ps1
./scripts/test-agent-static.ps1
Invoke-Pester -Script ./tests/PowerShell -PassThru
```

Local validation must not initialize a remote Terraform backend or access a
running deployment. Do not run an apply, destroy, image deployment, certificate
issuance, DNS update, Hosted Agent deployment, knowledge publication, or gateway
restart as part of an ordinary pull request.

## Pull request expectations

A pull request should:

- explain the problem and the chosen solution;
- stay focused and avoid unrelated cleanup;
- identify architecture, security, state, cost, or preview-feature effects;
- include tests and documentation for changed behavior;
- list the exact validation commands and results;
- use synthetic values in tests and examples;
- preserve generated-artifact and secret exclusions; and
- disclose any checks that could not be run.

Review feedback may request changes before merge. Please keep discussion
technical, respectful, and evidence-based.
