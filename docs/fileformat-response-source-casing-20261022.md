# FileFormat response casing from retained source

## Bounded original producer

Owner: Legacy.Maliev.OrderService. The source controller originates at initial
`5fac706a7983a6d359b39acbd670e6800afe020e` (no parents):
`Maliev.OrderService.Api/Controllers/FileFormatsController.cs`, blob
`5d6da037da66140c0a47b0803a838d94cc44e219`. It is byte-identical at retained
final `770cb58032969ae4a0fe169686b53ab61a44fff8`.
Initial FileFormat model blob `4852b2aa43fafcb1a27bdbe3dbf3fafd65f95b53`
has exactly Id, Name, Extension, CreatedDate and ModifiedDate; no navigation or
financial member exists. Final model blob
`9001b8fbfd4626b5e5469a1558c0d1f4f094ba9d` adds documentation only to those
same fields. That model update belongs to
`72eb9f1949176392141951d35e6e06f7c30af4c2`, parent
`023aa2f143fa5a8f557d72c1302b9add64792d6a`.

Final Order Startup blob `6c390304ea1dc77025e2c5b748eeda4815fbfb68` uses
AddNewtonsoftJson with null/reference-loop omission and no naming override.
Final project blob `67d08ae01965e4d5cde05e7cc895a13f66868ed4` pins net8.0
and MVC.NewtonsoftJson8.0.28. The exact official default provider blob
`2f1cfd6f20c6f9c17219f537e613c07d36af3ac1` uses CamelCaseNamingStrategy:
https://github.com/dotnet/aspnetcore/blob/v8.0.28/src/Mvc/Mvc.NewtonsoftJson/src/JsonSerializerSettingsProvider.cs

| Original FileFormat action facet | Proposed disposition |
| --- | --- |
| POST returns CreatedAtRoute GetFileFormat with entity | Preserve status/Location/values; use original five response names |
| GET by ID returns entity or404 | Preserve values/status; use original response names |
| GET list returns entities or404 for empty list | Preserve list/query/status; use original response names |
| PUT returns204; copies Name/Extension and server ModifiedDate | Unchanged |
| DELETE returns204/404 | Unchanged |
| Nullable fields omitted by original MVC | Preserve runtime omission; align FileFormat-only required schema metadata |
| Source leaf model has no graph members | No fabricated graph or nested-response change |

This is response-casing acceptance only. It does not close whole initial/source72eb,
the full controller's other parity obligations or any other response family.

## Actual target change

Base protected main `2fdba243047ed25cf448d39b2976ae2bcf93b666`, tree
`f9848076ad7e71767b1069b9ba12a609c38dab5e`.
Current FileFormatResponse in OrderModels blob
`ac5c36752db75da5fb618f88c4499fa947f1f779` serializes PascalCase under current
MVC/HTTP naming settings. Assign JsonPropertyName only to its five properties:
id, name, extension, createdDate, modifiedDate. CLR names/types and property order
are preserved. All other DTOs and UpsertFileFormatRequest are unchanged.

Program's existing OpenAPI registration gains a type-specific transformer that
removes only the four nullable FileFormatResponse properties from required.
No global naming/null policy changes. Existing controller/repository projection,
server identity/timestamps, ordering, authentication, permissions and missing
responses remain unchanged.

The existing FileFormat scalar HTTP test changes only its first raw-response
Name/Extension selector to name/extension. The later request-serialization
Name/Extension selector remains PascalCase. The shared original-input test uses
an explicit format-only id selector for created responses/followed Location;
all other kinds retain exact Id. It does not accept both cases or weaken tests.
All existing data rows, payloads, assertions and snapshots are retained.

## Actual nonfinancial caller qualification

Web CustomerOrderCatalogClient blob
`08828180bff129552d6c9cb30afefa8f86457182` reads orders/fileformats via
ReadFromJsonAsync<IReadOnlyList<T>>(cancellationToken) with no custom options.
The actual CustomerOrderFileFormat record in
`6f0461ef8079c8a696e38f490e348a2d52de61e9` has Id, Name and Extension.
Its display callers consume those typed fields. Existing default Web JSON
reader options are case-insensitive; no Web production change is required.
A test freezes this exact inspected three-field reader shape locally and uses
the same HTTP JSON overload. No live Web/service join is claimed.
Scoped Intranet search returned no direct fileformats caller; this is not a
whole-platform absence proof.

The coordinator's separate read-only provenance clarification found two
FileFormatResponse references solely in producer-controller declarations, not
an actual financial consumer. This author did not open the held financial
inventory or execute a financial test, provider or runtime.
No financial production acceptance follows from that clarification. Any actual
strict financial caller discovered later remains held for its owner's review.
No ancestry, SDK role, frozen inventory, contract pin or financial activation
belongs to this proposal.

## Authored regression and required hosted checks

Five new cases:
- Two pure value/null rows compare FileFormat output against stock MVC
  Newtonsoft settings; exact names and null omission; default HTTP reader
  compatibility; unchanged request and Category casing.
- Actual POST, followed Location detail and list raw wire checks; mixed-case
  input and ignored identity/audit fields preserve server authority; real
  FileFormat database readback.
- Actual stored-null detail/list omission and no-mutation check. PostgreSQL
  INSERT date defaults are explicitly cleared to SQL NULL, followed by tracked
  reload and null assertions before reads.
- Served OpenAPI names/nullable optional metadata and unchanged request/Category
  naming.

New HTTP cases use the existing production Program, RS256/permission and
PostgreSQL fixture. Only FileFormat rows are seeded; no Order, transition,
provider or financial records are exercised. Existing authentication/negative/
ignored-body/casing tests remain required under the existing admission rules.

All authored execution is NOT RUN in this source-only lane.
Required exact-head hosted Release0warnings/errors, new focused class plus
existing FileFormat/original-input regressions, admitted affected suite,
format/static/security/audit/contracts/unchanged coverage thresholds, independent
review/protected merge and separate protected-main acceptance remain mandatory.
No source-original host/binary execution is claimed.

Comment/TrackingNumber, held FX/provider/financial execution remain inactive.
No native resources, local file writes, refs, commits, PR, CI dispatch, official
log fetch, deployment, migration or dependency changes were created here.
The coordinator remains sole publisher, collector and canonical writer.
