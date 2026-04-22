// Models/Transaction.cs
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Transactions")]
    public class Transaction
    {
        [Key]
        public int TransactionID { get; set; }

        public int UserID { get; set; }
        public int? PackageID { get; set; }
        public int? PropertyID { get; set; }

        [Required]
        public int Quantity { get; set; } = 1; // Bổ sung số lượng gói tin mua

        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        [StringLength(50)]
        public string Type { get; set; }

        [StringLength(50)]
        public string PaymentMethod { get; set; }

        [StringLength(255)]
        public string TransactionCode { get; set; }

        [StringLength(50)]
        public string Status { get; set; }
        [StringLength(500)]
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [ForeignKey("UserID")]
        public virtual User? User { get; set; }

        [ForeignKey("PackageID")]
        public virtual PostServicePackage? Package { get; set; }

        [ForeignKey("PropertyID")]
        public virtual Property? Property { get; set; }
    }
}