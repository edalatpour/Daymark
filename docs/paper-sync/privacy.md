# Paper Sync privacy and data handling

Paper Sync uploads a planner-page image to Daymark's Azure environment so an Azure
OpenAI vision model can extract tasks, notes, and the page date. Schedule, appointment,
calendar, and time-slot areas are excluded from extraction.

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

## Azure OpenAI processing

Microsoft states that prompts, completions, embeddings, and training data submitted to
Azure OpenAI are not available to OpenAI or other customers and are not used to train
foundation models. See [Data, privacy, and security for Azure OpenAI Service](https://learn.microsoft.com/legal/cognitive-services/openai/data-privacy).

Azure OpenAI abuse monitoring may retain prompts and model output for up to 30 days and
may review flagged content. This service-side retention is separate from Daymark's
30-day image lifecycle policy. Eligible Azure customers can apply for modified abuse
monitoring; until an exemption is approved for the production subscription, Daymark
must disclose the default abuse-monitoring behavior.

## Application safeguards

- Azure OpenAI and Blob Storage use managed identity; local/key authentication is
  disabled.
- Transport uses HTTPS with TLS 1.2 or later.
- Logs and telemetry must not contain images, prompts, model output, or raw user IDs.
- Model output is treated as untrusted, schema-validated data and is shown for user
  review before anything is applied.
