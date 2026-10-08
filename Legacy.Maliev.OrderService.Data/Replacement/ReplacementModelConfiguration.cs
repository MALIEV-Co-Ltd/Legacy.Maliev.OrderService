using Legacy.Maliev.OrderService.Domain;
using Legacy.Maliev.OrderService.Domain.Replacement;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.OrderService.Data.Replacement;

public static class ReplacementModelConfiguration
{
    public static void Apply(ModelBuilder builder)
    {
        var cases = builder.Entity<ReplacementCaseRow>();
        cases.ToTable("ReplacementCase", table => table.HasCheckConstraint("CK_ReplacementCase_Identity",
            "\"CustomerId\" > 0 AND \"ReportedBy\" > 0 AND \"Revision\" > 0"));
        cases.HasKey(x => x.Id); cases.Property(x => x.Id).ValueGeneratedOnAdd();
        cases.Property(x => x.Revision).IsConcurrencyToken();
        cases.Property(x => x.OriginalsJson).HasColumnType("jsonb").IsRequired();
        cases.Property(x => x.EvidenceJson).HasColumnType("jsonb").IsRequired();
        cases.Property(x => x.CommandsJson).HasColumnType("jsonb").IsRequired();
        cases.HasIndex(x => x.CustomerId);
        var affected = builder.Entity<ReplacementAffectedRow>();
        affected.ToTable("ReplacementAffectedOrder"); affected.HasKey(x => new { x.CaseId, x.OrderId });
        affected.HasOne<ReplacementCaseRow>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
        affected.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        var operations = builder.Entity<ReplacementOperationRow>();
        operations.ToTable("ReplacementOperation"); operations.HasKey(x => new { x.EmployeeId, x.OperationId });
        operations.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
        operations.HasOne<ReplacementCaseRow>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
    }
}
