namespace HospitalPm.Domain.Identity;

/// <summary>
/// The four fixed roles. Constants rather than an enum because Identity
/// stores role names as strings and every authorization attribute needs the
/// literal — a typo'd string in [Authorize(Roles = "...")] fails open-ended
/// and silently, which is the failure mode this prevents.
/// </summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string BiomedicalHead = "BiomedicalHead";
    public const string SeniorEngineer = "SeniorEngineer";
    public const string Technician = "Technician";

    public static readonly IReadOnlyList<string> All =
        [Admin, BiomedicalHead, SeniorEngineer, Technician];
}
