using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;

namespace BDSKhanhHoa.Models
{
    [Table("Users")]
    public class User : IValidatableObject
    {
        [Key]
        public int UserID { get; set; }

        [Required(ErrorMessage = "Tên đăng nhập là bắt buộc")]
        [StringLength(50, MinimumLength = 4, ErrorMessage = "Tên đăng nhập phải từ 4 đến 50 ký tự")]
        [RegularExpression(@"^[a-zA-Z0-9._]+$", ErrorMessage = "Tên đăng nhập chỉ được chứa chữ, số, dấu chấm và dấu gạch dưới")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Mật khẩu là bắt buộc")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu phải từ 6 ký tự trở lên")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [NotMapped]
        [Required(ErrorMessage = "Vui lòng xác nhận mật khẩu")]
        [DataType(DataType.Password)]
        [Compare("Password", ErrorMessage = "Mật khẩu xác nhận không khớp.")]
        public string ConfirmPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email là bắt buộc")]
        [EmailAddress(ErrorMessage = "Email không đúng định dạng. Ví dụ: tenban@gmail.com")]
        [StringLength(100, ErrorMessage = "Email không được vượt quá 100 ký tự")]
        [RegularExpression(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", ErrorMessage = "Email không hợp lệ")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Họ tên là bắt buộc")]
        [StringLength(100, MinimumLength = 2, ErrorMessage = "Họ tên phải từ 2 đến 100 ký tự")]
        [RegularExpression(@"^[\p{L}\s'.-]+$", ErrorMessage = "Họ tên chỉ được chứa chữ cái, khoảng trắng và một số ký tự hợp lệ")]
        public string? FullName { get; set; }

        [StringLength(10, MinimumLength = 10, ErrorMessage = "Số điện thoại phải đủ 10 số")]
        [RegularExpression(@"^0[35789][0-9]{8}$", ErrorMessage = "Số điện thoại không hợp lệ. Phải bắt đầu bằng 03, 05, 07, 08 hoặc 09 và đủ 10 số")]
        public string? Phone { get; set; }

        [StringLength(255, ErrorMessage = "Địa chỉ không được vượt quá 255 ký tự")]
        [RegularExpression(@"^[\p{L}0-9\s,./\-#()]+$", ErrorMessage = "Địa chỉ chứa ký tự không hợp lệ")]
        public string? Address { get; set; }

        [StringLength(10, MinimumLength = 10, ErrorMessage = "Số Zalo phải đủ 10 số")]
        [RegularExpression(@"^0[35789][0-9]{8}$", ErrorMessage = "Số Zalo không hợp lệ. Phải bắt đầu bằng 03, 05, 07, 08 hoặc 09 và đủ 10 số")]
        public string? Zalo { get; set; }

        [StringLength(300, ErrorMessage = "Link Facebook không được vượt quá 300 ký tự")]
        [RegularExpression(@"^(https?:\/\/)?(www\.)?(facebook\.com|fb\.com)\/[A-Za-z0-9_.\-\/?=&]+$", ErrorMessage = "Link Facebook không hợp lệ. Vui lòng nhập link facebook.com hoặc fb.com")]
        public string? Facebook { get; set; }

        [StringLength(500, ErrorMessage = "Giới thiệu không được vượt quá 500 ký tự")]
        public string? Bio { get; set; }

        [StringLength(100, ErrorMessage = "Chức danh không vượt quá 100 ký tự")]
        [RegularExpression(@"^[\p{L}0-9\s,./\-()]+$", ErrorMessage = "Chức danh chứa ký tự không hợp lệ")]
        public string? Position { get; set; }

        /*
            Ghi chú quản trị:
            - Lưu cảnh báo vi phạm.
            - Lưu lý do khóa tài khoản.
            - Phải đồng bộ DB: ALTER COLUMN AdminNote NVARCHAR(2000) NULL.
        */
        [StringLength(2000, ErrorMessage = "Ghi chú quản trị không được vượt quá 2000 ký tự")]
        public string? AdminNote { get; set; }

        [Required(ErrorMessage = "Vui lòng chọn quyền hạn")]
        public int RoleID { get; set; }

        public bool IsActive { get; set; } = true;

        public bool IsDeleted { get; set; } = false;

        public bool? IsEmailVerified { get; set; }

        public DateTime? CreatedAt { get; set; }

        public string? Avatar { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            Username = NormalizeText(Username);
            Email = NormalizeEmail(Email);
            FullName = NormalizeText(FullName);
            Phone = NormalizePhone(Phone);
            Address = NormalizeText(Address);
            Zalo = NormalizePhone(Zalo);
            Facebook = NormalizeUrl(Facebook);
            Bio = NormalizeText(Bio);
            Position = NormalizeText(Position);
            AdminNote = NormalizeText(AdminNote);

            if (!string.IsNullOrWhiteSpace(Username))
            {
                string lowerUsername = Username.ToLower();

                string[] blockedNames =
                {
                    "admin", "administrator", "root", "system", "staff",
                    "support", "moderator", "quantri", "quanly", "bdskhanhhoa"
                };

                foreach (string blocked in blockedNames)
                {
                    if (lowerUsername == blocked)
                    {
                        yield return new ValidationResult(
                            "Tên đăng nhập này không được phép sử dụng.",
                            new[] { nameof(Username) });
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(Email))
            {
                string lowerEmail = Email.ToLower();

                string[] allowedDomains =
                {
                    "gmail.com", "yahoo.com", "outlook.com", "hotmail.com",
                    "icloud.com", "live.com", "student.hcmute.edu.vn"
                };

                string domain = lowerEmail.Contains("@")
                    ? lowerEmail.Split('@')[1]
                    : "";

                if (string.IsNullOrWhiteSpace(domain) || !domain.Contains("."))
                {
                    yield return new ValidationResult(
                        "Tên miền email không hợp lệ.",
                        new[] { nameof(Email) });
                }
            }

            if (!string.IsNullOrWhiteSpace(Phone) && !Regex.IsMatch(Phone, @"^0[35789][0-9]{8}$"))
            {
                yield return new ValidationResult(
                    "Số điện thoại không hợp lệ.",
                    new[] { nameof(Phone) });
            }

            if (!string.IsNullOrWhiteSpace(Zalo) && !Regex.IsMatch(Zalo, @"^0[35789][0-9]{8}$"))
            {
                yield return new ValidationResult(
                    "Số Zalo không hợp lệ.",
                    new[] { nameof(Zalo) });
            }

            if (!string.IsNullOrWhiteSpace(Facebook))
            {
                string fb = Facebook.ToLower();

                if (!fb.Contains("facebook.com") && !fb.Contains("fb.com"))
                {
                    yield return new ValidationResult(
                        "Facebook phải là đường dẫn thuộc facebook.com hoặc fb.com.",
                        new[] { nameof(Facebook) });
                }
            }

            if (!string.IsNullOrWhiteSpace(FullName))
            {
                string[] words = FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (words.Length < 2)
                {
                    yield return new ValidationResult(
                        "Họ tên nên nhập đầy đủ ít nhất 2 từ.",
                        new[] { nameof(FullName) });
                }
            }

            if (!string.IsNullOrWhiteSpace(Bio))
            {
                string bioLower = Bio.ToLower();

                string[] dangerousWords =
                {
                    "<script", "javascript:", "onclick=", "onerror=", "iframe"
                };

                foreach (string word in dangerousWords)
                {
                    if (bioLower.Contains(word))
                    {
                        yield return new ValidationResult(
                            "Giới thiệu chứa nội dung không hợp lệ.",
                            new[] { nameof(Bio) });
                    }
                }
            }
        }

        private static string NormalizeText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            value = value.Trim();
            value = Regex.Replace(value, @"\s+", " ");

            return value;
        }

        private static string NormalizeEmail(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            return value.Trim().ToLower();
        }

        private static string? NormalizePhone(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            value = value.Trim();
            value = Regex.Replace(value, @"[^\d]", "");

            return value;
        }

        private static string? NormalizeUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            value = value.Trim();

            if (value.StartsWith("facebook.com", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("fb.com", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("www.facebook.com", StringComparison.OrdinalIgnoreCase))
            {
                value = "https://" + value;
            }

            return value;
        }
    }
}