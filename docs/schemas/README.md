# Paper Sync schemas

Paper Sync contracts are versioned independently from application releases.

- `paper-sync-result.v1.schema.json` describes the API result returned to clients.
- `Ben.PaperSync.Contracts/Analyzers/paper-sync-auto-v1.json` defines the Azure
  Content Understanding custom analyzer that produces provider output.

Changes within a schema major version must be additive. Existing properties cannot be
removed, renamed, made more restrictive, or change meaning. A breaking change requires
a new schema file and schema version, while the server continues to support the previous
version during migration.

The API schema and analyzer definition are embedded in `Ben.PaperSync.Contracts` so
the server, client, deployment, and provider integration use the same checked-in
definitions.
