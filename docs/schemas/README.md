# Paper Sync schemas

Paper Sync contracts are versioned independently from application releases.

- `paper-sync-result.v1.schema.json` describes the API result returned to clients.
- `paper-sync-llm-output.v1.schema.json` describes the strict structured output requested from the vision model.

Changes within a schema major version must be additive. Existing properties cannot be
removed, renamed, made more restrictive, or change meaning. A breaking change requires
a new schema file and schema version, while the server continues to support the previous
version during migration.

Both schemas are embedded in `Ben.PaperSync.Contracts` so the server, client, and model
integration validate against the same checked-in definitions.
