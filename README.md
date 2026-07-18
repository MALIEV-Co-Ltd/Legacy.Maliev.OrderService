# Legacy.Maliev.OrderService

Public, sanitized .NET 10 extraction merging the private legacy Order and OrderStatus APIs. It
preserves 55 actions and 56 route templates across `/Orders`, order files/catalogs/processes,
`/OrderStatuses`, available transitions, and history (including all 14 named status shortcuts).

The service uses Scalar/OpenAPI, JWT permissions, Redis read caching and SHA-256-keyed create
idempotency. Orders and history rows support `X-Expected-Modified-Date`; status transitions run
serially and must exist in `OrderStatusHasPossibleStatus`, preventing impossible or concurrent
state changes while preserving the legacy routes.

Local, CI, and image builds resolve shared infrastructure only from the public
`Legacy.Maliev.ServiceDefaults` and `Legacy.Maliev.CompatibilityContracts` repositories. The
compatibility namespaces, order DTOs, JSON, routes, and messaging schemas remain unchanged.

## Data boundaries

- Planned `legacy-postgres-order` in namespace `maliev-legacy`, using the existing CloudNativePG
  operator and existing GKE resources only.
- Database `Order`: `Order`, `OrderFile`, `Process`, `Category`, `FileFormat`.
- Database `OrderStatus`: `OrderStatus`, `OrderStatusHasPossibleStatus`, `OrderStatusHistory`.
- PostgreSQL retains computed `Remaining`, discounted `Subtotal`, and `Turnaround`, legacy names,
  lengths, precision, FKs, pending/customer filtering, and ordered/latest history behavior.
- Customer, Employee, Material, Color, SurfaceFinish, Currency and financial IDs remain external
  scalar references. No cross-service database access is introduced.
- Order files store GCS bucket/object metadata only for ADC/Workload Identity.

Source SQL Server remains untouched. Extraction does not deploy. Cutover requires repeatable
copy/parity/rollback evidence for both databases, GCS reconciliation, Web/Intranet ownership and
workflow tests, dedicated WIF, and GitOps manifests. No new node pool, Cloud SQL, or paid service.

## Validate

```powershell
dotnet build Legacy.Maliev.OrderService.slnx -c Release
dotnet test Legacy.Maliev.OrderService.slnx -c Release --no-build
dotnet format Legacy.Maliev.OrderService.slnx --verify-no-changes --no-restore
dotnet list Legacy.Maliev.OrderService.slnx package --vulnerable --include-transitive
gitleaks git . --redact=100 --exit-code 1 --no-banner --no-color
```
