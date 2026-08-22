using Microsoft.AspNetCore.Identity;

namespace Post.Core.Infrastructure.Persistence;

public sealed class AppUser : IdentityUser<Guid>
{
    public bool IsActive { get; set; } = true;

    public ICollection<UserAllowedCenter> AllowedCenters { get; } = [];
}

public sealed class AppRole : IdentityRole<Guid>
{
}

public sealed class UserAllowedCenter
{
    public Guid UserId { get; set; }

    public string ExchangeCenterCode { get; set; } = string.Empty;

    public AppUser User { get; set; } = null!;
}

public sealed class IdentityBootstrapLock
{
    public const int SingletonId = 1;

    public int Id { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset CompletedAt { get; set; }
}
