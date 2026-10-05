# Order file metadata source acceptance draft

Source: 5fac706a7983a6d359b39acbd670e6800afe020e, no parents.
Paths: Maliev.OrderService.Api/Controllers/FilesController.cs and Maliev.OrderService.Data/Database/OrderContext/OrderFile.cs. No source repository or shared ledger was modified.

The original controller creates database metadata from query Bucket/ObjectName, returns CreatedAtRoute(GetOrderFile), reads by id or parent, updates Bucket/ObjectName/OrderId and ModifiedDate while preserving CreatedDate, and deletes metadata with204/404. It does not upload bytes or invoke GCS. Source list is404 when empty.

OrderFileMetadataLifecycleHttpTests adds eleven draft cases: ordinary full lifecycle including URL-escaped object name and parent reassignment; missing parent/record operations without orphans; five unrelated-grant denials covering every metadata route; three blank-query cases; and a repeated-object case. Production Program, actual PostgreSQL/Redis repositories and real RS256 middleware use the existing OrderDeletionReadinessFixture. Only TimeProvider is controlled for lifecycle timestamps. There is no mocked repository, API or authentication.

Source-compatible lifecycle assertions cover full Location record resolution, parent-specific list movement, all HTTP/database fields, creation preservation, modification advance, missing/replayed operations and surviving parents. Parent reassignment is explicitly permitted by the original controller; the target implements deletion-lifetime locking around it.

Target policies are separate from literal source parity: granular permissions; rejecting whitespace (source rejects null/empty only); trimming metadata; preserving original parent when nullable update OrderId is omitted; and idempotent repeated object registration (source unconditionally inserts another row). The new duplicate case validates existing target policy, not original duplicate-insert behavior. Lifetime409 and admission503 remain in existing focused tests and are not inferred from this ordinary lifecycle draft.

Inspected consumer: Legacy.Maliev.Intranet/Legacy.Maliev.Intranet/Orders/LegacyOrderClient.cs escapes both query values, deserializes the create response with Id/OrderId/Bucket/ObjectName/timestamps, lists /orders/{id}/files, and deletes /orders/files/{id}. No consumer or cloud state was changed. A joined FileService/IAM/Intranet execution is still required; this draft alone does not prove it.

No build, test, coverage or runtime acceptance is claimed. Validation uses authorized GitHub-hosted workers; no local SDK/Docker runtime is authorized. Required gates remain strict Release0warnings/errors, focused/full suite, raw per-production-assembly coverage>=80% including generated lines/no exclusions, static/dependency/security and joined boundary checks before protected-main acceptance.
