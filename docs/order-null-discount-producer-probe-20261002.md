# Nullable-discount Order producer probe

## Scope and provenance

Tests-only acceptance: `Legacy.Maliev.OrderService.Tests/Controllers/OrderNullDiscountProducerHttpTests.cs` and this document. Isolated branch `codex/order-null-discount-probe-20261002`, base OrderService `58ad11d856638c29045ec339c9085b494173fcf0`. Canonical checkout remains clean and untouched pending protected-main acceptance. Existing fixtures, runtime, schemas, workflows and financial policy are unchanged. This bounded producer slice does not close a whole source-owner migration.

Existing parents: [Web #275](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Web/issues/275), [Web #309](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Web/issues/309), [Web #177](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Web/issues/177). Root created bounded [OrderService #51](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.OrderService/issues/51) and added it to Project2 for this producer-only acceptance; it does not duplicate or close the whole parent migration. The original agent made no GitHub changes. Root owns interpretation and integration.

Committed source mirror: `B:\maliev-legacy\.artifacts\source-commit-mirror-20260930.git`, frozen checkpoint `bed10c7d15e0698e0b75f1329d0f312937f5d77f`. Source bundle `faf702a7f7990aaca6b60c652bae64fbc62987f3`, parent `3e95e397f7180356f0b5a8acfeed0119d1db62ad`, subject Restore additive STEP and batch order pricing. Its four changed paths are:

- `Maliev.Web.Tests/InstantQuotationGeometryAnalysisBrowserTests.cs`
- `Maliev.Web/Pages/InstantQuotation/3D-Printing.cshtml`
- `Maliev.Web/Pages/InstantQuotation/3D-Printing.cshtml.cs`
- `Maliev.Web/wwwroot/src/app/js/model-viewer/model-viewer.worker.js`

This probe is a narrow downstream compatibility prerequisite, not evidence for those four complete source paths or seven-part geometry/pricing fulfillment.

## Established original contract

Source `Maliev.Web/Pages/InstantQuotation/3D-Printing.Fulfillment.cs`, `CreateOrderAsync`, inspected at both faf702a7 and frozen bed10: sets Quantity and VerifiedUnitPriceThb, omits DiscountPercent, and keeps payment disabled. Source `Maliev.OrderService.Data/Database/OrderContext/Order.cs` declares nullable decimal DiscountPercent and Subtotal; its constructor only initializes OrderFile, with no discount initializer. Source `Maliev.OrderService.Api/Controllers/OrdersController.cs`, complete CreateOrderAsync, copies item.DiscountPercent unchanged. Source OrderContext configuration has decimal(5,2) with no discount default and computed decimal(18,2) Subtotal:

`UnitPrice * Quantity - ((UnitPrice * Quantity) * DiscountPercent) / 100`

There is no source null-to-zero coalescing. SQL Server source execution was not performed; its null propagation is a reading of the committed expression, not an observed source database result.

Current Web `18adfd9b881a6b6d5756a1fdcb0dbdbda3ac79cb`, `Legacy.Maliev.Web.Infrastructure/CustomerOrderSubmissionTransport.cs`, sends explicit `discountPercent:null` with draft Quantity/UnitPrice and OperationKey plus Idempotency-Key. Current OrderService UpsertOrderRequest and OrderResponse retain nullable discount/subtotal. OrderRepository maps discount unchanged; OrderDbContext uses stored PostgreSQL numeric(18,2) computation with the same uncoalesced arithmetic. No cross-domain money/ID lookup or foreign key is introduced.

## Reached boundary and evidence

Seven cases reuse unchanged `OrderDeletionReadinessFixture`: actual Program registration, Production HTTP TestServer, normal RS256 JWT authentication/permission pipeline, real Redis 7-alpine idempotency, PostgreSQL 18-alpine migrations in fresh disposable Order and OrderStatus databases. No controller/service/repository/auth replacement or permission-policy broadening. Only synthetic owned catalog Process/Category rows are seeded. External Customer/Material/Color/Finish/Currency IDs remain scalar references.

The independently written literal JSON matches the Web create shape (null employee/dates/tracking; quantity 3; unitPrice 3250.25; disabled payment/cancellation; operation key and matching header). The Web transport is not instantiated in this OrderService project: this is producer acceptance for a traced wire shape, not joined Web transport execution.

Actual POST /orders response and fresh scoped AsNoTracking PostgreSQL readback both prove:

| Discount | Persisted subtotal | Meaning |
| --- | --- | --- |
| null | null | Nullable inherited arithmetic, not an implicit zero discount |
| 0 | 9750.75 | Explicit zero control |
| 12.5 | 8531.91 | Computed numeric(18,2) rounding control |

Remaining = 3, scalar IDs, unit price, quantity, operation key and disabled payment persist. Two real Redis cases prove exact same-key/body replay returns the same response/order, while switching null and zero on the same key returns 409 without creating or changing a row. Unauthenticated and read-only-permission POST return 401/403 and leave Order empty. This does not prove IAM deployment availability, OAuth acquisition, customer ownership, concurrent first-write races, later ModifiedDate updates, order status history, receipt attribution or seven-part reconciliation.

### Diagnostic retained separately

First TRX `TestResults/order-null-discount-probe/order-null-discount-probe.trx`: 5 passed, 2 failed, 0 skipped. The added optional GET assertion observed a previously cached 12.50 discount: unchanged fixture creates separate databases sharing one Redis cache namespace and repeated Order ID 1. POST assertions passed. This was test-isolation/cache diagnostic, not nullable-discount product RED. The optional GET leg was removed from the NEW probe only; no existing assertion was modified. Required POST and fresh PostgreSQL assertions were retained. GET parity is not claimed.

Final focused TRX `TestResults/order-null-discount-postgres-final/order-null-discount-postgres-final.trx`: 7 passed, 0 failed, 0 skipped before final formatting. After formatting and a fresh zero-warning/error Release build, final freeze rerun `TestResults/order-null-discount-freeze/order-null-discount-freeze.trx` also passed 7/7, zero failed/skipped; XML counters were independently parsed. Freeze TRX SHA-256: `0F46E55E04F7034FE20C4D1D5B203EF86215D4F07C0E3EB63D97097F43B40C22`.

## Validation commands

Private dependency pins exactly match existing CI: ServiceDefaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`; CompatibilityContracts `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`. Both are ignored worktree-private `.dependencies` clones at detached pins, with private outputs.

Baseline solution build and subsequent test-project Release builds: zero warnings, zero errors.

```powershell
dotnet build Legacy.Maliev.OrderService.slnx -c Release -p:MalievWorkspaceRoot=B:\maliev-legacy\.worktrees\order-null-discount-probe-20261002\.dependencies -p:UseLocalMalievDependencies=true --verbosity minimal
dotnet build Legacy.Maliev.OrderService.Tests/Legacy.Maliev.OrderService.Tests.csproj -c Release -p:MalievWorkspaceRoot=B:\maliev-legacy\.worktrees\order-null-discount-probe-20261002\.dependencies -p:UseLocalMalievDependencies=true --verbosity minimal
dotnet test Legacy.Maliev.OrderService.Tests/Legacy.Maliev.OrderService.Tests.csproj -c Release --no-build --filter FullyQualifiedName~OrderNullDiscountProducerHttpTests --logger 'trx;LogFileName=order-null-discount-freeze.trx' --results-directory TestResults/order-null-discount-freeze --verbosity minimal
```

Scoped whitespace verification uses `dotnet format whitespace Legacy.Maliev.OrderService.slnx --verify-no-changes --no-restore --include Legacy.Maliev.OrderService.Tests/Controllers/OrderNullDiscountProducerHttpTests.cs`, with MalievWorkspaceRoot temporarily set to the same absolute private dependency directory. Initial verification identified NEW JSON initializer formatting only; scoped mechanical formatter corrected it. Full Order suite, full Web, browser, deployed services and production/persistent databases were not run: parent requested a frozen bounded two-file probe rather than an implementation acceptance claim.

Final scoped whitespace verification passed. `git diff --no-index --check -- NUL <owned-file>` passed for each new file (test CRLF normalization warning only). `Get-Content <two-owned-files> -Raw | gitleaks stdin --redact --no-banner` found no leaks. Worktree status contains only these two untracked owned paths; canonical OrderService remains clean. Private dependency SHAs were rechecked.

## Minimal recommendation and remaining gate

## Independent root acceptance

Root independently inspected the complete new test and document, original committed nullable entity, complete original POST producer, and source computed-column configuration at `bed10c7d15e0698e0b75f1329d0f312937f5d77f`. A fresh Release solution build passed with zero warnings/errors. Focus `TestResults/root-null-discount-focus/focus.trx` passed 7/7; the unfiltered affected suite `TestResults/root-null-discount-full/full.trx` passed 244/244, zero failures/skips, with raw coverage retained. Full TRX SHA256: `09DC97F3F7379FA4F6AEE7424F82DA987E8885DE6563C36407A467BD578B76E3`.

Root subsequently reran whole-solution `dotnet format --verify-no-changes --no-restore` using the same private dependency environment, and transitive vulnerable-package checks for all five solution projects; both passed. The earlier concurrent formatter is not used as acceptance evidence. No joined Web/full-browser, production SQL execution, persistent database change, or whole-source closure is claimed. Protected PR CI and post-merge main remain separate acceptance gates.

## Remaining financial and joined-boundary gates

No OrderService runtime fix is justified as source parity: null subtotal is inherited and now directly reached on PostgreSQL. Do not globally COALESCE, add a database discount default, rewrite historical rows, or reinterpret null as zero. If owner intent requires a numeric additive subtotal, obtain an explicit financial-contract decision. A narrow candidate is additive Web caller supplying explicit zero (only after approval), with actual joined transport/producer and per-part receipt tests, leaving generic nullable Order semantics intact. That would change the source caller behavior, not merely complete migration parity.

Seven-part receipt rounding, source-authoritative additive STEP geometry admission, batch per-part order mapping, status/history/attribution and disposable joined pricing-to-persistence acceptance remain independent unresolved gates under existing issues. CNC expansion/retirement, SQL provider revival, payment authority, infrastructure and deployment are excluded. The source SHA remains pending.
