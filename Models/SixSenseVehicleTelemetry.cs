using System;
using System.Collections.Generic;

namespace AutoGeniusSync.Models;

public partial class SixSenseVehicleTelemetry
{
    public int Id { get; set; }

    public string? VehicleId { get; set; }

    public string? RegNo { get; set; }

    public string? Imei { get; set; }

    public double? BatteryPercentage { get; set; }

    public double? BatteryVoltage { get; set; }

    public double? BatteryHealth { get; set; }

    public double? BatteryTemp { get; set; }

    public double? BatteryCurrent { get; set; }

    public double? DistanceToEmpty { get; set; }

    public double? DistanceTravelledToday { get; set; }

    public double? MonthlyDistanceTravelled { get; set; }

    public double? TotalOdometer { get; set; }

    public double? TotalEnergy { get; set; }

    public double? FuelSaved { get; set; }

    public double? Co2Saved { get; set; }

    public double? LastSpeed { get; set; }

    public double? MaxSpeed { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public string? VehicleCondition { get; set; }

    public bool? Charging { get; set; }

    public bool? IotConnected { get; set; }

    public string? DriveMode { get; set; }

    public double? ControllerTemp { get; set; }

    public double? TotalOperationalHours { get; set; }

    public double? MonthlyRuntime { get; set; }

    public double? DailyAvgSpeed { get; set; }

    public int? DailySpeedCount { get; set; }

    public DateTime? LastSeenAt { get; set; }

    public DateTime? LocationLastUpdated { get; set; }

    public DateTime? CreatedAt { get; set; }
}
