# Replacement and Rework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax.

**Goal:** Track approved post-delivery replacements with independent production/QA/shipping and protected evidence.
**Architecture:** Add Order-owned case aggregate and durable command receipts/audit alongside existing Order tables. Domain decisions do not write original orders; APIs consume current owner identity and shared producer receipts. Native execution as explicitly directed by parent; independent final review.
**Tech Stack:** .NET10/C#, EFCore10/Npgsql/PostgreSQL18, existing JWT/live permissions, Redis, Intranet Blazor/BFF.
**Spec:** docs/superpowers/specs/2026-10-08-replacement-rework-design.md

## Global Constraints
- Preserve legacy55actions/56routes and all14status shortcuts; external IDs scalar only.
- Existing Order/OrderStatus separate DBs; no production migration/deploy/live-order amendments.
- FileService stable DocumentId/VersionId Guid; CaseId/AttemptId/ShipmentId positive int.
- Unavailable document/accounting authority never becomes successful verification or financial issuance.
- No original quantity/status/tracking/invoice mutation and no automatic customer messages.

## Review Focus
- Concurrent attempts must not allocate the same outstanding affected unit twice.
- Failed shipment must not silently become successful delivery or open duplicate demand.
- Mixed-customer original IDs or stale receipt identity must be rejected before writes.
- No-return waiver must not fabricate returned quantity.
- Retry with same operation but altered payload must conflict.

### Task1: Domain lifecycle and quantity rules
Files: Domain/Replacement/ReplacementCase.cs; Tests/Replacement/ReplacementCaseTests.cs.
Produces: ReplacementCase.Create(int customerId,ReplacementReason reason,IReadOnlyList<ReplacementOriginal> originals); Approve(...); StartAttempt(...); CompleteQa(...); Ship(...); ConfirmDelivery(...); RecordReturn(...); Close(...). All use explicit actor/time/evidence stable references; records/read-only snapshots protect history.
- [x] Write domain tests for three damaged parts and one mold, return gates, partialQA/new attempt, shipped caps, failed-delivery retry, closure/claim independence, wrongcustomer/negative/overflow inputs.
- [x] Run filtered tests and retain missing-feature RED.
- [x] Implement exact lifecycle and checked demand caps; audit all decisions; keep original snapshots.
- [x] Run filtered tests and commit only after green.

### Task2: Durable persistence and owner lineage
Files: Data/Replacement/ReplacementModelConfiguration.cs, ReplacementStore.cs; Domain/Replacement/ReplacementPersistence.cs; Tests/Replacement/ReplacementStoreTests.cs; existing OrderDbContexts.cs + migration/snapshot ONLY after narrow owner coordination.
Consumes Task1 aggregate. Produces store CreateAsync/ExecuteAsync/GetAsync with actor-scoped operation GUID, canonicalpayload SHA256, ExpectedRevision, transaction and durable receipt. GET lineage projects persisted exact customer/IDs/revision.
- [x] Write PG18 tests for concurrent attempt cap, lostack replay/alteredpayload conflict, rollback, current exact customerread, originalrows unchanged.
- [x] Observe RED then implement serialized case command/version/audit/receipt; generate additive migration and inspect SQL against owned context handoff.
- [x] PG18 tests green; schema forward and legacy model tests; commit.

### Task3: Protected case API and shared evidence consumer
Files: Application/Replacement/ReplacementContracts.cs, ReplacementService.cs, ReplacementEvidenceClient.cs; Api/Controllers/ReplacementCasesController.cs; new Api/ReplacementRegistration.cs + minimal Program registration after owner coordination; Tests/Replacement/ReplacementHttpTests.cs.
Produces /replacementcases create/read/lineage plus typed lifecyclecommands. New permissions legacy.replacements.read/write/approve with /replacementcases/{caseId}; approval critical live checks. Missing404, denial403, version/idempotency409, invalid400, dependencies503. Same actor currentauthority; unavailable shared module failclosed.
- [x] Tests for unauthorized/denied/stale/mixedcustomer, frozen PascalCase receipt, exact lineage/documentversion checks and no finance/notification side effects.
- [ ] RED then implement producer adapters using acceptedownerpins; defaultoff until accepted producer available.
- [ ] Full API/route/OpenAPI/permission tests and actual PG HTTP command replay; commit.

### Task4: Intranet replacement workflow
Files: new Contracts/ReplacementContracts.cs; Bff/Orders/ReplacementProxies.cs; Client.Features.Orders/Components/ReplacementPanel.razor; minimal endpoint/page registrations coordinated with Intranetowner; Tests/ReplacementWorkflowTests.cs.
Consumes Task3 ownerproducer/Task2 lineage only. UI preserves originalsummary, separate case/attempttimeline, approval/return/production/QA/shipping and privateevidence selection; original fieldedit never sends replacement quantities.
- [x] UI/BFF tests correctcustomer exactIDs, privateevidence producerunavailable, partial/multipleattempts and recoverytimeline; English/Thai keyboard/error/retry flow.
- [x] RED then implement BFF CSRF/sessionauthority/commands and component.
- [ ] Native/browser tests plus full affected suite and producerintegration; commit.

### Task5: Operational costs/claims and final acceptance
Files: Domain/Replacement/ReplacementFinancialReferences.cs and focused tests; service/API/UI add explicit commands. Costs/claim facts record exactcurrency/amount/date and originalinvoice/quote; optional billingOperationId only from acknowledgedproducer. No credit/tax/payment APIs invoked automatically.
- [x] Test remedyclosure while claimpending, immutablecorrection, refund/credit separation and nonnegative currency amounts.
- [ ] Implement records; green focused and affectedfull, Release0warnings/errors, format,vulnerability,gitleaks.
- [ ] Fresh wholebranch reviewer; meaningful findings get regression-first fix; PR + exactheadCI and post-main checks; issue remainsopen until delivered. No productionactivation.

## Current acceptance checkpoint

Foundation domain/persistence is committed in Order PR62. API/service, cost/claim observations, Intranet BFF/UI and retained review regressions are implemented locally. Actual PostgreSQL HTTP lost-ack test passed with controlled authority/evidence; this is not a live producer fixture. Task3 accepted Auth/File adapter and Task4 producer integration remain blocked; defaults are off. Remaining full-suite/coverage/post-style browser validation and coherent commits/PR updates are queued through the migration coordinator. Issue61 remains open. Original migration takes native/shared-path priority.
