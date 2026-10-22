# Order file-create original input and error responses

## Retained source

Original initial commit `5fac706a7983a6d359b39acbd670e6800afe020e`, parents `[]`, owns `Maliev.OrderService.Api/Controllers/FilesController.cs`, blob `3621dbe000faa67a60fda392f1ec80c56f50f255`.

CreateOrderFileEntryAsync at line 56 checks bucket with IsNullOrEmpty first and returns `BadRequest("bucket is required")` at line 60; then checks objectName and returns `BadRequest("objectName is required")` at line 65; then looks up the parent and returns `NotFound("Order not found")` at line 71. Success copies literal metadata and returns GetOrderFile with the new identifier.

The bounded retained controller history contains only the initial commit; its blob remains identical at model-update commit `72eb9f1949176392141951d35e6e06f7c30af4c2`. This is no latest-checkpoint or whole-source closure claim.

## Current gap and bounded change

Base protected main `d9d8b52fff8ec51c508c0339eea376d9570bb373`, tree `61aaabff38e5ad8366f1f952f8d58e84e637930c`, combines whitespace checks into an empty 400 and returns an empty 404 for the missing-parent service result.

Only this POST action changes: restore ordered IsNullOrEmpty checks and the three exact source messages. Nonempty literal arguments, including whitespace in direct controller calls, pass unchanged to the existing service. This preserves the source callable-controller distinction between empty and whitespace; it does not claim that every such value is admitted by normal HTTP query binding.

Current nonnullable FromQuery signatures, MVC binding/implicit validation, routes, named Location, permission/resource scope, response declarations, filters, successful result mapping and all other actions remain unchanged. The accepted PUT null guard remains unchanged. Original-style empty messages are proved at direct controller boundaries; existing HTTP blank-query rejection tests remain in the focused gate. This packet makes no complete original MVC/query-wire or error-envelope parity claim.

The existing OrderRepository implements IOrderService directly. Its OrderFileLifetime create method checks the parent, validates the existing bucket length, copies literal bucket/objectName, serializes existing metadata replay, and writes through existing lifetime locks. No service, repository, length/schema, replay/locking, timestamps, database, GCS transfer, credentials, financial or status-transition code changes. Comment/TrackingNumber and all financial/provider/ancestry/data/cloud/deployment holds remain blocked.

## Regression inventory

- New OrderFileCreateOriginalResponseTests: 12 strict-mock cases. Seven null/empty argument combinations require exact ordered string-valued 400 before service access; three nonempty literal cases require unchanged request/token and CreatedAtRoute result; two missing IDs require exact string-valued 404 and one delegated call.
- New OrderFileCreateMissingParentHttpTests: four normal Program/RS256/PostgreSQL/Redis cases using the existing readiness fixture. Two Accept types require exact text/plain or JSON string response, no Location and fresh no-row metadata/parent readback. Anonymous and read-only actors cannot obtain the write response or create metadata.
- Existing focused filter appends only these two exact classes. The file metadata lifecycle/blank-query tests, previous null guards, strict result rosters and all existing thresholds remain unchanged.

The new tests do not replace authority middleware, controller/service or repository in the HTTP path. The direct strict mocks characterize callable results and delegation separately.

## Acceptance and resources

Author verification is source-only: retained source SHA/parent/path/blob/action mapping; current direct service/repository, Program and fixture inspection; unique reversible literals; immutable blob readback and exact tree/inverse proof. Release build, 16 new cases, focused/full suites, formatting, static/security, actual HTTP/query/database behavior and raw coverage are **not run author-side**. Root owns publication and exact-head GitHub Actions/protected-main acceptance.

No native SDK/test host, container, database, official-log fetch, ref, original checkout write, cloud or deployment operation was performed. No helper resources were allocated.
