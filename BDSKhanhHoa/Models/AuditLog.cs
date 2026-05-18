using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("AuditLogs")]
    public class AuditLog
    {
        [Key]
        public int LogID { get; set; }

        public int UserID { get; set; }

        // Ví dụ:
        // Admin chỉnh sửa tin đăng
        // Phê duyệt tin đăng
        // Từ chối tin đăng
        // Xóa mềm tin đăng
        [StringLength(255)]
        public string? Action { get; set; }

        // Ví dụ:
        // Properties
        // Projects
        // Users
        // Authentication
        [StringLength(100)]
        public string? ModuleName { get; set; }

        // Chỉ lưu ngắn gọn đối tượng bị tác động.
        // Không nhét nội dung dài vào đây để tránh lỗi truncate.
        // Ví dụ: PropertyID: 152
        [StringLength(255)]
        public string? Target { get; set; }

        // Lưu dữ liệu trước khi thay đổi.
        // SQL nên là nvarchar(max), không cần StringLength.
        public string? OldValues { get; set; }

        // Lưu dữ liệu sau khi thay đổi.
        // SQL nên là nvarchar(max), không cần StringLength.
        public string? NewValues { get; set; }

        [StringLength(50)]
        public string? IPAddress { get; set; }

        [StringLength(500)]
        public string? UserAgent { get; set; }

        [StringLength(20)]
        public string Severity { get; set; } = "Info";

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [ForeignKey(nameof(UserID))]
        public virtual User? User { get; set; }
    }
}