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

Task1 domain foundation is implemented. Task2 persistence/migration is implemented; protected lineage HTTP producer remains absent. Task3 wire reader is prepared, but authenticated current-session/staff/tenant owner adapters, permissions, case API and accepted exact-source integration are not implemented/accepted. Task4 Intranet UI/BFF and Task5 operational cost/claim facts are not implemented. No feature completion claim, activation or deployment.

Document dependency: Intranet#277 has no accepted authority/File-owner fixture packet. No lineage route source pin or live 401/403/404/503 contract can be advertised before its implementation and acceptance. CaseId is generated positive int; AttemptId/ShipmentId are case-local positive ints; shared document/version IDs remain Guids. Original Order reads remain genuine authority; browser-provided lineage is never accepted.

Accounting#79 has no accepted billing/tax/credit HTTP producer. Physical completion never creates a second receivable, tax event, payment, credit or commercial amendment. Current Order rows contain no invoice/quotation pointers; initial snapshot financial references remain null rather than fabricated. Future commercial reads must preserve authoritative existing IDs, and acknowledged BillingOperationId is optional Guid. Tax treatment requires a separate accountant decision; no blanket tax-free replacement rule.

No live order94826 amendment, order94876 association, customer/carrier message, claim submission, real invoice or financial posting, production migration, deployment, or paid infrastructure change occurred.
