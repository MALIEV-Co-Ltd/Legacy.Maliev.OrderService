# Original null-request guards for Order metadata controllers

Base protected main: `debd1d230478ee7d09cd9db8b356d5a5590f9cc9`, tree `a931c1a5d974ca241c9e8856e908999ab1137a21`. This is a bounded follow-up to source baseline issue #44 and the previously accepted Category null-request correction in PR #95. Category code/tests remain unchanged.

## Exact original ownership

Original commit `5fac706a7983a6d359b39acbd670e6800afe020e` is the initial commit and has no parent. This slice concerns exactly these complete owned controller paths:

| Original path | Original blob | Original guarded actions |
| --- | --- | --- |
| Maliev.OrderService.Api/Controllers/FileFormatsController.cs | 5d6da037da66140c0a47b0803a838d94cc44e219 | CreateFileFormatAsync and UpdateFileFormatAsync |
| Maliev.OrderService.Api/Controllers/ProcessesController.cs | d65fe4fff0427579467baf94333117776451de11 | CreateProcessAsync and UpdateProcessAsync |
| Maliev.OrderStatusService.Api/Controllers/OrderStatusesController.cs | 65d40f242c27d82c22359748f2beac339361b692 | CreateOrderStatusAsync and UpdateOrderStatusAsync |

Each original action checks `item == null` and returns empty BadRequest before entity construction or lookup/storage access. Retained path history contains only the initial commit for these paths. The later source checkpoint object is unavailable in the retained mirror; no latest-checkpoint or whole-original parity claim is made. No whole initial inventory or configuration/credential files are needed for this association.

Current target preimages are FileFormats `e168277a950f4f24a38c7be7600d92269aae2cd2`, Processes `dfa7a28cc57fe9c87842846a1d13a3b0e13b1482` and OrderStatuses `feb7a351d81b4148aaa86511f530cc5bcfd67acb`. They omit these explicit guards and dispatch null direct calls to the service boundary.

## Observable change and compatibility

Restore exactly six explicit null guards. Create/update actions now return an empty HTTP 400 result before invoking the service when the callable controller receives a null request. Valid-request service calls, CreatedAtRoute names/values, NoContent/NotFound mapping and every other action remain byte-identical outside the six literal replacements.

This is the explicit callable-controller source boundary. Normal ApiController input binding may already reject JSON null before the action; this packet does not claim an observed current HTTP 500 or alter binding behavior. Routes, HTTP verbs, method signatures, DTO property names/nullability, PascalCase/null omission, authentication, permission attributes, cancellation, critical-write policy and all model/storage contracts remain unchanged.

The three new Facts and six theory rows follow the accepted Category precedent: strict IOrderService mocks reject any invocation; exact BadRequestResult proves empty 400; VerifyNoOtherCalls confirms no create/storage or lookup dispatch. Updates cover IDs 101 and int.MaxValue. No container, database, provider, network or financial runtime is needed by these nine regressions.

The existing focused source workflow appends only the exact new test-class filter, preserving every prior selected class. The unfiltered required suite remains unchanged. Existing source-status and query/sort result rosters remain unchanged because their filters do not select this new class. No workflow action/dependency pin, trigger, permission, deadline, coverage floor or artifact policy changes.

## Validation and limits

Author verification is source-only: exact original SHA/parent/path/blob association, current target/accepted Category pattern inspection, unchanged route/permission declarations, unique forward/inverse literal replay, exact immutable postimage readbacks, nontruncated six-leaf tree delta and actual inverse restoring the declared base before handoff. Independent actual-source review is required before Root publication.

Release build, nine new cases, affected full suite, existing focused cohorts, formatting/static/security/audit/contracts and unchanged coverage gates are **not run** by the author. Root owns exact-head GitHub Actions validation, protected publication/merge and exact-main acceptance. No old pass substitutes for this head. Historical source runtime/binary execution is not claimed.

No financial-stopped runtime, SDK/test host, container, schema/data operation, cloud workload, credential, canonical checkout, ref or private-ledger change occurs here. Comment/TrackingNumber, FX, Quotation #183 Hub provenance and deployment/data holds remain in effect. This bounded three-path family earns no whole-source closure or duplicate acceptance credit.
