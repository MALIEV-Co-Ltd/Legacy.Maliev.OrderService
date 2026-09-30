# Order #44/#45 bounded audit and intended RED

Owned branch `codex/order-delete-history-fault-20261001`, clean base `b7e6ccfed23baeddc717738f746b7c6bbe74c6f8`; canonical HEAD/main/origin matched before isolation. Initial audit/delete RED evidence is retained below. A separately approved history PUT status repair and normal HTTP regressions are recorded in the final section. Source mirror and Workflows ledgers are read-only. No deletion runtime repair, commits, deployment, persistent SQL, schema, source-owner closure or other-repository edits.

## Authority and reconciled counts

Source initial `5fac706a7983a6d359b39acbd670e6800afe020e`; complete committed checkpoint `bed10c7d15e0698e0b75f1329d0f312937f5d77f` from the isolated source mirror. `source-commit-ledger.json` inventories27 Order-owned records; `source-commit-resolutions.json` separately records23 pending and4 migrated. Migration-required path classification is not the same as an unresolved owner status.

Source55 actions/56 HTTP templates includes one dual-template administrative/customer order-list action. Target preserves that customer-list URI through a separate least-privilege action and adds details/cancel:55+3=58 actions,56-1+3=58 templates. All14 named history shortcuts remain, including exact `InProgress` spelling. This is not a missing-route finding. Customer boundary approval: PR2, merge `c04d0036672596070a1aadd49c997abfd4762a50`; signed service/customer authorization PR3 `2ea1425f26904cdbc7d3a5ead28a1da71a41f556`.

PR5 expressly removed forced live IAM while retaining every authenticated exact permission, resource template and critical marker; merge `08eaef2e7c8de366f3e5a57282e1ebea446b43be`. Its body describes short-lived signed permission authority for the self-contained legacy topology. Current all58 `RequireLiveCheck=false` tests reflect that approval. AGENTS/README critical-live language is stale guidance, not evidence of an unapproved security defect. No signed-permission policy change is proposed in this lane. Actual RS256 validation is pinned by PR43/baseb7e6ccf and Defaults8f; no test-auth shortcut establishes live IAM authority.

## Route/action matrix

Rows below cover all58 current actions. I means route/action implemented, not a claim of fully equivalent source JSON. C means approved dedicated customer boundary. P/H/U/T flag the concrete compatibility questions below. All rows require controller JWT authentication plus the exact permission. Permission abbreviations: O=`legacy.orders`, C=`legacy.order-catalog`, F=`legacy.order-files`, S=`legacy.order-status`; suffixes read/create/update/delete/write are literal. Every row retains the approved non-forced-live policy. Critical markers apply to order create/update/delete/customer cancel and history append/update. Common errors: ordinary normal JWT401/permission403; absent entity or empty catalog/file/history lists404; create201; catalog/file/status mutations204; invalid body400; exceptions are not reclassified as successful writes.

| Action | HTTP / effective route | Request → response/status | Permission | Mapping |
|---|---|---|---|---|
| CreateOrderAsync | POST /orders | UpsertOrder→OrderResponse201; keyed409/503 | O.create | I |
| DeleteOrderAsync | DELETE /orders/{id:int} | none→204/404 | O.delete, /orders/{id} | I; #45 RED |
| GetOrderAsync | GET /orders/{id:int} | none→OrderResponse200/404 | O.read, /orders/{id} | I |
| GetPaginatedPendingOrderAsync | GET /orders/pending | sort/search/index/size→Page<Order>200/404 | O.read | P |
| GetPaginatedOrderAsync | GET /orders | sort/search/index/size→Page<Order>200/404 | O.read | P |
| GetCustomerOrdersAsync | GET /orders/customers/{customerId:int} | same page query→Page<Order>200/404 | legacy.customer-orders.read, /customers/{customerId}/orders | C/P; extracted source alias |
| GetCustomerOrderAsync | GET /orders/customers/{customerId:int}/{id:int} | none→Order/Process/History/Files200/404 | legacy.customer-orders.read, /customers/{customerId}/orders/{id} | C |
| CancelCustomerOrderAsync | POST /orders/customers/{customerId:int}/{id:int}/cancel | none→204/404/409 | legacy.customer-orders.cancel, /customers/{customerId}/orders/{id} | C |
| UpdateOrderAsync | PUT /orders/{id:int} | UpsertOrder + optional X-Expected-Modified-Date→204/404/409 | O.update, /orders/{id} | I |
| CreateCategoryAsync | POST /orders/categories | {Name}→Category201 | C.write | I |
| DeleteCategoryAsync | DELETE /orders/categories/{id:int} | none→204/404 | C.delete | I |
| GetCategoryAsync | GET /orders/categories/{id:int} | none→Category200/404 | C.read | I |
| UpdateCategoryAsync | PUT /orders/categories/{id:int} | {Name}→204/404 | C.write | I |
| CreateFileFormatAsync | POST /orders/fileformats | {Name,Extension}→FileFormat201 | C.write | I |
| DeleteFileFormatAsync | DELETE /orders/fileformats/{id:int} | none→204/404 | C.delete | I |
| GetAllFileFormatsAsync | GET /orders/fileformats | none→FileFormat[]200/404 | C.read | I |
| GetFileFormatAsync | GET /orders/fileformats/{id:int} | none→FileFormat200/404 | C.read | I |
| UpdateFileFormatAsync | PUT /orders/fileformats/{id:int} | {Name,Extension}→204/404 | C.write | I |
| CreateOrderFileEntryAsync | POST /orders/{orderId:int}/files | query bucket/objectName→OrderFile201/400/404 | F.write, /orders/{orderId} | I |
| DeleteOrderFileAsync | DELETE /orders/files/{id:int} | none→204/404 | F.delete | I |
| GetOrderFileAsync | GET /orders/files/{id:int} | none→OrderFile200/404 | F.read | I |
| GetOrderFilesAsync | GET /orders/{orderId:int}/files | none→OrderFile[]200/404 | F.read, /orders/{orderId} | I |
| UpdateOrderFileAsync | PUT /orders/files/{id:int} | optional OrderId + Bucket/ObjectName→204/404 | F.write | I |
| CreateProcessAsync | POST /orders/processes | {CategoryId,Name}→Process201 | C.write | I |
| DeleteProcessAsync | DELETE /orders/processes/{id:int} | none→204/404 | C.delete | I |
| GetAdditiveProcessAsync | GET /orders/processes/additive | none→Process[]200/404 | C.read | I |
| GetAllProcessesAsync | GET /orders/processes | none→Process[]200/404 | C.read | I |
| GetElectronicsProcessAsync | GET /orders/processes/electronics | none→Process[]200/404 | C.read | I |
| GetMachiningProcessAsync | GET /orders/processes/machining | none→Process[]200/404 | C.read | I |
| GetProcessAsync | GET /orders/processes/{id:int} | none→Process200/404 | C.read | I |
| GetScanningProcessAsync | GET /orders/processes/scanning | none→Process[]200/404 | C.read | I |
| UpdateProcessAsync | PUT /orders/processes/{id:int} | {CategoryId,Name}→204/404 | C.write | I |
| CreateOrderStatusAsync | POST /orderstatuses | {Name,Description}→Status201 | S.write | I |
| DeleteOrderStatusAsync | DELETE /orderstatuses/{id:int} | none→204/404 | S.delete | I |
| GetAllOrderStatusesAsync | GET /orderstatuses | none→Status[]200/404 | S.read | I |
| GetOrderStatusAsync | GET /orderstatuses/{id:int} | none→Status200/404 | S.read | I |
| GetOrderStatusByNameAsync | GET /orderstatuses/{statusName} | none→Status200/404 | S.read | I |
| UpdateOrderStatusAsync | PUT /orderstatuses/{id:int} | {Name,Description}→204/404 | S.write | I |
| GetAvailableStatusAsync | GET /orderstatuses/{currentStatusId:int}/available | none→Status[]200/404 | S.read | I |
| CreateOrderHistoryAcceptedStatusAsync | POST /orderstatuses/histories/{orderId:int}/accepted | optional Idempotency-Key→201/404/409/503 | S.write | T |
| CreateOrderHistoryCancelledStatusAsync | POST /orderstatuses/histories/{orderId:int}/cancelled | same | S.write | T |
| CreateOrderHistoryDeclinedStatusAsync | POST /orderstatuses/histories/{orderId:int}/declined | same | S.write | T |
| CreateOrderHistoryExpiredStatusAsync | POST /orderstatuses/histories/{orderId:int}/expired | same | S.write | T |
| CreateOrderHistoryFinishedStatusAsync | POST /orderstatuses/histories/{orderId:int}/finished | same | S.write | T |
| CreateOrderHistoryInProgressStatusAsync | POST /orderstatuses/histories/{orderId:int}/InProgress | same | S.write | T |
| CreateOrderHistoryNewStatusAsync | POST /orderstatuses/histories/{orderId:int}/new | same | S.write | T |
| CreateOrderHistoryPaidStatusAsync | POST /orderstatuses/histories/{orderId:int}/paid | same | S.write | T |
| CreateOrderHistoryQuotedStatusAsync | POST /orderstatuses/histories/{orderId:int}/quoted | same | S.write | T |
| CreateOrderHistoryRejectedStatusAsync | POST /orderstatuses/histories/{orderId:int}/rejected | same | S.write | T |
| CreateOrderHistoryReopenStatusAsync | POST /orderstatuses/histories/{orderId:int}/reopen | same | S.write | T |
| CreateOrderHistoryReviewedStatusAsync | POST /orderstatuses/histories/{orderId:int}/reviewed | same | S.write | T |
| CreateOrderHistoryReviewingStatusAsync | POST /orderstatuses/histories/{orderId:int}/reviewing | same | S.write | T |
| CreateOrderHistoryShippedStatusAsync | POST /orderstatuses/histories/{orderId:int}/shipped | same | S.write | T |
| CreateOrderStatusEntryAsync | POST /orderstatuses/histories/{orderId:int}/{statusId:int} | optional Idempotency-Key→201/404/409/503 | S.write | T |
| DeleteHistoryAsync | DELETE /orderstatuses/histories/{historyId:int} | none→204/404 | S.delete | I; authorized historic mutation retained |
| GetLatestAsync | GET /orderstatuses/histories/{orderId:int}/latest | none→Status200/404 | S.read | H |
| GetOrderHistoryAsync | GET /orderstatuses/histories/{orderId:int} | none→History[]200/404 | S.read | H |
| UpdateOrderHistoryAsync | PUT /orderstatuses/histories/{historyId:int} | {OrderId,OrderStatusId} + optional expected date→201/404/409 | S.write | U |

## Field/schema/wire map and outstanding parity decisions

Current HTTP serializer is System.Text.Json with PropertyNamingPolicy=null/DictionaryKeyPolicy=null and null omission. DTO field names below are exact current emitted property names. Source uses MVC Newtonsoft with null/reference-loop omission and scaffold CLR names; exact original running HTTP casing has not been executed here, so CLR-name comparison is not represented as a verified original HTTP snapshot. No original build/runtime/secret access was authorized.

- OrderResponse: Id:int; CustomerId/EmployeeId/MaterialId/SurfaceFinishId/ColorId/CurrencyId/LeadTime:int?; Name/Description/Comment/TrackingNumber:string?; ProcessId/Quantity/Manufactured:int; Remaining/Turnaround:int?; UnitPrice/DiscountPercent/Subtotal:decimal?; PromisedDate/FinishedDate/CreatedDate/ModifiedDate:DateTime?; AllowSocialMedia/AllowCancellation/AllowPayment:bool. All source scalar CLR names/types preserved; computed fields are response-only and PostgreSQL-generated. Upsert accepts the writable subset plus optional nullable OperationKey(max128), defaultnull. Name100/Description250 constructor validation is independently accepted PR37; computed/server dates are not accepted as writable response-shaped fields. Navigation graphs are deliberately absent from the current DTO; whole original serialized graph parity remains a separate boundary review.
- Category: Id:int,Name:string?,CreatedDate/ModifiedDate:DateTime?; Process: Id/CategoryId:int,Name:string,datesnullable. FileFormat: Id:int,Name/Extension:string?,datesnullable. OrderFile: Id/OrderId:int,Bucket/ObjectName:string,datesnullable; request OrderId:int? retains existing owner when omitted. Bucket/object metadata only, no embedded GCS credentials/signed URLs.
- Status: Id:int,Name/Description:string?,datesnullable. History: Id/OrderId/OrderStatusId:int,Name/Description:string?,datesnullable. History mutation request onlyOrderId/OrderStatusId. Page: Items,PageIndex,TotalPages,TotalRecords,HasNextPage,HasPreviousPage. CustomerDetails: Order,optionalProcess,History,Files.
- Separate OrderDbContext: Category/FileFormat/Process/Order/OrderFile; separate OrderStatusDbContext: OrderStatus/OrderStatusHasPossibleStatus/OrderStatusHistory. Local FK constraints preserve legacy names. StatusHistory.OrderId is an external scalar, not an Order database FK. Customer/employee/material/color/surface/currency/quotation/accounting references remain scalar, no cross-domain context/FK.
- Remaining=Quantity-Manufactured; Subtotal=(UnitPrice*Quantity)-percentage discount numeric18,2; Turnaround=FinishedDate-CreatedDate::date. Date-only promised/finished and UTC wall-clock timestamp-without-time-zone dates remain. ModifiedDate concurrency applies to order/history; not every catalog/file route is concurrency-token protected. Do not infer a universal guarantee from AGENTS shorthand.
- P: Source controller defaults omitted size to the filtered query count; current controller defaults50 and repository clamps1..250. Example51 matching orders: source default returns51/onepage; target returns50/twopages. Current out-of-range populated-page query can return200 with emptyItems whereas source controller checks Items.Count==0→404. Sort numeric values0..11 preserved;6/7 status-sort labels are not handled by either switch. Current stable ID tie-breakers/null-date ordering are deliberate deterministic additions but require owner disposition, not silent source parity.
- H: Source history list explicitly constructs OrderStatus with Id=1-based display ordinal,Name/Description/historydates. Target returns durable historyId plus OrderId/OrderStatusId. Example persisted historyId42/statusId7: source Id1; target Id42,OrderStatusId7. Source latest returns status-definition dates; target latest projects history-event dates. Current Intranet at2589c562815bbdea394a72e416325be220031b4e explicitly consumes the new History DTO in Orders/OrderContracts.cs, LegacyOrderClient.cs and BFF OrderDetailContracts.cs. Restoring ordinal IDs blindly would break its adopted durable model; a reviewed compatibility disposition/versioned bridge is needed.
- U: Source UpdateOrderHistoryAsync ends NoContent204; base shared Result(Updated) returns201. This concrete status mismatch is not explained by the signed-permission approval. The final section records the separate root-approved one-action repair after normal HTTP RED.
- T: Source named/status-ID append returns201 with CreatedAtRoute/history payload; current returns empty201 and enforces transition graph/owner existence/idempotency. Invalid status-ID source400 versus current404 is another response distinction. These safety and payload changes require explicit source-owner disposition; current Quotation client accepts201/other2xx and uses accepted/declined exact retry keys, Intranet/Web consumers use EnsureSuccessStatusCode. Do not roll back current serialized transition safety to reproduce unsafe source behavior.
- Source DeleteOrderAsync only removes Order in its own context; it does not erase the separate OrderStatus history. Current explicit two-DB cleanup is target-added behavior, whose failure is reproduced below.

## Existing durable operation and consumers

OperationKey is already implemented: optional trimmed key on initial Order, unique IX_Order_OperationKey, first readback and unique-violation reconciliation. Migration20260829182133_AddDurableOrderOperationKey exists. It is NOT a deletion receipt and must not be repurposed or reimplemented. Existing repository replay returns the previously persisted order without an added payload/actor fingerprint comparison; API Redis idempotency separately fingerprints the request and scopes operation+principal. Redis hashes raw keys, reserves pending keys for24h, caches completed response24h and fails closed503; absent header is currently allowed. Principal extraction currently prefers user_id then sub then NameIdentifier; not silently changed by this data-fault lane.

Quotation producer accepted/declined POST uses stable Idempotency-Key per decision/order, handles409/503 partial convergence; no duplicate transition after exact replay. Intranet order client uses normal employee bearer, dedicated customer/admin resource shapes and app-generated UUID keys; Web customer submission uses least-privilege service token and durable order-operation key, followed by /new history. Actual current Web revision636ad950dc6a7d37e51a7d6bc4886dadd7ab20a9. Cross-service caller/grant activation is out of scope. Source/consumer inventories do not establish live hosted-chain acceptance.

## Twenty-three pending source-owner records

These are evidence mappings, not ledger resolutions or blanket retired-path approval. Deployment/source infrastructure is replaced by current .NET10/standard Defaults/native logging/secret injection/ADC/OpenAPI under repo guidance; exact rollout/capacity/readiness remains AppHost/Workflows/runtime gate. No source config/credential values were read or copied.

| Full source SHA | Owned change category | Current evidence / open boundary |
|---|---|---|
| 5fac706a7983a6d359b39acbd670e6800afe020e | Initial Order + OrderStatus controllers/models/tests/config | 58-row route/field map above; P/H/U/T and delete fault remain unclosed |
| 3a393215d883fa35e1461f69c876bf2ead7ce36e | Split deployment/service ingress scripts | current API Dockerfile + centralized standard workflow; no local deployment/service manifests in this repo, explicit AppHost/operational disposition needed |
| 0822636e5e2d46e4db20a79d27037aab426d85aa | Resource limits/remove node selectors | deployment topology/capacity owner, not missing domain action |
| 3a104503328cc3c0d57ff9ae2deafba06d1e46d5 | Remove node selectors | same runtime rollout gate |
| 72eb9f1949176392141951d35e6e06f7c30af4c2 | Dependency/startup/scaffold refresh | scalar/computed/model mapping above; no original provider resurrected |
| 5458b7ddc81a15d72087fa69fb4cfcc27ae75747 | Deployment config after old frontend removal | AppHost/Workflows deployment disposition |
| 53f4baf373ef04a3ed5ab5c1ef39bd61404c5258 | Resource limits/requests | runtime capacity evidence remains separate |
| 93f9f99522fbe6c128acb5d049f2b448e07dba95 | Resource optimization | same capacity gate, no deployment in this lane |
| c2b26cd90bf0cd7838c8fe3044c66c2ad3d6a511 | Critical-service replica/memory settings | actual legacy cluster readback not performed |
| 90f34b389c298d1ce85abe2ae7ac92877dbbf7af | Project/dependency deployment split | current .NET10 projects/CI pins, not source package copying |
| 00ec830615c15b5e4e227046712247b11df0100f | Deployment-script hardening | standard protected workflow/runtime deployment gate |
| 2aab25eb07894fc0267b03b85bad96490219d2fa | Externalize design-time DB connections/resources | current externally configured Npgsql contexts; original resource secrets excluded |
| 7d6f46f53cbab853ca9c25e385af067cfff6238a | Externalize runtime DB/scaffold credentials | current named connection config/CI dependency isolation; no source value access |
| eb8ed86672bd9afccc6560b547b734d0fcd7363b | Merge secret-remediation owner paths | path parity overlaps2aab/7d6f and migratedcbac; owner merge remains pending |
| a649db99a27bda65274fe1b18866ae226d3c69cf | Merge secret-remediation paths | same reviewed trust/provider boundaries, not duplicate implementation |
| 03eaff1194c3ae2a54ceefeae31deffaff90436f | Docker build-context excludes | current Docker/ignore files, separate packaging acceptance |
| 72163e9ae11f39f6579423841a2e20529b986fab | Deployment exit-status propagation | protected workflow/job failure behavior, runtime publication separate |
| f8921b1b1d5846eeaff999af10b640011655d1d4 | Throwaway deployment manifest rendering | no deployment scripts run; modern rollout disposition required |
| 143f53ba0a1c81c78d252864ca131d42ed79dc1b | Require runtime secrets in production manifests | current secret-injection gate; no provisioning/activation |
| 9e51e6c5da29de8e617b65b59d46882cde6d3b64 | Remove deprecated LoggerService/native logging | current standard logging; overlap migrated5ac7 source follow-up, ledger stillpending |
| c660de68b633618cb0c857a287020f5ed9c42683 | Logging capacity rollouts | runtime cluster-capacity evidence not claimed |
| a7d0a4517ef1cfef638763cb1092088a5932fa2f | Single-replica rollout availability | deployment readiness/strategy owner gate |
| 03dc9a1271c16e6535934445e9dd6e3f30e8fffe | Isolate generated XML docs | current build policy/artifacts; no generated-source rewrites |

Four separate already-migrated resolutions: cbac7d7155da2208c77d56103b6a2cb19196fc83→baseb7e6ccf/PR43; f0640fe0719b2eb6becda378bff08153d955be07→773214c271ebaf9430389a55b0cba39220b0c38d/PR41;7b311e4e7f0dd80be0441abc2625dab295179f1a→69603e640f17e44667301625ef4c229091af3a62/PR37;5ac7d045c51194edd9e64d8564f1b726b001be34→9617b32b63bf50ba24b187a099a507559011b5ef/PR39. Readback does not change their status or close remaining initial-source scope.

## #45 reproduced partial failure and design gate

Real PostgreSQL18 in two independently owned disposable containers, migrated exact current DbContexts. New tests do not replace the repository with a mock. Cache double is outside the failing persistence boundary. Owned connections disable pooling only in this isolated fixture for cleanup; production registration/retry options are unchanged.

1. Actual Order BEFORE DELETE trigger raises P0001 after status history cleanup. Order transaction rolls file deletion back; order/file remain. Expected original immutable history remains; actual history is empty → genuine RED.
2. Actual history ExecuteDelete completion interceptor cancels caller before Order transaction begins. Caller cancellation propagates, order/file remain. Expected original history remains; actual history is empty → genuine RED.
3. New success/repeat deletion control passes; original DeleteOrder_RemovesFileAndStatusGraphAndIsReplaySafe passes unchanged. Those success controls are not rollback evidence.

Baseline Release0W0E/full106pass0fail0skip: `TestResults/order-baseline/natth_MALIEV-31USFIV_2026-10-01_04_10_11_net10.0.trx`. Test-first Release0W0E/focus2fail2pass0skip: `TestResults/order-delete-intended-red/natth_MALIEV-31USFIV_2026-10-01_04_14_22_net10.0.trx`; both failures are Assert.Single on empty status history after independent order/file survival assertions. Tests remain intentionally RED; not a completed repaired slice.

Fresh post-format Release0W0E, expanded focus39pass2intendedfail0skip (`TestResults/order-delete-final-red/natth_MALIEV-31USFIV_2026-10-01_04_25_07_net10.0.trx`) includes unchanged route/model and existing deletion controls. Full pre-repair109total:107pass2sameintendedfail0skip (`TestResults/order-full-pre-repair-red/natth_MALIEV-31USFIV_2026-10-01_04_26_24_net10.0.trx`), not a passing runtime suite. Scoped owned-test formatting, exact CI Workflows73dd signing-resource scanner, gitleaks owned test/doc and diff whitespace check pass. NuGet audit five solution projects reports no vulnerable packages. Document structural validation confirms58 unique action rows and23 fullSHA rows exactly matching pending Order owner resolutions; all23 commits are present in isolated committed source mirror. Canonical remains clean exactb7e6ccf.

Source-controller method census was also executed against all eight committed checkpoint files:55 actual source methods, all mapped by exact name to58 distinct audit rows. This confirms method coverage, not original runtime JSON equivalence. Packaging inspection finds only current .dockerignore, .gitignore and API Dockerfile, not local legacy deployment/service manifests; the operational owner disposition above is explicitly unresolved.

Commands use private exact dependencies: `dotnet build Legacy.Maliev.OrderService.slnx -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=<owned>/TestResults/.order-dependencies`; tests use same properties, Release `--no-build --no-restore --logger trx` and the result directories above. Full baseline precedes test creation; final full includes the intended new failures. No persistent Postgres/Aspire/GCS or real credential acceptance was attempted.

Minimal choices requiring root design approval:

- Sequence-only quarantine: commit Order/files first, then history cleanup; evict cache after durable Order deletion even if cleanup fails. It fixes the demonstrated loss-before-order-rollback but leaves untracked orphan cleanup after lost response/process death and races with TransitionAsync's pretransaction Order existence check. Not enough for full#45; do not label it durable recovery.
- Coherent durable deletion intent/outbox in Order DB (recommended next gated design): record immutable order-id deletion/cleanup intent atomically with Order/files delete. Fence deletion and status mutations by the same authoritative order key/lock; transition must re-read authoritative Order existence under that fence before Status commit, using a consistent Order→Status lock order. Delete commit unknown reconciles the durable intent, not scoped tracking state. Status cleanup is idempotent and retried from durable intent even when Order no longer exists; mark completion only after independently durable cleanup and cache eviction attempt semantics. This needs a separately approved additive schema/model/worker-or-recovery contract gate; no DDL/source code proposed as already authorized. Existing create OperationKey is not this receipt.
- Retain historical history rather than deleting it: source-like safer behavior but changes the current accepted success contract; requires explicit domain decision and consumer/history-retention policy. Do not silently remove cleanup or alter successful controls.

Before runtime: root selects exact response/retry/retention semantics and schema scope. Required next genuine tests: failure before Order commit, unknown Order commit, failed Status cleanup after Order commit and replay, interrupted cleanup recovered from intent, missing/unrelated order must not destroy arbitrary history, transition started before delete and concurrent transition during delete, caller cancellation at each commit boundary, configured execution strategy fresh contexts, cache postcommit failure/readback. A cross-database atomic transaction is never claimed. All pending source/CI/hosted-chain owner gates remain open.

## Independent PUT status candidate and subsequent recovery design

See `order-history-put-status-acceptance-20261001.md` for genuine normal Production HTTP expected204/actual201 RED, the one-action successful PUT204 repair, unchanged append201/auth/concurrency controls and final passing candidate checks. The original deletion test is preserved byte-identically outside active compilation at `design-artifacts/OrderDeletePartialFailureTests.cs.txt`, not weakened or skipped; the RED result directories above remain. See `order-deletion-intent-design-20261001.md` for exact proposed intent/fence/worker/response/schema-first boundaries. Root approved the next code-only design scope after this narrow candidate's output release, not production migration/activation or a claim that deletion recovery is already implemented.
