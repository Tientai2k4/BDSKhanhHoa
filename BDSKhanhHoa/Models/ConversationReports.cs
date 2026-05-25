using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("ConversationReports")]
    public class ConversationReport
    {
        [Key]
        public int ReportID { get; set; }

        [Required]
        public int ReporterID { get; set; }

        [ForeignKey(nameof(ReporterID))]
        public User? Reporter { get; set; }

        [Required]
        public int ReportedUserID { get; set; }

        [ForeignKey(nameof(ReportedUserID))]
        public User? ReportedUser { get; set; }

        [Required]
        public int PropertyID { get; set; }

        [ForeignKey(nameof(PropertyID))]
        public Property? Property { get; set; }

        [Required]
        [StringLength(120)]
        public string Reason { get; set; } = "";

        [StringLength(2000)]
        public string? Description { get; set; }

        /*
            Pending   = Chờ xử lý
            Processed = Đã xử lý
            Rejected  = Không chấp nhận báo cáo

            Lưu tiếng Anh trong CSDL để code ổn định,
            nhưng giao diện phải hiển thị tiếng Việt.
        */
        [Required]
        [StringLength(30)]
        public string Status { get; set; } = "Pending";

        /*
            WarningOnly      = Ghi nhận và cảnh báo
            LockReportedUser = Khóa tài khoản bị báo cáo
            Reject           = Không chấp nhận báo cáo
        */
        [StringLength(50)]
        public string? AdminAction { get; set; }

        [StringLength(2000)]
        public string? AdminNote { get; set; }

        public int? ProcessedByID { get; set; }

        [ForeignKey(nameof(ProcessedByID))]
        public User? ProcessedBy { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime? ProcessedAt { get; set; }
    }
}