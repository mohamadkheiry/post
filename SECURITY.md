# Security policy

This repository must not contain live credentials, private keys, database connection strings, device tokens, or exported production data.

Use environment variables, Docker secrets, or the deployment platform's secret manager. Values in `.env.example` are names only. Local `.env*`, `secrets/`, certificates, SQLite runtime files, logs, and supplied confidential source documents are excluded from Git.

If a secret is committed, revoke or rotate it immediately, remove it from the complete Git history, and document the incident without reproducing the value. Redacting only the latest commit is insufficient.

Device tokens, administrator credentials, authorization headers, postal-service credentials, parcel images, and device IP addresses must never be logged. Correlation IDs may be logged but must not be used as secrets.

Report vulnerabilities privately to the repository owner. Do not include exploitable production details in a public issue.

## Credential lifecycle

- Edge-to-Core and sorting-device secrets are returned only on the first successful
  provision/rotation response. Persist them directly in an approved secret store;
  replay intentionally returns no raw value.
- The destination-lookup OAuth username/password supplied in the original private
  archive was exposed as plaintext before this repository existed. It is used only
  from the local .NET user-secret store for the authorized validation, must never be
  copied into Git or test evidence, and must be revoked/rotated after validation.
- `POST_EDGE_SQLITE_KEY` and its temporary previous-key counterpart are external
  key material for the encrypted Edge DB/WAL/SHM and backups. Losing both the key
  and its protected backup makes the retained local database intentionally
  unrecoverable.
- Synthetic full-development secrets live only below the ignored
  `.secrets/full-dev/` path. They are not production values and must not be reused.

Run both secret gates before every commit and again after the local runtime has
been exercised:

```powershell
.\scripts\verify-no-secrets.ps1
.\scripts\verify-operational-secrets-absent.ps1 `
  -EnvSecretDirectory .secrets/full-dev `
  -ComposeProject post-platform-final
```

The second command compares configured operational values exactly without printing
them and scans candidate repository files, retained runtime logs, the complete Git
blob history, and scoped container logs.

## Supply chain and runtime boundary

- Release builds use warnings-as-errors and the repository-pinned SDK/runtime.
- Direct and transitive NuGet packages must pass the machine-readable vulnerability
  and deprecation gate in `scripts/verify-dotnet-packages.ps1`.
- Third-party and .NET container bases are version-and-manifest-digest locked and
  verified by `scripts/infra/verify-image-locks.ps1`.
- Only Gateway and Edge may be published from the local topology. Core backends,
  SQL, Redis, Kafka-compatible messaging, and S3-compatible storage remain on the
  internal network.
- Production must fail closed without TLS, authentication, required key material,
  owned SQL connections, and approved external integration configuration.

## Reporting and response

Preserve correlation IDs, affected revision, timestamps, and redacted reproduction
steps. Do not attach tokens, request/response bodies, pre-signed URLs, parcel images,
device IP addresses, database copies, or the confidential requirements archive.
If a secret may have been disclosed, disable/revoke it first, rotate it with a new
idempotency key, scan the full history and runtime artifacts, and only then resume
service.
