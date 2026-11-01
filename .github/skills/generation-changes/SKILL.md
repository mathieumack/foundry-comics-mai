---
name: generation-changes
description: "Implement or review Microsoft Agent Framework, MAI prompts, structured responses, generation retries, usage traces, and provider integration. Use for AsAIAgent, book plans, prompt rewriting, throttling, cancellation, or generation regressions."
---

# Generation changes

## Procedure

1. Read the [generation architecture](../../../docs/architecture.md) and identify the owning API/worker path. Board generation and book planning are queued; chapter prose/images are foreground API work.
2. Locate the existing provider adapter, prompt, normalizer, trace capture, status transitions, and regression tests before adding logic.
3. Check installed package references before using Agent Framework/OpenAI examples. Reuse existing adapters and linked trace-capture sources.
4. Define observable acceptance criteria: structured output, ordering/limits, invalid response behavior, cancellation, provider retry guidance, trace counters, and persisted progress as applicable.
5. Add deterministic cases for the affected behavior:
   - Valid output and malformed/incomplete structured responses.
   - Missing versus explicit-zero usage fields and permitted trace metadata.
   - Throttling/Retry-After, transient versus terminal failure, and cancellation.
   - Duplicate delivery or concurrent requests without overwriting completed work.
6. Preserve owner-scoped persistence, bounded concurrency, leases, and existing retry/status semantics. Do not log raw provider responses or sensitive inputs.
7. Run focused tests using the [development validation matrix](../foundry-comics-development/SKILL.md#completion). Build the solution for cross-project changes.
8. Update architecture/functional documentation for behavior changes and report check results and limitations.

## Quality and live evaluations

Unit tests validate contracts and control flow, not prose or image quality. For intentional prompt changes, describe the expected quality improvement and compare against fixed, synthetic examples. Request explicit approval before invoking live models; record the model/version, evaluation criteria, results, and remaining uncertainty without committing private prompts or provider output. Do not claim a quality evaluation passed when it was not run.

Do not introduce hosted-agent orchestration, retrieval, new models, or paid evaluation infrastructure unless the task requires it.
