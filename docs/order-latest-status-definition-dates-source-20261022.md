# Latest status definition-date provenance

## Retained original and ownership

This is a bounded OrderStatus read facet owned by Legacy.Maliev.OrderService.
Original initial SHA `5fac706a7983a6d359b39acbd670e6800afe020e` has no parents.
Path `Maliev.OrderStatusService.Api/Controllers/HistoriesController.cs`, blob
`16208817f8486876e2362462d133a288ac27c3f4`, remains byte-identical at retained
final SHA `770cb58032969ae4a0fe169686b53ab61a44fff8`.
Original model `Maliev.OrderStatusService.Data/Database/OrderStatusContext/OrderStatus.cs`,
initial blob `39dd78a41ad51606349592cf3fe2181693544ab0`, declares both dates
as nullable DateTime. No original configuration or credential content is copied.

The complete original GetLatest action:
- GET orderstatuses/histories/{orderId:int}/latest, named GetLatest.
- Filter history by the requested OrderId.
- Include its status definition; select the highest history Id, not newest date.
- Return 404 if no event exists.
- Return item.OrderStatus, whose CreatedDate/ModifiedDate belong to the definition.

The separate source GetOrderHistory action explicitly copies event dates into
its timeline items. That distinction is preserved.

## Current preimage and smallest repair

Protected main inspected: `2fdba243047ed25cf448d39b2976ae2bcf93b666`, tree
`f9848076ad7e71767b1069b9ba12a609c38dab5e`.
Runtime preimage `Legacy.Maliev.OrderService.Data/OrderHistoryLifetime.cs`,
blob `167aeaab6a0b2f637635a4ec392bc19743e39eaa`, projects the two event dates
into OrderStatusResponse in GetLatestStatusAsync.
Only those two projection arguments change to x.OrderStatus.CreatedDate and
x.OrderStatus.ModifiedDate. The literal preimage must occur exactly once;
inverse replacement must restore the exact entire preimage.

Route, status identity/name/description, highest-history-ID selection, external
OrderId filtering, missing result, signed JWT/permission and lifetime safeguards,
DTO fields, casing and null omission remain unchanged. GetHistoryAsync and all
status write/transition methods remain byte-identical. No Order row, financial
field, transition, provider, schema, migration or dependency change is proposed.
Comment/TrackingNumber and frozen FX/provider/financial holds remain inactive.

This is not whole initial-SHA/controller acceptance. Original response graph and
global naming compatibility, timeline ordinal versus durable identity, transition
payloads and approved target safety differences remain separately unresolved.
No missing graph is replaced with a fabricated empty graph in this slice.

## Inspected current consumer boundary

Current Intranet source search revision:
`96439bf8a7c95e63289e8d3e4c0592706dd40d4e`.
- Orders/LegacyOrderClient.cs, blob `76d562c5cb9ac34d00929dde5eaedc7aa078e0d8`:
  reads the same latest route into OrderStatusResponse; timeline is separate.
- Orders/OrderContracts.cs, blob `4a08e3b609465e5e1c7fdb70638cf35fb9f2d01c`:
  both nullable date fields retain the same types.
- Pages/Orders/View.cshtml.cs, blob `6065ba31bd6f68a7d4d61974950a54e726cb58fe`:
  uses latest.Id for available transitions, separately fetches event history.
- Bff/Orders/OrderDetailProxy.cs, blob `8e5f46679729dc03d0c97fdcd94d71246e46edb2`:
  forwards the same route.
- Bff/Orders/OrderDetailAggregator.cs, blob `aed19d928aa0981c0451a5ce6c8891c898fcacf1`:
  deserializes latest status, uses Id for availability, carries history separately.

The two Bff paths above abbreviate the actual Legacy.Maliev.Intranet.Bff project
prefix; the other paths use Legacy.Maliev.Intranet.
Bounded Web code search returned no latest-history caller. That search is not a
whole-platform absence proof. Financial inventory hits were not opened; no
financial consumer/runtime acceptance is inferred.

## Authored regression and execution boundary

New OrderLatestStatusDateSourceHttpTests uses the existing production
WebApplicationFactory, normal RS256 fixture token, permission enforcement and
real PostgreSQL18 DbContexts. It seeds only status definitions and history with
external scalar OrderIds; no Order or transition/provider data is created.
The fixture's existing disposable PostgreSQL/Redis and disposal remain unchanged;
no new standalone process/container fixture is introduced.

Two cases deliberately make highest history Id disagree with event chronology,
keep an unrelated higher-ID event, distinguish definition dates from event dates,
exercise null definition-date omission and preserve event timeline timestamps.
They assert definition/history snapshots remain unchanged and no Order exists.
The nullable-date case first inserts the definition, then explicitly sets its
persisted CreatedDate/ModifiedDate columns to SQL NULL through ExecuteUpdateAsync.
It reloads the tracked definition and asserts both dates are null before taking
snapshots or issuing HTTP requests. This avoids PostgreSQL INSERT defaults
populating the nullable generated-on-add columns. Production defaults are unchanged.

Three further cases preserve401/403/404 and empty stores.

All five are authored declarations, not executed results. No original HTTP host,
native local build, Docker, financial/provider test or heavy validation ran in
this source-only lane. Required hosted exact-head checks: Release0warnings/errors,
focused new class plus existing lifetime/timeline regression, admitted affected
suite, unchanged formatting/static/security/audit/contract/coverage gates, followed
by protected review/merge and separate exact protected-main acceptance.
No check or coverage threshold may be weakened. The coordinator remains sole
publisher and official exact-head/main log collector.
