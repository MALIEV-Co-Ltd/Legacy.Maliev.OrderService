# SDD ledger — plan: docs/superpowers/plans/2026-10-08-replacement-rework.md

Base cb57f8f. Native managed-worktree tool unavailable for durable task; isolated writable local clone used. Approval relayed by parent permits plan then implementation without another permission loop. Task1-5 interfaces inspected; finance/document producers unavailable, so critical consumer acceptance remains pending; new domain work can proceed.

## Implemented foundation

- Immutable original snapshots; reason/evidence; approval/rejection; required-return gates and audited return/quantity waivers; partial production/QA; independent finished dates; partial shipments; failed delivery and explicit retry authorization; closure rules.
- Actor-scoped operation GUID/payload hash receipts, exact historical replay, optimistic case revision and transactional advisory fences. Intake uses canonical sorted original-order lifetime fences, rejects deletion tombstones and mixed-customer originals, and preserves all original fields.
- Additive canonical Order context/migration for three replacement tables. Restrictive original references; database immutable snapshot/lineage/receipt and append-only command guards. Legacy deletion returns Conflict under the lifetime fence before tombstone or cleanup; pre-replacement schemas remain supported.
- Unregistered strict FileService receipt reader, bounded response, exact stable version/customer/kind/original-order references, verified actor/time/digest/revision, dependency-failure normalization. It does not confer authority or register the producer.

## Review and verification evidence

Fresh reviewer identified lifetime intake race, required-return closure, explicit quantity waiver, failed-shipment waiver consistency and interrupted receipt streams. All were reproduced with regressions and corrected. Separate database RED/GREEN proves snapshot/history rewrites are rejected. Final reviewer reports no additional material findings.

- 151 affected replacement/deletion/model/route tests passed before the final database immutability addition (`replacement-reviewed-green.trx`).
- 3 canonical PostgreSQL18 migration/deletion/immutability tests passed (`immutable-green.trx`).
- Full-suite final run: 421 passed, zero failures/skips (`replacement-full-final.trx`, 3m59s). Formatting corrections affected whitespace only; final formatted Release build is verified separately.
- New replacement source whitespace formatting verification passed using relative include paths. Initial absolute include paths did not cover the files; the relative-path check exposed and then corrected those errors.
- Final formatted Release solution build passed with zero warnings/errors; final Gitleaks directory scan found no leaks.
- Final coverage run: 421 passed, zero failures/skips (`replacement-coverage.trx`). Existing production gate passed with no exclusions: API 88.81%, Application 100%, Data 96.25%, Domain 93.13%. Raw report SHA256 `d9b0736c8ffc00af3c68ecc1a882d663695327d4f7fe63bc9d1a4cb574cf1292`.
- Vulnerability audit with the correct shared dependency root: no vulnerable packages reported. Gitleaks directory scan: no leaks. Git diff whitespace check passed.
- Earlier full run was aborted after 396 passes while a concurrent build conflicted with its testhost; this is not full-suite acceptance. Subsequent builds/tests are sequential.

## Outstanding implementation and acceptance

Task1 domain and Task2 persistence are implemented. Task3 seven protected case routes, service, live-permission metadata, durable reconciliation and default-off registration are implemented locally. Task4 employee-token BFF, English/Thai case UI, private metadata picker and uncertain-operation guards are implemented locally. Task5 immutable operational cost/claim observations and correction UI are implemented locally. Accepted current-session/active-staff/trusted-tenant authority and File-owner joins remain unavailable; controlled tests are not producer acceptance. No whole-feature completion, activation or deployment claim.

Document dependency: Intranet#277 has no accepted authority/File-owner fixture packet. No lineage route source pin or live 401/403/404/503 contract can be advertised before its implementation and acceptance. CaseId is generated positive int; AttemptId/ShipmentId are case-local positive ints; shared document/version IDs remain Guids. Original Order reads remain genuine authority; browser-provided lineage is never accepted.

Accounting#79 has no accepted billing/tax/credit HTTP producer. Physical completion never creates a second receivable, tax event, payment, credit or commercial amendment. Current Order rows contain no invoice/quotation pointers; initial snapshot financial references remain null rather than fabricated. Future commercial reads must preserve authoritative existing IDs, and acknowledged BillingOperationId is optional Guid. Tax treatment requires a separate accountant decision; no blanket tax-free replacement rule.

No live order94826 amendment, order94876 association, customer/carrier message, claim submission, real invoice or financial posting, production migration, deployment, or paid infrastructure change occurred.

## Resumed local implementation checkpoint

The parent explicitly resumed approved feature work after the historical deferred checkpoint. Original migration retains shared-path/integration/test-capacity priority. All feature changes remain isolated; shared owners' pending patches are not applied.

- Local API/service reviews corrected Redis marker deserialization and current ownership races on durable replay. Real cache-backed HTTP and PostgreSQL evidence-window regressions retained RED/GREEN.
- BFF review corrected unbounded response prebuffering and wrong-case responses that happened to share the same original order. Controlled transport regressions retained RED/GREEN.
- UI review corrected pending-operation loss on child remount, cross-employee visibility/retry, cancelled old-order completions, and original-editor writes during unknown outcomes. Page-owned immutable operation identity/payload, original write guards, cancellation leases and navigation guards cover the uncertain operation. Exact stale-revision rejection is distinguished from unknown idempotency conflict by an allowlisted owner header.
- Latest controlled UI/BFF/affected editor checks: 42 passed. Actual Chromium English/Thai 375/1440 timeline and lost-ack checks: 6 passed with CI-pinned browser dependencies. Visual inspection found isolated CSS did not carry into the child; feature-scoped styles and localized picker prompts added, browser verification pending after those changes.
- Actual PostgreSQL18 + MVC HTTP lost-ack test passed: durable commit followed by cache acknowledgment failure returns 503; same operation is in-progress 409; actor-scoped GET returns revision1 after later approval revision2; another employee receives404; one case and unchanged original manufactured/tracking/finished/modified fields. Controlled authority/evidence are explicit test boundaries, not accepted joins.

Ruling: use exact dependency pins from each repository's CI in isolated task-owned dependency clones rather than the advancing migration checkout — reproducibility without changing shared dependencies — cost if wrong: rerun against a newly accepted migration baseline.
Ruling: final receipt verification remains mandatory, but unavailable Auth/File owners stay unavailable/default-off rather than using display claims or browser lineage — preserves approved authority boundary — cost: feature cannot activate until accepted joined producers are delivered.
Ruling: UI exposes one current original per report, while the case/API support multiple authoritative originals — avoids inventing a cross-order picker in this scope — cost: staff make separate reports for separate originals until an approved selection UI exists.

Final complete-suite/coverage/format/security results will be appended after sequential validation. No source pin or live lineage producer acceptance is advertised before accepted current authority and File joins.

## Remaining whole-branch review

Fresh reviewer found no new material defects in cost/claim immutability, financial side-effect separation, permission routing or default-off composition. Previous review findings were reproduced and fixed with retained regression evidence. Reviewer did not assess unavailable live Auth/File joins or production activation.

Final: minor (deferred): evidence version selection reads first20 versions; older verified versions require future pagination.
Final: minor (deferred): null entries in producer metadata arrays can raise500 rather than normalized503; writes remain blocked and no evidence is authorized from metadata.
Final: minor (deferred): currency validation enforces three-letter shape; authoritative supported currencies require the separate Accounting owner decision.

Native validation yielded to original migration after parent request. Read-only process inspection found compilers2032/27488 with absent parent18424/47524 and no task path in argv. Exclusive ownership is unproven; neither was killed. Future builds use disabled shared compilation/node reuse. Final full suite initially did not execute because the requested runsettings file does not exist in OrderService; this is an invalid verification invocation, not a test result. Correct collection command remains pending capacity release.

## Resumed full run and preserved RED

Exact CI-pinned full run finished425pass31fail0skip/456 in6m14s, retained in `.validation/resumed-final-coverage/full.trx`. All29 source-contract failures traced to AppContext ancestor discovery because task-owned artifacts sat outside the checkout. Two legacy filter census failures counted65 rather than unchanged58, because seven additive replacement actions were included in the legacy census. Corrected only the census scope and added an explicit seven-action live/critical permission/no-legacy-filter contract; legacy filter totals22/31 remain unchanged. No workflow/source-discovery assertions are weakened; next run places output under checkout `.validation/build-ci` so existing discovery works naturally.

Original migration/native priority honored: known-owned SDK/test lineage terminal and no task worker/testcontainer found on fresh read-only inventory; slot released to queued Accounting full run before replacement rerun. Newly formatted service tests passed in425 successes. Matching corrected full GREEN/coverage gate remains pending Billing release.

Intranet original migration culture fix0e59e807 incorporated without conflicts. Exact feature source preserved through recoverable stash27b1339ad692c1c1934621828656cf1930a7b86a, applied with its staged state. No dependency pin changes or unrelated lockfile content changes.

## Corrected complete-suite evidence

Matching corrected run finished457passed0failed0skipped,6m44s (`.validation/resumed-green-coverage/full.trx`). Artifacts are inside the checkout; all unchanged source-root contracts and legacy58-operation assertions pass, with a distinct seven-route replacement permission contract. Release test build emitted no warnings/errors. Existing production80% coverage gate passes with no exclusions: API87.5%, Application95.97%, Data96.08%, Domain92.91%; rawSHA256 `997b39c5fdee24e99aaadcaae1bf233ee94fc2bd0a70f4a56bc382797596be29`. The prior425/31 RED is retained separately. Current tracked/staged-source Gitleaks and whitespace checks pass.

Fresh terminal inventory contained no SDK/testhost/MSBuild/VBCS processes or disposable containers. Native slot explicitly released to queued Accounting matching validation; later formatting/dependency audit and Intranet post-style focused/browser validation wait their release and original migration priority. No unavailable current authority/File contract is inferred from this full-suite result.

Current Order formatting verification passed for the later service and census/OpenAPI changes; prior new-source checks remain retained. Dependency vulnerability audit across the solution and transitive packages reports no vulnerable packages. Full457, coverage gate, Release compilation, formatting, source secret scan and whitespace checks now support the coherent API/service/operational-recovery draft update. Accepted producer joins and runtime activation remain blocked independently.
