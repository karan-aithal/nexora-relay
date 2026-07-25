# 0017 — `ITransactionDispatch` port over RabbitMQ store-and-forward with a dead-letter queue

## Context

Phase 5 requires an authorisation dispatch path with a store-and-forward queue, a durable
dead-letter queue, and consumer idempotency by transaction id (CLAUDE.md section 5 locks
RabbitMQ). But CLAUDE.md section 2 is equally firm that CI must run the full test suite on
Linux with no broker. Both must be true from the same code.

## Decision

- **A port, `ITransactionDispatch`, with two real implementations.** `RabbitMqDispatch` (the
  production path) and `InProcDispatch` (CI), selected by configuration — the same
  ports-and-adapters rule as every other boundary (ADR 0001). The orchestration code cannot
  tell which is behind it.
- **`Publish` + `Subscribe(handler)` where the handler returns `Ack | Retry | DeadLetter`.**
  Delivery is at-least-once; the handler decides acknowledgement, redelivery or dead-lettering.
  This is the smallest surface that expresses store-and-forward with a DLQ.
- **RabbitMQ shape:** a durable `settlement` queue declared with a dead-letter exchange routing
  to a durable `settlement.dlq`; messages published persistent; `BasicQos(prefetch=1)` so a slow
  consumer cannot swallow the queue; redelivery count read from the broker's `x-death` header,
  and past a limit the message is dead-lettered for good.
- **Idempotency lives in the consumer, not the transport.** `SettlementConsumer` dedupes on
  transaction id and acks a duplicate as a no-op — which is what turns at-least-once delivery
  into an exactly-once effect, and pairs with the crash-recovery re-dispatch of ADR 0016.
- **RabbitMQ.Client 7.x**, the fully-async client, matching the project's no-blocking rule.

## Consequences

- CI tests the queue *semantics* — redelivery on `Retry`, dead-lettering past the limit,
  consumer dedupe — against `InProcDispatch` with no broker; the demo exercises `RabbitMqDispatch`
  against a real `rabbitmq:4-management` container.
- The reversal path reuses the same queue (a `reversal-advice` message) rather than extending
  the auth-only `IHostConnection`, keeping the host port small.
- New dependency `RabbitMQ.Client` is justified by a locked technical decision; `Microsoft.Data.Sqlite`
  and its native bundle are pinned above their advisory-flagged transitive versions.

## Alternatives considered

- **Wire `RabbitMQ.Client` straight into the orchestrator.** Rejected: breaks the "CI on Linux
  without a broker" rule and the "mock at the port" rule in one move.
- **Idempotency in the transport (broker-side dedupe).** Rejected: transaction-id dedupe is a
  business rule the consumer owns; it must also work for the in-proc path.
- **TPL Dataflow instead of a queue abstraction.** Rejected: store-and-forward across a process
  restart needs durability the in-memory dataflow cannot give; RabbitMQ is the locked choice.
