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

    public virtual DbSet<OpsPodVehicleLiveDatum> OpsPodVehicleLiveData { get; set; }

    public virtual DbSet<SixSenseVehicleTelemetry> SixSenseVehicleTelemetries { get; set; }

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

            entity.HasIndex(e => e.DealerCode, "IX_DMS_Service_DealerCode");

            entity.HasIndex(e => e.JobDate, "IX_DMS_Service_JobDate");

            entity.HasIndex(e => new { e.DealerCode, e.JobNo, e.JobDate }, "UQ_DMS_Service_Job").IsUnique();

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

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
