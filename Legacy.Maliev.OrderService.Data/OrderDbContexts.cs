using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Legacy.Maliev.OrderService.Data;

public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>(); public DbSet<Process> Processes => Set<Process>(); public DbSet<Category> Categories => Set<Category>(); public DbSet<FileFormat> FileFormats => Set<FileFormat>(); public DbSet<OrderFile> Files => Set<OrderFile>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        var c = b.Entity<Category>(); c.ToTable("Category"); c.HasKey(x => x.Id); c.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd(); c.Property(x => x.Name).HasMaxLength(50); Dates(c);
        var f = b.Entity<FileFormat>(); f.ToTable("FileFormat"); f.HasKey(x => x.Id); f.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd(); f.Property(x => x.Name).HasMaxLength(50); f.Property(x => x.Extension).HasMaxLength(50); Dates(f);
        var p = b.Entity<Process>(); p.ToTable("Process"); p.HasKey(x => x.Id); p.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd(); p.Property(x => x.CategoryId).HasColumnName("CategoryID"); p.Property(x => x.Name).HasMaxLength(50).IsRequired(); Dates(p); p.HasOne(x => x.Category).WithMany(x => x.Processes).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_Process_Category");
        var o = b.Entity<Order>(); o.ToTable("Order"); o.HasKey(x => x.Id); o.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd(); o.Property(x => x.CustomerId).HasColumnName("CustomerID"); o.Property(x => x.EmployeeId).HasColumnName("EmployeeID"); o.Property(x => x.ProcessId).HasColumnName("ProcessID"); o.Property(x => x.MaterialId).HasColumnName("MaterialID"); o.Property(x => x.SurfaceFinishId).HasColumnName("SurfaceFinishID"); o.Property(x => x.ColorId).HasColumnName("ColorID"); o.Property(x => x.CurrencyId).HasColumnName("CurrencyID"); o.Property(x => x.Name).HasMaxLength(100).HasDefaultValue("unnamed"); o.Property(x => x.Description).HasMaxLength(250); o.Property(x => x.DiscountPercent).HasPrecision(5, 2); o.Property(x => x.UnitPrice).HasPrecision(18, 2); o.Property(x => x.TrackingNumber).HasMaxLength(250); o.Property(x => x.PromisedDate).HasColumnType("date"); o.Property(x => x.FinishedDate).HasColumnType("date"); o.Property(x => x.Remaining).HasComputedColumnSql("\"Quantity\" - \"Manufactured\"", true); o.Property(x => x.Subtotal).HasPrecision(18, 2).HasComputedColumnSql("(\"UnitPrice\" * \"Quantity\" - ((\"UnitPrice\" * \"Quantity\") * \"DiscountPercent\") / 100)::numeric(18,2)", true); o.Property(x => x.Turnaround).HasComputedColumnSql("(\"FinishedDate\" - (\"CreatedDate\" AT TIME ZONE 'UTC')::date)", true); Dates(o); o.Property(x => x.ModifiedDate).IsConcurrencyToken(); o.HasOne(x => x.Process).WithMany(x => x.Orders).HasForeignKey(x => x.ProcessId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_Order_Process");
        var of = b.Entity<OrderFile>(); of.ToTable("OrderFile"); of.HasKey(x => x.Id); of.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd(); of.Property(x => x.OrderId).HasColumnName("OrderID"); of.Property(x => x.Bucket).HasMaxLength(50).IsRequired(); of.Property(x => x.ObjectName).IsRequired(); Dates(of); of.HasOne(x => x.Order).WithMany(x => x.Files).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_OrderFile_Order");
    }
    private static void Dates<T>(EntityTypeBuilder<T> e) where T : class { e.Property<DateTime?>("CreatedDate").HasColumnType("timestamp with time zone").HasDefaultValueSql("CURRENT_TIMESTAMP"); e.Property<DateTime?>("ModifiedDate").HasColumnType("timestamp with time zone").HasDefaultValueSql("CURRENT_TIMESTAMP"); }
}

public sealed class OrderStatusDbContext(DbContextOptions<OrderStatusDbContext> options) : DbContext(options)
{
    public DbSet<OrderStatus> Statuses => Set<OrderStatus>(); public DbSet<OrderStatusTransition> Transitions => Set<OrderStatusTransition>(); public DbSet<OrderStatusHistory> History => Set<OrderStatusHistory>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        var s = b.Entity<OrderStatus>(); s.ToTable("OrderStatus"); s.HasKey(x => x.Id); s.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd(); s.Property(x => x.Name).HasMaxLength(50); Dates(s);
        var t = b.Entity<OrderStatusTransition>(); t.ToTable("OrderStatusHasPossibleStatus"); t.HasKey(x => x.Id); t.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd(); t.Property(x => x.OrderStatusId).HasColumnName("OrderStatusID"); t.Property(x => x.PossibleStatusId).HasColumnName("PossibleStatusID"); Dates(t); t.HasOne(x => x.OrderStatus).WithMany(x => x.FromTransitions).HasForeignKey(x => x.OrderStatusId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_OrderStatusHasPossibleStatus_OrderStatus"); t.HasOne(x => x.PossibleStatus).WithMany(x => x.ToTransitions).HasForeignKey(x => x.PossibleStatusId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_OrderStatusHasPossibleStatus_OrderStatus1");
        var h = b.Entity<OrderStatusHistory>(); h.ToTable("OrderStatusHistory"); h.HasKey(x => x.Id); h.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd(); h.Property(x => x.OrderId).HasColumnName("OrderID"); h.Property(x => x.OrderStatusId).HasColumnName("OrderStatusID"); Dates(h); h.Property(x => x.ModifiedDate).IsConcurrencyToken(); h.HasOne(x => x.OrderStatus).WithMany(x => x.History).HasForeignKey(x => x.OrderStatusId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_OrderHasOrderStatus_OrderStatus");
    }
    private static void Dates<T>(EntityTypeBuilder<T> e) where T : class { e.Property<DateTime?>("CreatedDate").HasColumnType("timestamp with time zone").HasDefaultValueSql("CURRENT_TIMESTAMP"); e.Property<DateTime?>("ModifiedDate").HasColumnType("timestamp with time zone").HasDefaultValueSql("CURRENT_TIMESTAMP"); }
}
