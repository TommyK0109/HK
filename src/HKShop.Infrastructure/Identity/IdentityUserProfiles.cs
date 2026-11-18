using System.ComponentModel.DataAnnotations;
using HKShop.Application.Accounts;
using HKShop.Domain;
using HKShop.Infrastructure.Media;
using HKShop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Infrastructure.Identity;

public sealed class IdentityUserProfiles(UserManager<ShopUser> users, ShopDbContext db) : IUserProfiles
{
    public async Task Update(string userId, UserProfileInput input, Stream? image, CancellationToken cancellationToken)
    {
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(input, new ValidationContext(input), errors, validateAllProperties: true))
            throw new ShopRuleException(errors[0].ErrorMessage ?? "Invalid profile information.");
        if (input.RemoveImage && image is not null)
            throw new ShopRuleException("Choose either to upload a new image or remove the current image.");

        var user = await users.FindByIdAsync(userId);
        if (user is null || user.Disabled) throw new ShopRuleException("Please sign in again.");

        var upload = image is null ? null : await ImageUpload.Read(image, UserProfileInput.MaxImageBytes, cancellationToken);
        if (upload is not null || input.RemoveImage)
        {
            var stored = await db.Set<UserProfileImage>().FindAsync([userId], cancellationToken);
            if (upload is not null)
            {
                if (stored is null)
                {
                    stored = new UserProfileImage { UserId = userId };
                    db.Add(stored);
                }
                stored.Data = upload.Data;
                stored.ContentType = upload.ContentType;
            }
            else if (stored is not null)
            {
                db.Remove(stored);
            }
        }

        user.DisplayName = input.DisplayName.Trim();
        user.ContactPhone = string.IsNullOrWhiteSpace(input.ContactPhone) ? null : input.ContactPhone.Trim();

        // Identity's EF store shares this context and commits the user and image in one SaveChanges call.
        try
        {
            if (!(await users.UpdateAsync(user)).Succeeded)
                throw new ShopRuleException("Could not save your profile. Please reload and try again.");
        }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new ShopRuleException("Your profile has changed. Please reload and try again.");
        }
    }

    public Task<bool> HasImage(string userId, CancellationToken cancellationToken) =>
        db.Set<UserProfileImage>().AnyAsync(image => image.UserId == userId, cancellationToken);

    public Task<ProfileImage?> GetImage(string userId, CancellationToken cancellationToken) =>
        db.Set<UserProfileImage>().AsNoTracking().Where(image => image.UserId == userId)
            .Select(image => new ProfileImage(image.Data, image.ContentType)).SingleOrDefaultAsync(cancellationToken);
}
