# Duplicate-tolerant status-name lookup from retained source

## Exact original facet

Source owner: Legacy.Maliev.OrderService, consolidating original OrderStatus.
Initial `5fac706a7983a6d359b39acbd670e6800afe020e` has no parents.
Original path
`Maliev.OrderStatusService.Api/Controllers/OrderStatusesController.cs`,
blob `65d40f242c27d82c22359748f2beac339361b692`, is byte-identical at retained
final `770cb58032969ae4a0fe169686b53ab61a44fff8`.

The complete GetOrderStatusByNameAsync action uses authenticated
GET /orderstatuses/{statusName}, named GetOrderStatusName. It calls
OrderStatus.FirstOrDefaultAsync(item => item.Name == statusName), returns404 for
no match and otherwise returns the matching definition. It does not require
name uniqueness or specify which duplicate is first.

Final retained scaffold
`Maliev.OrderStatusService.Data/Database/OrderStatusContext/OrderStatusContext.cs`,
blob `3c51a831719c127eff30593f1245390cd92f1bb6`, requires Name with maximum50
but declares no unique name index. This is retained model evidence, not a claim
about the current contents or external constraints of a legacy SQL database.

## Actual current gap and bounded repair

Current protected main `7dae250a68e49fe08513e383e22746ce79d02b5d`, tree
`9a0b840877658a89443adf9e0f28a15c1abdf9df`, preserves accepted Order date/XML
work. Current OrderRepositories.cs blob
`9de831a8999d295f1bf9b3689b4bd9410ef9d361` uses SingleOrDefaultAsync for the
string-name read overload, which requires at most one matching row. Current
OrderStatusDbContext `6e9bcb7cc819eeab9a4c2097b50ed5a10588f262` likewise has
no Name uniqueness declaration.

Only that read overload's terminal operator changes to FirstOrDefaultAsync.
Predicate, projection, cancellation token and method signature remain identical.
No ordering is added. A duplicate read must return one exact matching persisted
definition, not an invented lowest-ID or latest-date winner.

The separate TransitionAsync(orderId, string name, ...) has its own independent
case-insensitive SingleOrDefault query. Both transition overloads remain
byte-identical. The new regression never executes them. ID reads remain unique
by primary key and retain SingleOrDefault. No status write, route, permission,
DTO/casing, schema, migration, provider or financial behavior changes.

Current controller `feb7a351d81b4148aaa86511f530cc5bcfd67acb` delegates the
name route to IOrderService.GetStatusAsync(string,...); its int route delegates
to the separate int overload. Repository implements IOrderService directly.
A bounded current-owner code search found declarations/calls only in interface
`6bc1d9ca4d5dd6ba2b21c1fcec0cea07d2a181a9`, this controller and this
repository. No external consumer contract or actual financial chain is accepted
from that search. Responses retain their current scalar shape and status dates.

## Original action facets retained

- Route/method/name: unchanged.
- JWT plus current granular read permission: unchanged.
- Exact Name predicate: unchanged; no new normalization/collation claim.
- Zero matches:404, preserved and covered.
- One match: definition200, preserved and covered.
- Multiple matches: one matching definition, repaired; source does not promise
  ordering, so the regression asserts membership among exact matching rows.
- Status identity/name/description/definition dates: existing projection unchanged.
- Original entity graph/global JSON compatibility remains a separate obligation.
  This slice does not claim complete controller, initial-SHA or owner retirement.

## Authored regression and hosted execution

Four new OrderStatusNameLookupSourceHttpTests cases use the existing normal
production Program, RS256 fixture token, permission enforcement and PostgreSQL18
fixture. They seed only operational status definitions, with distinct literal
description/date controls:
- Duplicate names:200 with one exact matching definition; both ID reads remain
  exact; raw scalar response names unchanged.
- Unique/missing names:200/404 and no writes.
- Anonymous401 and unrelated-permission403 with no definition disclosure.

Each case verifies the literal definition snapshot remains unchanged, no
history/transition exists and no Order row exists. No financial values, Orders,
provider, transition or financial test are created or executed by this lane.
No replacement authentication handler or mocked repository is introduced.

All four cases are authored, NOT RUN. No original SQL/HTTP host or binary was run.
The duplicate failure is a proven source/operator mismatch, not an executed RED.
Required exact-head hosted checks remain: Release0warnings/errors, new focused
class, existing status-read/lifecycle and admitted affected suite, unchanged
format/static/security/audit/contracts/coverage gates; independent review,
protected merge, and separate exact protected-main acceptance.
No warning, threshold, source or financial gate may be weakened.

Comment/TrackingNumber, frozen FX/provider and all financial/transition execution
holds remain inactive. No native resource, filesystem write, ref, commit, PR,
CI dispatch, original log fetch, canonical write or deployment is created here.
Root remains sole publisher, collector and canonical writer.
