using System;
using System.Collections.Generic;
using AutoGeniusSync.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Data;

public partial class AppDbContext : DbContext
{
    public AppDbContext()
    {
    }

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<DmsAuthToken> DmsAuthTokens { get; set; }

    public virtual DbSet<DmsDealer> DmsDealers { get; set; }

    public virtual DbSet<DmsPincodeMaster> DmsPincodeMasters { get; set; }

    public virtual DbSet<DmsServiceHistory> DmsServiceHistories { get; set; }

    public virtual DbSet<DmsSyncLog> DmsSyncLogs { get; set; }

    public virtual DbSet<DmsVehicleSale> DmsVehicleSales { get; set; }

    public virtual DbSet<OpsPodVehicleLiveDatum> OpsPodVehicleLiveData { get; set; }

    public virtual DbSet<SixSenseVehicleTelemetry> SixSenseVehicleTelemetries { get; set; }
    public DbSet<DmsVehicleDispatch>  DmsVehicleDispatches  { get; set; }
    public DbSet<DmsCallCentreDealer> DmsCallCentreDealers  { get; set; }
    public DbSet<DmsLineOrderReport> DmsLineOrderReports { get; set; }
    public DbSet<DmsShadowfaxChassisMaster> DmsShadowfaxChassisMasters { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DmsAuthToken>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__DMS_Auth__3214EC0789A5492C");

            entity.ToTable("DMS_AuthTokens");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.LoginEmail).HasMaxLength(200);
            entity.Property(e => e.VendorCode).HasMaxLength(50);
            entity.Property(e => e.VendorId).HasMaxLength(50);
            entity.Property(e => e.VendorName).HasMaxLength(500);
        });

        modelBuilder.Entity<DmsDealer>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__DMS_Deal__3214EC07B39CA0D7");

            entity.ToTable("DMS_Dealers");

            entity.HasIndex(e => e.DealerCode, "UQ_DMS_Dealers_DealerCode").IsUnique();

            entity.Property(e => e.ActiveStatus).HasMaxLength(50);
            entity.Property(e => e.AlternateContactNo).HasMaxLength(50);
            entity.Property(e => e.ContactNo).HasMaxLength(50);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.DealerCityName).HasMaxLength(200);
            entity.Property(e => e.DealerCode).HasMaxLength(50);
            entity.Property(e => e.DealerCompany).HasMaxLength(500);
            entity.Property(e => e.DealerStateName).HasMaxLength(200);
            entity.Property(e => e.LastFetchedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.PinCode).HasMaxLength(20);
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.Email).HasMaxLength(300);
            entity.Property(e => e.CityTier).HasMaxLength(20);
            entity.Property(e => e.Source).HasMaxLength(50);
            entity.Property(e => e.PreviousStatus).HasMaxLength(50);
            entity.Property(e => e.ModifiedBy).HasMaxLength(200);
            entity.Property(e => e.Remarks).HasMaxLength(1000);
        });

        modelBuilder.Entity<DmsPincodeMaster>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__DMS_Pinc__3214EC07CAAA8551");

            entity.ToTable("DMS_PincodeMaster");

            entity.HasIndex(e => e.PinCode, "UQ__DMS_Pinc__70964C4FD48561BD").IsUnique();

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.PinCode).HasMaxLength(20);
        });

        modelBuilder.Entity<DmsServiceHistory>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__DMS_Serv__3214EC07DF008F46");

            entity.ToTable("DMS_ServiceHistory");

            entity.HasIndex(e => e.ChassisNo, "IX_DMS_Service_ChassisNo");

            // ── FIXED: these were incorrectly marked .IsUnique() ──────────
            // That meant "only one row per dealer, ever" and "only one row
            // per JobDate across ALL dealers, ever" — silently dropping the
            // vast majority of real jobs on insert. They are now plain,
            // non-unique indexes kept only for query performance.
            entity.HasIndex(e => e.DealerCode, "IX_DMS_Service_DealerCode");
            entity.HasIndex(e => e.JobDate, "IX_DMS_Service_JobDate");

            // The ONLY correct unique constraint: one row per (DealerCode, JobNo).
            entity.HasIndex(e => new { e.DealerCode, e.JobNo }, "UQ_DMS_Service_Job").IsUnique();

            entity.Property(e => e.Accessory)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.BatterySerialNo1).HasMaxLength(200);
            entity.Property(e => e.BatterySerialNo2).HasMaxLength(200);
            entity.Property(e => e.BatterySerialNo3).HasMaxLength(200);
            entity.Property(e => e.BatterySerialNo4).HasMaxLength(200);
            entity.Property(e => e.BatterySerialNo5).HasMaxLength(200);
            entity.Property(e => e.BatterySerialNo6).HasMaxLength(200);
            entity.Property(e => e.BrandName).HasMaxLength(200);
            entity.Property(e => e.ChassisNo).HasMaxLength(100);
            entity.Property(e => e.CloseTime).HasMaxLength(20);
            entity.Property(e => e.CompName).HasMaxLength(500);
            entity.Property(e => e.CouponNo).HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.DealerCode).HasMaxLength(50);
            entity.Property(e => e.DocNo).HasMaxLength(50);
            entity.Property(e => e.DocType).HasMaxLength(100);
            entity.Property(e => e.EngineNo).HasMaxLength(100);
            entity.Property(e => e.EstimatedJobExpenses)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Ffrpercentage)
                .HasMaxLength(20)
                .HasColumnName("FFRPercentage");
            entity.Property(e => e.Gstamount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("GSTAmount");
            entity.Property(e => e.Igstamount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("IGSTAmount");
            entity.Property(e => e.InTime).HasMaxLength(20);
            entity.Property(e => e.IndividualAhbattery1)
                .HasMaxLength(50)
                .HasColumnName("IndividualAHBattery1");
            entity.Property(e => e.IndividualAhbattery2)
                .HasMaxLength(50)
                .HasColumnName("IndividualAHBattery2");
            entity.Property(e => e.IndividualAhbattery3)
                .HasMaxLength(50)
                .HasColumnName("IndividualAHBattery3");
            entity.Property(e => e.IndividualAhbattery4)
                .HasMaxLength(50)
                .HasColumnName("IndividualAHBattery4");
            entity.Property(e => e.IndividualAhbattery5)
                .HasMaxLength(50)
                .HasColumnName("IndividualAHBattery5");
            entity.Property(e => e.IndividualAhbattery6)
                .HasMaxLength(50)
                .HasColumnName("IndividualAHBattery6");
            entity.Property(e => e.IsRowTotal).HasDefaultValue(false);
            entity.Property(e => e.JobCategory).HasMaxLength(200);
            entity.Property(e => e.JobNo).HasMaxLength(50);
            entity.Property(e => e.JobType).HasMaxLength(200);
            entity.Property(e => e.Kms)
                .HasMaxLength(50)
                .HasColumnName("KMS");
            entity.Property(e => e.Labour)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.LabourHours)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Location).HasMaxLength(500);
            entity.Property(e => e.MobileNumber).HasMaxLength(50);
            entity.Property(e => e.Model).HasMaxLength(500);
            entity.Property(e => e.NetTotal)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Oil)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.OutsideWork)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Parts)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.PartyName).HasMaxLength(500);
            entity.Property(e => e.RegNo).HasMaxLength(100);
            entity.Property(e => e.ServiceHead).HasMaxLength(200);
            entity.Property(e => e.Supervisor).HasMaxLength(200);
            entity.Property(e => e.Technician).HasMaxLength(200);
            entity.Property(e => e.TotalWotax)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("TotalWOTax");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.VehicleType).HasMaxLength(100);

            // ── NEW: JobStatus — DB-computed column, EF never writes to it ──
            entity.Property(e => e.JobStatus)
                .HasComputedColumnSql("(CASE WHEN [InvoiceDate] IS NOT NULL THEN 'Closed' ELSE 'Open' END)", stored: true)
                .HasMaxLength(10)
                .ValueGeneratedOnAddOrUpdate();
        });

        modelBuilder.Entity<DmsSyncLog>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__DMS_Sync__3214EC07D94E2D38");

            entity.ToTable("DMS_SyncLog");

            entity.HasIndex(e => new { e.SyncDate, e.SyncType }, "IX_DMS_SyncLog_SyncDate");

            entity.Property(e => e.DealerCode).HasMaxLength(50);
            entity.Property(e => e.RecordsFetched).HasDefaultValue(0);
            entity.Property(e => e.RecordsInserted).HasDefaultValue(0);
            entity.Property(e => e.RecordsUpdated).HasDefaultValue(0);
            entity.Property(e => e.StartedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.Status).HasMaxLength(50);
            entity.Property(e => e.SyncType).HasMaxLength(100);
        });

        modelBuilder.Entity<DmsVehicleSale>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__DMS_Vehi__3214EC0751DC185C");

            entity.ToTable("DMS_VehicleSales");

            entity.HasIndex(e => e.ChassisNo, "IX_DMS_VehicleSales_ChassisNo");

            entity.HasIndex(e => e.DealerCode, "IX_DMS_VehicleSales_DealerCode");

            entity.HasIndex(e => e.InvoiceDate, "IX_DMS_VehicleSales_InvoiceDate");

            entity.HasIndex(e => new { e.DealerCode, e.InvoiceNo }, "UQ_DMS_VehicleSales_Invoice").IsUnique();

            entity.Property(e => e.AccountType).HasMaxLength(100);
            entity.Property(e => e.AcsryAmount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Address1).HasMaxLength(500);
            entity.Property(e => e.Address2).HasMaxLength(500);
            entity.Property(e => e.Battery).HasMaxLength(200);
            entity.Property(e => e.BatteryCapacity).HasMaxLength(100);
            entity.Property(e => e.BatteryChemical).HasMaxLength(200);
            entity.Property(e => e.BatteryMake).HasMaxLength(200);
            entity.Property(e => e.Cgstamount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("CGSTAmount");
            entity.Property(e => e.Cgstper)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(8, 2)")
                .HasColumnName("CGSTPer");
            entity.Property(e => e.ChargerNo).HasMaxLength(200);
            entity.Property(e => e.ChargerNo2).HasMaxLength(200);
            entity.Property(e => e.ChassisNo).HasMaxLength(100);
            entity.Property(e => e.City).HasMaxLength(200);
            entity.Property(e => e.ColorCode).HasMaxLength(50);
            entity.Property(e => e.ControllerNo).HasMaxLength(200);
            entity.Property(e => e.Converter).HasMaxLength(200);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.CusMob).HasMaxLength(50);
            entity.Property(e => e.CustDob).HasColumnName("CustDOB");
            entity.Property(e => e.DealerCode).HasMaxLength(50);
            entity.Property(e => e.DealerName).HasMaxLength(500);
            entity.Property(e => e.DiscTypeName).HasMaxLength(100);
            entity.Property(e => e.ExecutiveName).HasMaxLength(200);
            entity.Property(e => e.FameIi)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("FameII");
            entity.Property(e => e.FameIirequired)
                .HasMaxLength(10)
                .HasColumnName("FameIIRequired");
            entity.Property(e => e.FinAmount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.FinancedBy).HasMaxLength(300);
            entity.Property(e => e.Gender).HasMaxLength(20);
            entity.Property(e => e.Hsnsaccode)
                .HasMaxLength(50)
                .HasColumnName("HSNSACCode");
            entity.Property(e => e.Igstamount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("IGSTAmount");
            entity.Property(e => e.Igstper)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(8, 2)")
                .HasColumnName("IGSTPer");
            entity.Property(e => e.InstitutionalName).HasMaxLength(200);
            entity.Property(e => e.InsuAmount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.InvoiceNo).HasMaxLength(100);
            entity.Property(e => e.ItemModel).HasMaxLength(300);
            entity.Property(e => e.ItemRate)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.LocCode).HasMaxLength(100);
            entity.Property(e => e.Location).HasMaxLength(500);
            entity.Property(e => e.LocationCity).HasMaxLength(200);
            entity.Property(e => e.MotorNo).HasMaxLength(100);
            entity.Property(e => e.NetAmount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Oemmodel)
                .HasMaxLength(300)
                .HasColumnName("OEMModel");
            entity.Property(e => e.PartyEmail).HasMaxLength(300);
            entity.Property(e => e.Pin).HasMaxLength(20);
            entity.Property(e => e.PostGstdisc)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("PostGSTDisc");
            entity.Property(e => e.PreGstdiscAmount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("PreGSTDiscAmount");
            entity.Property(e => e.ReferenceNo).HasMaxLength(100);
            entity.Property(e => e.RegnAmount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Remarks).HasMaxLength(500);
            entity.Property(e => e.SaleType).HasMaxLength(100);
            entity.Property(e => e.SchemeName).HasMaxLength(200);
            entity.Property(e => e.SegmentName).HasMaxLength(200);
            entity.Property(e => e.Sgstamount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("SGSTAmount");
            entity.Property(e => e.Sgstper)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(8, 2)")
                .HasColumnName("SGSTPer");
            entity.Property(e => e.SoldTo).HasMaxLength(500);
            entity.Property(e => e.State).HasMaxLength(200);
            entity.Property(e => e.StateFameIi)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("StateFameII");
            entity.Property(e => e.TotalCount).HasMaxLength(50);
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.Vcu)
                .HasMaxLength(200)
                .HasColumnName("VCU");
            entity.Property(e => e.VehicleGroup).HasMaxLength(100);
            entity.Property(e => e.VehicleType).HasMaxLength(100);
        });

        modelBuilder.Entity<OpsPodVehicleLiveDatum>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__OpsPodVe__3214EC07A7FF91CD");

            entity.Property(e => e.BatteryPercentage).HasMaxLength(50);
            entity.Property(e => e.Branch).HasMaxLength(255);
            entity.Property(e => e.Company).HasMaxLength(255);
            entity.Property(e => e.Datetime).HasMaxLength(100);
            entity.Property(e => e.DeviceModel).HasMaxLength(255);
            entity.Property(e => e.ExternalVolt).HasMaxLength(50);
            entity.Property(e => e.FetchedAt).HasColumnType("datetime");
            entity.Property(e => e.Gps)
                .HasMaxLength(50)
                .HasColumnName("GPS");
            entity.Property(e => e.GpsactualTime)
                .HasMaxLength(100)
                .HasColumnName("GPSActualTime");
            entity.Property(e => e.Ign)
                .HasMaxLength(50)
                .HasColumnName("IGN");
            entity.Property(e => e.ImeiNo).HasMaxLength(100);
            entity.Property(e => e.Latitude).HasMaxLength(50);
            entity.Property(e => e.Longitude).HasMaxLength(50);
            entity.Property(e => e.Odometer).HasMaxLength(50);
            entity.Property(e => e.Power).HasMaxLength(50);
            entity.Property(e => e.Speed).HasMaxLength(50);
            entity.Property(e => e.Status).HasMaxLength(100);
            entity.Property(e => e.Temperature).HasMaxLength(50);
            entity.Property(e => e.VehicleName).HasMaxLength(255);
            entity.Property(e => e.VehicleNo).HasMaxLength(100);
        });

        modelBuilder.Entity<SixSenseVehicleTelemetry>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__SixSense__3214EC079CF5DB28");

            entity.ToTable("SixSenseVehicleTelemetry");

            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("(getutcdate())")
                .HasColumnType("datetime");
            entity.Property(e => e.DriveMode).HasMaxLength(50);
            entity.Property(e => e.Imei).HasMaxLength(50);
            entity.Property(e => e.LastSeenAt).HasColumnType("datetime");
            entity.Property(e => e.LocationLastUpdated).HasColumnType("datetime");
            entity.Property(e => e.RegNo).HasMaxLength(50);
            entity.Property(e => e.VehicleCondition).HasMaxLength(100);
            entity.Property(e => e.VehicleId).HasMaxLength(100);
        });

        modelBuilder.Entity<DmsVehicleDispatch>(entity =>
        {
            entity.HasIndex(v => new { v.InvoiceNo, v.ChassisNo })
                .IsUnique();

            entity.HasIndex(v => v.SaleDate);

            entity.HasIndex(v => v.ChassisNo);

            entity.HasIndex(v => v.LocationCode);

            // NEW — fixes "No store type was specified for decimal property FinAmount"
            entity.Property(v => v.FinAmount).HasColumnType("decimal(18, 2)");
        });

        modelBuilder.Entity<DmsCallCentreDealer>(entity =>
        {
            entity.HasIndex(d => d.DealerCode)
                .IsUnique();

            entity.HasIndex(d => d.PinCode);
        });

        modelBuilder.Entity<DmsLineOrderReport>(entity =>
        {
            entity.ToTable("DMS_LineOrderReport");

            entity.HasKey(e => e.Id);

            entity.HasIndex(e => new { e.DealerCode, e.UniqueId })
                .IsUnique()
                .HasDatabaseName("UQ_DMS_LOR_DealerUniqueId");

            entity.HasIndex(e => e.ChassisNo)
                .HasDatabaseName("IX_DMS_LOR_ChassisNo");

            entity.HasIndex(e => e.JobDate)
                .HasDatabaseName("IX_DMS_LOR_JobDate");

            entity.HasIndex(e => e.DealerCode)
                .HasDatabaseName("IX_DMS_LOR_DealerCode");

            entity.Property(e => e.DealerName).HasMaxLength(500);
            entity.Property(e => e.DealerCode).HasMaxLength(50);
            entity.Property(e => e.UniqueId).HasMaxLength(50);
            entity.Property(e => e.LocCode).HasMaxLength(100);
            entity.Property(e => e.DocNo).HasMaxLength(50);
            entity.Property(e => e.DocType).HasMaxLength(100);
            entity.Property(e => e.JobNo).HasMaxLength(50);
            entity.Property(e => e.BrandName).HasMaxLength(200);
            entity.Property(e => e.Model).HasMaxLength(300);
            entity.Property(e => e.JobCardType).HasMaxLength(200);
            entity.Property(e => e.PaymentMode).HasMaxLength(100);
            entity.Property(e => e.PartyName).HasMaxLength(500);
            entity.Property(e => e.PartyMobile).HasMaxLength(50);
            entity.Property(e => e.RegNo).HasMaxLength(100);
            entity.Property(e => e.VehicleType).HasMaxLength(100);
            entity.Property(e => e.ChassisNo).HasMaxLength(100);
            entity.Property(e => e.Location).HasMaxLength(500);
            entity.Property(e => e.ItemName).HasMaxLength(300);
            entity.Property(e => e.ItemDescription).HasMaxLength(500);
            entity.Property(e => e.ItemType).HasMaxLength(100);
            entity.Property(e => e.Qty).HasMaxLength(50);
            entity.Property(e => e.DealerType).HasMaxLength(100);

            entity.Property(e => e.Rate).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Total).HasColumnType("decimal(18,2)");
            entity.Property(e => e.SgstPer).HasColumnType("decimal(8,2)");
            entity.Property(e => e.SgstAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.CgstPer).HasColumnType("decimal(8,2)");
            entity.Property(e => e.CgstAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.IgstPer).HasColumnType("decimal(8,2)");
            entity.Property(e => e.IgstAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Discount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Mrp).HasColumnType("decimal(18,2)");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("(getutcdate())");
        });

        modelBuilder.Entity<DmsShadowfaxChassisMaster>(entity =>
        {
            entity.ToTable("DMS_ShadowfaxChassisMaster");

            entity.HasKey(e => e.Id)
                .HasName("PK_DMS_ShadowfaxChassisMaster");

            entity.HasIndex(e => e.ChassisNo)
                .IsUnique()
                .HasDatabaseName("UQ_ShadowfaxChassis");

            entity.Property(e => e.VehicleId)
                .HasMaxLength(50);

            entity.Property(e => e.Model)
                .HasMaxLength(200);

            entity.Property(e => e.RegistrationNumber)
                .HasMaxLength(50);

            entity.Property(e => e.ChassisNo)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(e => e.City)
                .HasMaxLength(50);

            entity.Property(e => e.VehicleStatus)
                .HasMaxLength(50);

            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("(getutcdate())");

            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("(getutcdate())");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
