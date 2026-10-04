# Paper Sync privacy and data handling

Paper Sync uploads a planner-page image to Daymark's Azure environment so Azure
Content Understanding can extract tasks, notes, the page date, confidence, and source
grounding. Schedule, appointment, calendar, and time-slot areas are excluded.

## Image storage

- Images are stored in the private `paper-sync-images` Blob container.
- Blob anonymous access and storage shared-key authorization are disabled.
- The backend accesses images with its managed identity. Clients do not receive storage
  credentials or SAS tokens.
- A storage lifecycle policy deletes base blobs, versions, and snapshots after 30 days.
- Deleting a Paper Sync job is expected to remove its image immediately; the lifecycle
  policy is the retention backstop.
- `paper-sync-training` is reserved for evaluation samples that a user explicitly opts
  in to share. Paper Sync must not copy images there without that consent.

## Content Understanding processing

Content Understanding combines Azure AI services including Document Intelligence and
customer-owned Foundry model deployments. Paper Sync provisions a supported
chat-completion model and embedding model on the same `AIServices` resource and maps
them to Content Understanding. Content Understanding-specific meters and model token
usage are billed separately.

Microsoft documents Content Understanding privacy responsibilities in
[Data, privacy, and security for Content Understanding](https://learn.microsoft.com/azure/foundry/responsible-ai/content-understanding/data-privacy).

The underlying Foundry model processing is also subject to
[Azure OpenAI data, privacy, and security](https://learn.microsoft.com/legal/cognitive-services/openai/data-privacy).
Microsoft states that submitted customer data is not available to OpenAI or other
customers and is not used to train foundation models without permission. Default abuse
monitoring can store and review model inputs and outputs under Microsoft's applicable
service terms. Daymark must confirm the current retention terms and whether Modified
Abuse Monitoring is required before production enablement.

Service-side processing and retention are separate from Daymark's 30-day Blob lifecycle
policy.

## Application safeguards

- Foundry/AIServices and Blob Storage use managed identity; local/key authentication is
  disabled.
- Transport uses HTTPS with TLS 1.2 or later.
- Logs and telemetry must not contain images, analyzer input/output, extracted text,
  source content, or raw user IDs.
- Analyzer output is treated as untrusted data, validated, and shown for user review
  before anything is applied.
- Image text is data, never an instruction. Analyzer definitions provide no tools or
  access to other accounts or application data.
