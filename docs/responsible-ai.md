# Responsible AI and transparency FAQ

## What is the intended use?

This repository exists solely for training, experimentation, and discovery of Azure services, Microsoft Foundry, and MAI models. Comic boards and illustrated books provide a practical learning scenario, not a production publishing service or a commercial product.

It is not intended for consequential decisions, processing sensitive information, or unattended real-world content publication. No suitability, compliance certification, or production readiness is asserted.

## Which models generate content?

`MAI-Thinking-1` prepares comic scaffolds, rewrites panel prompts, creates book plans, and writes chapter prose. `MAI-Image-2.5-Pro` creates comic panels and chapter illustrations. See [architecture](architecture.md#foundry-models) for the configured model versions and upgrade policy.

The application invokes models through explicit service operations. It does not perform web research, verify facts, or retrieve grounding documents from AI Search in these flows.

## What can go wrong?

- Text can contain invented facts, stereotypes, bias, contradictions, or unsuitable material.
- Images may contain visual errors, unintended content, unreadable lettering, or inconsistent characters.
- Reference images guide comic character identity but cannot guarantee fidelity.
- Chapter context helps continuity but does not ensure it; preserved prose/images can become inconsistent after plan edits.
- Outputs may resemble protected works or brands. Neither the application nor its MIT license guarantees rights to generated content.
- Availability, latency, quotas, model defaults, and behavior can change. Automatic model-version upgrades can change outputs.

Review every result before use or sharing. Do not rely on generated content as factual authority or as a replacement for professional judgment.

## What content-safety controls are present?

The Bicep model deployments select the `GuardrailNoLimits` RAI policy. The presence or name of a policy is not evidence of a complete moderation or compliance solution; inspect the actual policy and applicable service settings before running a workshop.

The board worker recognizes image content-safety rejections, records a panel-level error, and composes a placeholder rather than silently replacing the rejected image. Technical errors use a different failure/retry path. Chapter generation records separate prose/image errors.

There is no application-level human approval service, comprehensive moderation pipeline, rights verification, or abuse-management system. Do not bypass service safeguards. These sample behaviors must not be treated as production safety controls.

## What data is sent and stored?

Story ideas, prompts, creative context, and selected references are sent to the configured Azure model services as needed. Book prose requests include the saved global plan and previous chapter prose. Chapter illustrations currently use text prompts, not reference sheets.

Cosmos DB stores owner-scoped projects, characters, plans, prose, prompts, statuses, errors, and artifact references. Blob Storage holds uploaded references and generated PNGs. Queues carry generation identifiers and ownership/routing metadata. Telemetry and exceptions may contain operational identifiers; review logging and access before using any real data.

Use synthetic, non-sensitive data. Do not upload secrets, personal information, confidential material, or third-party content without authorization. Consult the applicable Azure/model service terms for processing, retention, and regional commitments; the repository makes no additional data-residency promise.

## What generation trace and usage data is retained?

Each image or text request can retain its provider, configured model/deployment name, operation, safe request/correlation identifier, UTC start/completion times, duration, outcome, and the input, output, and total token counts supplied by the provider. Image traces also include requested image count and dimensions. Prompt rewriting, story scaffolding, book planning, chapter prose, and illustrations are tracked separately, including retry attempts where captured.

The compact AI indicator beside generated content opens details on hover, keyboard focus, or touch. `Provider-reported` means the model response supplied usage, not that the application independently verified billing. Unavailable counters stay unknown, never zero; an image may instead show its count and size. Partial counters do not imply a known total. The contract distinguishes estimated usage, but no estimates are currently produced. Usage is not a price quote or billing ledger.

Trace records do **not** contain API keys, access tokens, authorization headers, endpoints, raw provider responses, or hidden/internal reasoning. Only allowlisted response metadata is retained; existing owner-scoped prompt storage is unchanged. Metadata capture failures do not deliberately discard otherwise usable generated content.

Cosmos retains traces alongside their owner-scoped project, panel, plan, or chapter. Regeneration can retain multiple request records, and editing text does not erase the history of the original model call or imply that the edited text is unchanged AI output. Permanent project deletion removes its trace records and linked scaffold sidecars along with the other artifacts.

Before a draft becomes a project or board, its scaffold trace is stored in a private, owner-scoped Blob sidecar. The authenticated API resolves its opaque ID when the draft is saved; browser-supplied token counts are not accepted as provider usage. Abandoned scaffold sidecars have no automatic expiration: remove the environment to remove these unclaimed records. Other saved trace records also have no automatic expiration; project deletion or environment removal is required. Review this retention model before workshops, and never put sensitive data into prompts or reference images.

## Who can access artifacts?

The application derives the owner from authentication and uses owner-partitioned Cosmos queries and owner-scoped artifact paths. Downloads go through the API; the application does not publish anonymous blob links. Backend data/model services use private endpoints and managed identity.

This is not a security certification or a substitute for reviewing the [actual trust boundaries and limitations](architecture.md#identity-and-secrets). Protect workshop accounts and Azure access. Do not publish deployment outputs or credentials in feedback.

## Who pays, operates, and supports the environment?

The deploying user or organization is responsible for Azure charges, quotas, service terms, monitoring, permissions, and cleanup. Some resources incur charges while idle. See [requirements](requirements.md) and [resource removal](deployment.md#remove-an-environment).

This is an independent community project, not an officially supported Microsoft offering. No warranty, support commitment, uptime commitment, or service-level agreement is provided.

## How do the educational disclaimer, copyright, and MIT license relate?

The root [LICENSE](../LICENSE) contains the standard MIT grant, attribution requirement, warranty disclaimer, and liability limitation, with `Copyright (c) 2026 MACK Mathieu`.

The educational-only statement describes the repository's purpose and the maintainers' intended use. It does not impose an additional license restriction: MIT permissions, including commercial permissions, remain unchanged. Do not describe MIT as a non-commercial license.

Preserve the MIT notice and applicable third-party attribution when redistributing copies or substantial portions. Some existing source/infrastructure headers identify Microsoft Corporation; retain existing notices rather than asserting ownership of third-party material. Such notices do not imply that Microsoft endorses or supports this repository as a whole.

Dependencies, Azure services, models, trademarks, uploaded references, and generated content can have separate terms or rights. MIT applies to the licensed repository material; it does not transfer third-party intellectual-property rights or replace service terms. Microsoft, Azure, Foundry, and MAI names remain subject to their owners' rights.

## What should a workshop facilitator check?

1. Use a disposable training environment, authorized accounts, and synthetic inputs.
2. Confirm model access, quota, policy configuration, budgets, and participant permissions.
3. Explain which work continues in the background and which requires the page to stay open.
4. Review generated content and references for appropriateness, privacy, factual claims, and rights.
5. Avoid including private inputs or full Azure environment outputs in logs, screenshots, or issues.
6. Remove the environment and review remaining resources after the exercise.
