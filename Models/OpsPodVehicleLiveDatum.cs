using System;
using System.Collections.Generic;

namespace AutoGeniusSync.Models;

public partial class OpsPodVehicleLiveDatum
{
    public int Id { get; set; }

    public DateTime FetchedAt { get; set; }

    public string? VehicleName { get; set; }

    public string? VehicleNo { get; set; }

    public string? Company { get; set; }

    public string? Status { get; set; }

    public string? Speed { get; set; }

    public string? Latitude { get; set; }

    public string? Longitude { get; set; }

    public string? Gps { get; set; }

    public string? Ign { get; set; }

    public string? Odometer { get; set; }

    public string? BatteryPercentage { get; set; }

    public string? Branch { get; set; }

    public string? DeviceModel { get; set; }

    public string? GpsactualTime { get; set; }

    public string? Datetime { get; set; }

    public string? ImeiNo { get; set; }

    public string? Power { get; set; }

    public string? Location { get; set; }

    public string? Temperature { get; set; }

    public string? ExternalVolt { get; set; }

    public string? RawJson { get; set; }
}
