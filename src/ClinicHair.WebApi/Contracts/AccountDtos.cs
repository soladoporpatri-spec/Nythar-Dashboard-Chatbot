using System.ComponentModel.DataAnnotations;

namespace ClinicHair.WebApi.Contracts;

public sealed record ChangePasswordRequest
{
    [Required, MaxLength(100)]
    public required string CurrentPassword { get; init; }

    [Required, MaxLength(100)]
    public required string NewPassword { get; init; }
}

public sealed record ReauthRequest
{
    [Required, MaxLength(100)]
    public required string Password { get; init; }
}

public sealed record ReauthResponse(string Ticket, DateTimeOffset ExpiresAt);
