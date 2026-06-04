// Models/Transaction.cs
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Transactions")]
    public class Transaction
    {
        [Key]
        public int TransactionID { get; set; }

        [Required]
        public int UserID { get; set; }

        public int? PackageID { get; set; }

        public int? PropertyID { get; set; }

        [Required]
        public int Quantity { get; set; } = 1;

        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        [StringLength(50)]
        public string Type { get; set; } = "";

        [StringLength(50)]
        public string PaymentMethod { get; set; } = "";

        [StringLength(255)]
        public string TransactionCode { get; set; } = "";

        [StringLength(50)]
        public string Status { get; set; } = "Pending";
        // Pending, Success, Completed, Failed, Cancelled

        [Column(TypeName = "nvarchar(max)")]
        public string? Description { get; set; }

        [StringLength(500)]
        public string? BillImageUrl { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Hạn cuối thanh toán. Quá thời gian này mà vẫn Pending thì tự hủy.
        public DateTime? ExpiresAt { get; set; }

        // Thời điểm hệ thống đóng/hủy giao dịch.
        public DateTime? CancelledAt { get; set; }

        [ForeignKey("UserID")]
        public virtual User? User { get; set; }

        [ForeignKey("PackageID")]
        public virtual PostServicePackage? Package { get; set; }

        [ForeignKey("PropertyID")]
        public virtual Property? Property { get; set; }
    }
}