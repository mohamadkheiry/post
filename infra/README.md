# Full-development infrastructure

`compose.full.yaml` is the connected development overlay. It keeps the default
Mock stack small while providing SQL Server (five databases), Redis, MinIO with
bucket-default SSE-S3, Redpanda, and an optional observability profile. Only the
Gateway and Edge publish loopback host ports.

All upstream images are version-and-digest locked. The lock list is verified by
`scripts/infra/verify-image-locks.ps1`; updating a tag and digest is an explicit
dependency change.

The optional observability profile uses Prometheus (Apache-2.0), Grafana and
Loki (AGPL-3.0), and Grafana Alloy. Redis 8 and MinIO also carry licenses that
must be reviewed for the target distribution/hosting model. SQL Server uses the
Developer edition and is not licensed for production use. The local two-node
`ha` profile is a development/resilience exercise, not a production HA design.

Alloy receives read-only access to the Docker socket for local log discovery.
Even read-only socket mounts are highly privileged; do not carry this topology
into production. Application availability never depends on the observability
containers, so exporter/collector outages remain non-fatal.
