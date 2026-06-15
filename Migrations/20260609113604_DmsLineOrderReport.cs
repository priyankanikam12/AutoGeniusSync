using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoGeniusSync.Migrations
{
    /// <inheritdoc />
    public partial class DmsLineOrderReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DMS_AuthTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccessToken = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LoginEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    VendorName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    VendorCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    VendorId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(getutcdate())"),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__DMS_Auth__3214EC0789A5492C", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DMS_CallCentreDealers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DealerCode = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    DealerCompany = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ContactNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AlternateContactNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DealerStateName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PinCode = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    ActiveStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastFetchedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DMS_CallCentreDealers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DMS_Dealers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DealerCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DealerCompany = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ContactNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    AlternateContactNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DealerStateName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DealerCityName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PinCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ActiveStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LastFetchedAt = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(getutcdate())"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(getutcdate())"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(getutcdate())")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__DMS_Deal__3214EC07B39CA0D7", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DMS_LineOrderReport",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DealerName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DealerCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    UniqueId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LocCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DocDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DocNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DocType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    JobDate = table.Column<DateOnly>(type: "date", nullable: true),
                    JobNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    BrandName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    JobCardType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PaymentMode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PartyName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PartyMobile = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RegNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    VehicleType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ChassisNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ItemName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ItemDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ItemType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Qty = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Rate = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Total = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    SgstPer = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    SgstAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CgstPer = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    CgstAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    IgstPer = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    IgstAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Discount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Mrp = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    DealerType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(getutcdate())"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(getutcdate())")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DMS_LineOrderReport", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DMS_PincodeMaster",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PinCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(getutcdate())")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__DMS_Pinc__3214EC07CAAA8551", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DMS_ServiceHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DealerCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    JobNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    JobDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CompName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    InTime = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CloseTime = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    JobCategory = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FFRPercentage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    DocNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DocType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DocDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Model = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BrandName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RegNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    VehicleType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EngineNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ChassisNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    KMS = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    BatterySerialNo1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BatterySerialNo2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BatterySerialNo3 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BatterySerialNo4 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BatterySerialNo5 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BatterySerialNo6 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IndividualAHBattery1 = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IndividualAHBattery2 = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IndividualAHBattery3 = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IndividualAHBattery4 = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IndividualAHBattery5 = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IndividualAHBattery6 = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PartyName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MobileNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Supervisor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Technician = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ServiceHead = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    JobType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SaleDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CouponNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExpectedDeliveryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ProformaDate = table.Column<DateOnly>(type: "date", nullable: true),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EstimatedJobExpenses = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    LabourHours = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    Parts = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    Accessory = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    Oil = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    Labour = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    OutsideWork = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    TotalWOTax = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    GSTAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    IGSTAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    NetTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    IsRowTotal = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(getutcdate())"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(getutcdate())"),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RepairType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CompletionDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__DMS_Serv__3214EC07DF008F46", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DMS_SyncLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SyncType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SyncDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "(getutcdate())"),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordsFetched = table.Column<int>(type: "int", nullable: true, defaultValue: 0),
                    RecordsInserted = table.Column<int>(type: "int", nullable: true, defaultValue: 0),
                    RecordsUpdated = table.Column<int>(type: "int", nullable: true, defaultValue: 0),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DealerCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__DMS_Sync__3214EC07D94E2D38", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DMS_VehicleDispatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SaleDate = table.Column<DateOnly>(type: "date", nullable: true),
                    InvoiceNo = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Location = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LocationCode = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    LocationCity = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LocationStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DealerName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Zone = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AreaOffice = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MfgYear = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BrandName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModelCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ColorCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChassisNo = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    RegNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MotorNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BatteryId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BatteryNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EcuSerialNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EcuImEi = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EcuBalMac = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ImmoblizerNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BikeSimId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BikeMobileNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChargerNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ControllerNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SoundbarSerialNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SoundbarBalMac = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Voltage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RegNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Tyre1 = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Tyre2 = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    VehicleStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BookingId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BillNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BillDate = table.Column<DateOnly>(type: "date", nullable: true),
                    BillType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FinancerName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FinAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    NameOfParty = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Address1 = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Address2 = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    State = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    City = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Pin = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MobileNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Email = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AppPush = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LeadId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Vcu = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DMS_VehicleDispatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DMS_VehicleSales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DealerName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DealerCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    InvoiceNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Location = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LocCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LocationCity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CustDOB = table.Column<DateOnly>(type: "date", nullable: true),
                    Gender = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SoldTo = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AccountType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PartyEmail = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CusMob = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Address1 = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Address2 = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    City = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    State = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ExecutiveName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Pin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ChassisNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    MotorNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Remarks = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ItemModel = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    OEMModel = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ColorCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    VehicleType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    VehicleGroup = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    HSNSACCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SaleType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FinancedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    FinAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    ItemRate = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    InsuAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    RegnAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    AcsryAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    PreGSTDiscAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    DiscTypeName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PostGSTDisc = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    FameII = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    StateFameII = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    SGSTPer = table.Column<decimal>(type: "decimal(8,2)", nullable: true, defaultValue: 0m),
                    SGSTAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    CGSTPer = table.Column<decimal>(type: "decimal(8,2)", nullable: true, defaultValue: 0m),
                    CGSTAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    IGSTPer = table.Column<decimal>(type: "decimal(8,2)", nullable: true, defaultValue: 0m),
                    IGSTAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    NetAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    ReferenceNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BookingDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TotalCount = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Battery = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BatteryChemical = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BatteryCapacity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BatteryMake = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ChargerNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ChargerNo2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Converter = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    VCU = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ControllerNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FameIIRequired = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    SegmentName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    InstitutionalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SchemeName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(getutcdate())"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(getutcdate())")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__DMS_Vehi__3214EC0751DC185C", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpsPodVehicleLiveData",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FetchedAt = table.Column<DateTime>(type: "datetime", nullable: false),
                    VehicleName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    VehicleNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Company = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Speed = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Latitude = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Longitude = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    GPS = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IGN = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Odometer = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    BatteryPercentage = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Branch = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    DeviceModel = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    GPSActualTime = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Datetime = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ImeiNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Power = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Temperature = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ExternalVolt = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RawJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__OpsPodVe__3214EC07A7FF91CD", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SixSenseVehicleTelemetry",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VehicleId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RegNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Imei = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    BatteryPercentage = table.Column<double>(type: "float", nullable: true),
                    BatteryVoltage = table.Column<double>(type: "float", nullable: true),
                    BatteryHealth = table.Column<double>(type: "float", nullable: true),
                    BatteryTemp = table.Column<double>(type: "float", nullable: true),
                    BatteryCurrent = table.Column<double>(type: "float", nullable: true),
                    DistanceToEmpty = table.Column<double>(type: "float", nullable: true),
                    DistanceTravelledToday = table.Column<double>(type: "float", nullable: true),
                    MonthlyDistanceTravelled = table.Column<double>(type: "float", nullable: true),
                    TotalOdometer = table.Column<double>(type: "float", nullable: true),
                    TotalEnergy = table.Column<double>(type: "float", nullable: true),
                    FuelSaved = table.Column<double>(type: "float", nullable: true),
                    Co2Saved = table.Column<double>(type: "float", nullable: true),
                    LastSpeed = table.Column<double>(type: "float", nullable: true),
                    MaxSpeed = table.Column<double>(type: "float", nullable: true),
                    Latitude = table.Column<double>(type: "float", nullable: true),
                    Longitude = table.Column<double>(type: "float", nullable: true),
                    VehicleCondition = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Charging = table.Column<bool>(type: "bit", nullable: true),
                    IotConnected = table.Column<bool>(type: "bit", nullable: true),
                    DriveMode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ControllerTemp = table.Column<double>(type: "float", nullable: true),
                    TotalOperationalHours = table.Column<double>(type: "float", nullable: true),
                    MonthlyRuntime = table.Column<double>(type: "float", nullable: true),
                    DailyAvgSpeed = table.Column<double>(type: "float", nullable: true),
                    DailySpeedCount = table.Column<int>(type: "int", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "datetime", nullable: true),
                    LocationLastUpdated = table.Column<DateTime>(type: "datetime", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime", nullable: true, defaultValueSql: "(getutcdate())")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__SixSense__3214EC079CF5DB28", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DMS_CallCentreDealers_DealerCode",
                table: "DMS_CallCentreDealers",
                column: "DealerCode",
                unique: true,
                filter: "[DealerCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DMS_CallCentreDealers_PinCode",
                table: "DMS_CallCentreDealers",
                column: "PinCode");

            migrationBuilder.CreateIndex(
                name: "UQ_DMS_Dealers_DealerCode",
                table: "DMS_Dealers",
                column: "DealerCode",
                unique: true,
                filter: "[DealerCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DMS_LOR_ChassisNo",
                table: "DMS_LineOrderReport",
                column: "ChassisNo");

            migrationBuilder.CreateIndex(
                name: "IX_DMS_LOR_DealerCode",
                table: "DMS_LineOrderReport",
                column: "DealerCode");

            migrationBuilder.CreateIndex(
                name: "IX_DMS_LOR_JobDate",
                table: "DMS_LineOrderReport",
                column: "JobDate");

            migrationBuilder.CreateIndex(
                name: "UQ_DMS_LOR_DealerUniqueId",
                table: "DMS_LineOrderReport",
                columns: new[] { "DealerCode", "UniqueId" },
                unique: true,
                filter: "[DealerCode] IS NOT NULL AND [UniqueId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UQ__DMS_Pinc__70964C4FD48561BD",
                table: "DMS_PincodeMaster",
                column: "PinCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DMS_Service_ChassisNo",
                table: "DMS_ServiceHistory",
                column: "ChassisNo");

            migrationBuilder.CreateIndex(
                name: "IX_DMS_Service_DealerCode",
                table: "DMS_ServiceHistory",
                column: "DealerCode",
                unique: true,
                filter: "[DealerCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DMS_Service_JobDate",
                table: "DMS_ServiceHistory",
                column: "JobDate",
                unique: true,
                filter: "[JobDate] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UQ_DMS_Service_Job",
                table: "DMS_ServiceHistory",
                columns: new[] { "DealerCode", "JobNo" },
                unique: true,
                filter: "[DealerCode] IS NOT NULL AND [JobNo] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DMS_SyncLog_SyncDate",
                table: "DMS_SyncLog",
                columns: new[] { "SyncDate", "SyncType" });

            migrationBuilder.CreateIndex(
                name: "IX_DMS_VehicleDispatches_ChassisNo",
                table: "DMS_VehicleDispatches",
                column: "ChassisNo");

            migrationBuilder.CreateIndex(
                name: "IX_DMS_VehicleDispatches_InvoiceNo_ChassisNo",
                table: "DMS_VehicleDispatches",
                columns: new[] { "InvoiceNo", "ChassisNo" },
                unique: true,
                filter: "[InvoiceNo] IS NOT NULL AND [ChassisNo] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DMS_VehicleDispatches_LocationCode",
                table: "DMS_VehicleDispatches",
                column: "LocationCode");

            migrationBuilder.CreateIndex(
                name: "IX_DMS_VehicleDispatches_SaleDate",
                table: "DMS_VehicleDispatches",
                column: "SaleDate");

            migrationBuilder.CreateIndex(
                name: "IX_DMS_VehicleSales_ChassisNo",
                table: "DMS_VehicleSales",
                column: "ChassisNo");

            migrationBuilder.CreateIndex(
                name: "IX_DMS_VehicleSales_DealerCode",
                table: "DMS_VehicleSales",
                column: "DealerCode");

            migrationBuilder.CreateIndex(
                name: "IX_DMS_VehicleSales_InvoiceDate",
                table: "DMS_VehicleSales",
                column: "InvoiceDate");

            migrationBuilder.CreateIndex(
                name: "UQ_DMS_VehicleSales_Invoice",
                table: "DMS_VehicleSales",
                columns: new[] { "DealerCode", "InvoiceNo" },
                unique: true,
                filter: "[DealerCode] IS NOT NULL AND [InvoiceNo] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DMS_AuthTokens");

            migrationBuilder.DropTable(
                name: "DMS_CallCentreDealers");

            migrationBuilder.DropTable(
                name: "DMS_Dealers");

            migrationBuilder.DropTable(
                name: "DMS_LineOrderReport");

            migrationBuilder.DropTable(
                name: "DMS_PincodeMaster");

            migrationBuilder.DropTable(
                name: "DMS_ServiceHistory");

            migrationBuilder.DropTable(
                name: "DMS_SyncLog");

            migrationBuilder.DropTable(
                name: "DMS_VehicleDispatches");

            migrationBuilder.DropTable(
                name: "DMS_VehicleSales");

            migrationBuilder.DropTable(
                name: "OpsPodVehicleLiveData");

            migrationBuilder.DropTable(
                name: "SixSenseVehicleTelemetry");
        }
    }
}
