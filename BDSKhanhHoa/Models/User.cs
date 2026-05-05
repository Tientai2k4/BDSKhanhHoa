using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Users")]
    public class User
    {
        [Key]
        public int UserID { get; set; }

        [Required(ErrorMessage = "Tên đăng nhập là bắt buộc")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Mật khẩu là bắt buộc")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu phải từ 6 ký tự trở lên")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [NotMapped]
        [DataType(DataType.Password)]
        [Compare("Password", ErrorMessage = "Mật khẩu xác nhận không khớp.")]
        public string ConfirmPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email là bắt buộc")]
        [EmailAddress(ErrorMessage = "Email không đúng định dạng (VD: tenban@gmail.com)")]
        public string Email { get; set; } = string.Empty;

        [StringLength(100, ErrorMessage = "Họ tên không được vượt quá 100 ký tự")]
        public string? FullName { get; set; }

        [StringLength(10, ErrorMessage = "Số điện thoại tối đa 10 số")]
        [RegularExpression(@"^0[35789][0-9]{8}$", ErrorMessage = "Số điện thoại không hợp lệ (Phải bắt đầu bằng 03, 05, 07, 08, 09 và đủ 10 số)")]
        public string? Phone { get; set; }

        public string? Address { get; set; }

        [StringLength(10, ErrorMessage = "Số Zalo tối đa 10 số")]
        [RegularExpression(@"^0[35789][0-9]{8}$", ErrorMessage = "Số Zalo không hợp lệ")]
        public string? Zalo { get; set; }

        [Url(ErrorMessage = "Link Facebook không hợp lệ")]
        public string? Facebook { get; set; }

        [StringLength(500, ErrorMessage = "Giới thiệu không được vượt quá 500 ký tự")]
        public string? Bio { get; set; }

        [StringLength(100, ErrorMessage = "Chức danh không vượt quá 100 ký tự")]
        public string? Position { get; set; }

        [StringLength(500)]
        public string? AdminNote { get; set; }

        [Required]
        public int RoleID { get; set; }

        public bool IsActive { get; set; } = true;
        public bool IsDeleted { get; set; } = false;
        public bool? IsEmailVerified { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string? Avatar { get; set; }
    }
}