# Original order file and history null-request guards

This slice restores two callable-controller null-request branches from retained original source. It changes no valid-request path.

## Source ownership

Original initial commit: `5fac706a7983a6d359b39acbd670e6800afe020e`; parents: `[]`.

| Original owner path | Original blob | Action | Null response |
| --- | --- | --- | --- |
| `Maliev.OrderService.Api/Controllers/FilesController.cs` | `3621dbe000faa67a60fda392f1ec80c56f50f255` | `UpdateOrderFileAsync`, line 162 | `BadRequest("item is required.")` before lookup |
| `Maliev.OrderStatusService.Api/Controllers/HistoriesController.cs` | `16208817f8486876e2362462d133a288ac27c3f4` | `UpdateOrderHistoryAsync`, line 605 | Empty `BadRequest()` before lookup |

The bounded retained path histories contain only this initial commit. This is no claim of latest-checkpoint parity or whole-original-source closure.

## Current boundary and change

Base commit: `56c57bcbc4ff70f6af40d6aedbfbdfcd4c1e4639`; tree: `e2143e95d7ad28c03a8f8955f558cdd0cd2b019b`. This base is the preceding metadata null-guard draft (PR #107), pending hosted acceptance when this packet was prepared.

- `FilesController.UpdateOrderFileAsync` rejects null with the exact original string payload before `IOrderService.UpdateFileAsync`.
- `HistoriesController.UpdateOrderHistoryAsync` rejects null with an empty 400 before `IOrderService.UpdateHistoryAsync`, regardless of expected-date header presence.

Routes, JWT/permissions, critical-write flags, exception filters, signatures, DTOs, serialization, existing service/repository calls, concurrency arguments and non-null result mapping remain unchanged. No history mutation, transition, named shortcut, append-only rule, file transfer, storage configuration, migration, or financial behavior is added or changed. Comment/TrackingNumber and every financial/provider hold remain blocked.

These tests call the controller directly. ASP.NET Core's existing `[ApiController]` binding can already reject HTTP JSON null; this packet makes no observed HTTP-500 or changed HTTP-wire claim.

## Regression and hosted acceptance

`OrderFileHistoryOriginalNullControllerTests` adds six strict-mock rows: two file IDs (101 and int.MaxValue), and four history combinations of those IDs with absent/present expected date. File cases require exact `BadRequestObjectResult`, status 400 and string payload; history cases require exact empty `BadRequestResult`, status 400. Every row verifies no service calls, and history rows also verify no idempotency calls.

The focused workflow only appends this exact class to the existing filter, preserving the prior metadata class and all other workflow settings.

Author-side checks: original SHA/parents/path/blob ownership, exact forward/inverse literals, immutable blob readback, route/attribute/signature preservation and exact tree delta. No SDK, test host, Docker, database, cloud, official-log collection or ref changes were performed. No persistent helper resources were allocated.

Build, six regression rows, focused tests, affected full suite, formatting, static/security checks, contract/browser checks and raw coverage are **not run author-side**. Heavy validation stays on GitHub Actions under Root ownership. Require passing existing exact-head checks with unchanged thresholds and protected-main verification before accepting this slice. Reconcile onto the accepted base if PR #107 changes or is not merged; do not silently drop its changes.
