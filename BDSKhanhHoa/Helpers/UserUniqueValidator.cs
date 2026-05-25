using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Helpers
{
    public static class UserUniqueValidator
    {
        public static void NormalizeUser(User user)
        {
            user.Username = UserInputHelper.NormalizeText(user.Username);
            user.Email = UserInputHelper.NormalizeEmail(user.Email);
            user.FullName = UserInputHelper.NormalizeText(user.FullName);
            user.Phone = UserInputHelper.NormalizePhone(user.Phone);
            user.Address = UserInputHelper.NormalizeText(user.Address);
            user.Zalo = UserInputHelper.NormalizePhone(user.Zalo);
            user.Facebook = UserInputHelper.NormalizeFacebook(user.Facebook);
            user.Bio = UserInputHelper.NormalizeText(user.Bio);
            user.Position = UserInputHelper.NormalizeText(user.Position);
            user.AdminNote = UserInputHelper.Cut(user.AdminNote, 2000);
        }

        public static async Task<bool> ValidateForCreateAsync(
            ApplicationDbContext context,
            User user,
            ModelStateDictionary modelState)
        {
            NormalizeUser(user);

            bool valid = true;

            if (string.IsNullOrWhiteSpace(user.Username))
            {
                modelState.AddModelError("Username", "Tên đăng nhập là bắt buộc.");
                valid = false;
            }

            if (string.IsNullOrWhiteSpace(user.Email))
            {
                modelState.AddModelError("Email", "Email là bắt buộc.");
                valid = false;
            }

            if (string.IsNullOrWhiteSpace(user.FullName))
            {
                modelState.AddModelError("FullName", "Họ tên là bắt buộc.");
                valid = false;
            }

            if (!UserInputHelper.IsValidPhone(user.Phone))
            {
                modelState.AddModelError("Phone", "Số điện thoại không hợp lệ.");
                valid = false;
            }

            if (!UserInputHelper.IsValidPhone(user.Zalo))
            {
                modelState.AddModelError("Zalo", "Số Zalo không hợp lệ.");
                valid = false;
            }

            if (!UserInputHelper.IsValidFacebook(user.Facebook))
            {
                modelState.AddModelError("Facebook", "Link Facebook không hợp lệ.");
                valid = false;
            }

            if (UserInputHelper.HasDangerousHtml(user.Bio))
            {
                modelState.AddModelError("Bio", "Giới thiệu chứa nội dung không hợp lệ.");
                valid = false;
            }

            bool usernameExists = await context.Users
                .AnyAsync(u =>
                    !u.IsDeleted &&
                    u.Username.ToLower() == user.Username.ToLower());

            if (usernameExists)
            {
                modelState.AddModelError("Username", "Tên đăng nhập đã tồn tại. Vui lòng chọn tên khác.");
                valid = false;
            }

            bool emailExists = await context.Users
                .AnyAsync(u =>
                    !u.IsDeleted &&
                    u.Email.ToLower() == user.Email.ToLower());

            if (emailExists)
            {
                modelState.AddModelError("Email", "Email này đã được sử dụng trong hệ thống.");
                valid = false;
            }

            if (!string.IsNullOrWhiteSpace(user.Phone))
            {
                bool phoneExists = await context.Users
                    .AnyAsync(u =>
                        !u.IsDeleted &&
                        u.Phone != null &&
                        u.Phone == user.Phone);

                if (phoneExists)
                {
                    modelState.AddModelError("Phone", "Số điện thoại này đã được tài khoản khác sử dụng.");
                    valid = false;
                }
            }

            if (!string.IsNullOrWhiteSpace(user.Zalo))
            {
                bool zaloExists = await context.Users
                    .AnyAsync(u =>
                        !u.IsDeleted &&
                        u.Zalo != null &&
                        u.Zalo == user.Zalo);

                if (zaloExists)
                {
                    modelState.AddModelError("Zalo", "Số Zalo này đã được tài khoản khác sử dụng.");
                    valid = false;
                }
            }

            if (!string.IsNullOrWhiteSpace(user.Facebook))
            {
                bool facebookExists = await context.Users
                    .AnyAsync(u =>
                        !u.IsDeleted &&
                        u.Facebook != null &&
                        u.Facebook.ToLower() == user.Facebook.ToLower());

                if (facebookExists)
                {
                    modelState.AddModelError("Facebook", "Link Facebook này đã được tài khoản khác sử dụng.");
                    valid = false;
                }
            }

            return valid;
        }

        public static async Task<bool> ValidateForUpdateAsync(
            ApplicationDbContext context,
            User user,
            ModelStateDictionary modelState)
        {
            NormalizeUser(user);

            bool valid = true;

            if (user.UserID <= 0)
            {
                modelState.AddModelError("", "Không xác định được tài khoản cần cập nhật.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(user.FullName))
            {
                modelState.AddModelError("FullName", "Họ tên là bắt buộc.");
                valid = false;
            }

            if (!UserInputHelper.IsValidPhone(user.Phone))
            {
                modelState.AddModelError("Phone", "Số điện thoại không hợp lệ.");
                valid = false;
            }

            if (!UserInputHelper.IsValidPhone(user.Zalo))
            {
                modelState.AddModelError("Zalo", "Số Zalo không hợp lệ.");
                valid = false;
            }

            if (!UserInputHelper.IsValidFacebook(user.Facebook))
            {
                modelState.AddModelError("Facebook", "Link Facebook không hợp lệ.");
                valid = false;
            }

            if (UserInputHelper.HasDangerousHtml(user.Bio))
            {
                modelState.AddModelError("Bio", "Giới thiệu chứa nội dung không hợp lệ.");
                valid = false;
            }

            if (!string.IsNullOrWhiteSpace(user.Phone))
            {
                bool phoneExists = await context.Users
                    .AnyAsync(u =>
                        !u.IsDeleted &&
                        u.UserID != user.UserID &&
                        u.Phone != null &&
                        u.Phone == user.Phone);

                if (phoneExists)
                {
                    modelState.AddModelError("Phone", "Số điện thoại này đã được tài khoản khác sử dụng.");
                    valid = false;
                }
            }

            if (!string.IsNullOrWhiteSpace(user.Zalo))
            {
                bool zaloExists = await context.Users
                    .AnyAsync(u =>
                        !u.IsDeleted &&
                        u.UserID != user.UserID &&
                        u.Zalo != null &&
                        u.Zalo == user.Zalo);

                if (zaloExists)
                {
                    modelState.AddModelError("Zalo", "Số Zalo này đã được tài khoản khác sử dụng.");
                    valid = false;
                }
            }

            if (!string.IsNullOrWhiteSpace(user.Facebook))
            {
                bool facebookExists = await context.Users
                    .AnyAsync(u =>
                        !u.IsDeleted &&
                        u.UserID != user.UserID &&
                        u.Facebook != null &&
                        u.Facebook.ToLower() == user.Facebook.ToLower());

                if (facebookExists)
                {
                    modelState.AddModelError("Facebook", "Link Facebook này đã được tài khoản khác sử dụng.");
                    valid = false;
                }
            }

            return valid;
        }

        public static async Task<bool> ValidateRegisterAsync(
            ApplicationDbContext context,
            string username,
            string email,
            string? phone,
            ModelStateDictionary modelState)
        {
            username = UserInputHelper.NormalizeText(username);
            email = UserInputHelper.NormalizeEmail(email);
            phone = UserInputHelper.NormalizePhone(phone);

            bool valid = true;

            bool usernameExists = await context.Users
                .AnyAsync(u =>
                    !u.IsDeleted &&
                    u.Username.ToLower() == username.ToLower());

            if (usernameExists)
            {
                modelState.AddModelError("Username", "Tên đăng nhập này đã tồn tại. Vui lòng chọn tên khác.");
                valid = false;
            }

            bool verifiedEmailExists = await context.Users
                .AnyAsync(u =>
                    !u.IsDeleted &&
                    u.Email.ToLower() == email.ToLower() &&
                    u.IsEmailVerified == true);

            if (verifiedEmailExists)
            {
                modelState.AddModelError("Email", "Email này đã được đăng ký và xác thực. Vui lòng đăng nhập.");
                valid = false;
            }

            if (!string.IsNullOrWhiteSpace(phone))
            {
                bool phoneExists = await context.Users
                    .AnyAsync(u =>
                        !u.IsDeleted &&
                        u.Phone != null &&
                        u.Phone == phone);

                if (phoneExists)
                {
                    modelState.AddModelError("Phone", "Số điện thoại này đã được tài khoản khác sử dụng.");
                    valid = false;
                }
            }

            return valid;
        }
    }
}