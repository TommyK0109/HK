using System.ComponentModel.DataAnnotations;

namespace HKShop.Application.Accounts;

public sealed class UserProfileInput
{
    public const int MaxImageBytes = 2 * 1024 * 1024;

    [Required(ErrorMessage = "Full name is required."), StringLength(100)]
    public string DisplayName { get; set; } = "";

    [StringLength(20)]
    public string? ContactPhone { get; set; }

    public bool RemoveImage { get; set; }
}

public sealed record ProfileImage(byte[] Data, string ContentType);

public interface IUserProfiles
{
    Task Update(string userId, UserProfileInput input, Stream? image, CancellationToken cancellationToken);
    Task<bool> HasImage(string userId, CancellationToken cancellationToken);
    Task<ProfileImage?> GetImage(string userId, CancellationToken cancellationToken);
}
