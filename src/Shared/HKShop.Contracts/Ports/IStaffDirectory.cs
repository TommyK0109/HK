using System.ComponentModel.DataAnnotations;
using HKShop.Application;

namespace HKShop.Application.Accounts;

public sealed class RegisterInput
{
    [Required(ErrorMessage = "Name is required."), StringLength(100)]
    public string DisplayName { get; set; } = "";

    [Required(ErrorMessage = "Email is required."), EmailAddress(ErrorMessage = "Invalid email format."), StringLength(256)]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Password is required."), StringLength(128, MinimumLength = 6, ErrorMessage = "Password cannot be less than 10 characters.")]
    public string Password { get; set; } = "";
}

public sealed class UserInfo
{
    [Required(ErrorMessage = "First name is required."), StringLength(100)]
    public string FirstName { get; set; } = "";

    [Required(ErrorMessage = "Last name is required."), StringLength(100)]
    public string LastName { get; set; } = "";

    [Required(ErrorMessage = "Email is required."), EmailAddress(ErrorMessage = "Invalid email format."), StringLength(256)]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Phone number is required."), Phone(ErrorMessage = "Invalid phone number format."), StringLength(20)]
    public string PhoneNumber { get; set; } = "";

    [Required(ErrorMessage = "Date of birth is required."), DataType(DataType.Date), DisplayFormat(DataFormatString = "{0:yyyy-MM-dd}", ApplyFormatInEditMode = true)]
    public DateTime DateOfBirth { get; set; }

    [Required(ErrorMessage = "Address is required."), StringLength(200)]
    public string Address { get; set; } = "";

    [Required(ErrorMessage = "Country is required."), StringLength(100)]
    public string Country { get; set; } = "";

    [Required(ErrorMessage = "NationalID is required."), StringLength(50)]
    public string NationalID { get; set; } = "";

    [Required(ErrorMessage = "Password is required."), StringLength(128, MinimumLength = 6, ErrorMessage = "Password cannot be less than 10 characters.")]
    public string Password { get; set; } = "";
}

public sealed record StaffAccount(string Id, string Name, string? Email, bool EmailConfirmed,
    bool TwoFactorEnabled, bool Disabled, string Role, bool HasPassword);
public sealed record StaffInvitation(string UserId, string Email, string ResetToken, string ConfirmationToken);

public interface IStaffDirectory
{
    Task<Page<StaffAccount>> Browse(ShopActor actor, string? search, int page);
    Task<StaffInvitation> Invite(ShopActor actor, string email, string name, string role);
    Task<StaffInvitation> GetPendingInvitation(ShopActor actor, string id);
    Task ChangeAccess(ShopActor actor, string id, string role, bool disabled);
}
