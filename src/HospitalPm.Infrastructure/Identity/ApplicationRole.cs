using Microsoft.AspNetCore.Identity;

namespace HospitalPm.Infrastructure.Identity;

public sealed class ApplicationRole : IdentityRole<int>
{
    public int TenantId { get; set; } = 1;

    public string? Description { get; set; }
}
