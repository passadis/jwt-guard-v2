| Layer     | Test                     | Expected result |
| --------- | ------------------------ | --------------- |
| Terraform | Format                   | Pass            |
| Terraform | Validate                 | Pass            |
| .NET      | Release build            | Pass            |
| UI        | SPA loads                | 200             |
| UI        | MSAL sign-in             | Success         |
| UI API    | `/api/whoami`            | 200             |
| Gateway   | Missing token to `/enter`| 401             |
| Gateway   | Wrong audience           | 401             |
| Gateway   | Correct audience         | 200             |
| Gateway   | Tampered token           | 401             |
| Identity  | SentinelGate result      | Canonical IDs   |
| Backend   | Host and TLS/SNI name    | SentinelGate ACA FQDN |
| Context   | Matching `x-original-host` | Consistent routing only; not JWT proof |
| Routing   | UI → SentinelApp only    | Pass            |
| Routing   | API → SentinelGate only  | Pass            |
| Network   | Direct access to each ACA| Blocked         |
| Agent     | Live config tool         | Accurate        |
| Agent     | Log query                | Real telemetry  |
| Recovery  | Restart plus config push | Matrix passes   |
