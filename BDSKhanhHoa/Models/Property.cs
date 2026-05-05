using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Properties")]
    public class Property
    {
        [Key]
        public int PropertyID { get; set; }

        [Required(ErrorMessage = "Tiêu đề không được để trống")]
        [StringLength(255)]
        public string Title { get; set; }

        public string? Description { get; set; }

        [StringLength(255)]
        public string? AddressDetail { get; set; }

        public int? ProjectID { get; set; }

        [ForeignKey("ProjectID")]
        public virtual Project? Project { get; set; }

        public int WardID { get; set; }
        public int TypeID { get; set; }
        public int UserID { get; set; }
        public int PackageID { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Price { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal? AreaSize { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal? Width { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal? Length { get; set; }

        [StringLength(20)]
        public string? Status { get; set; } = "Pending"; // Pending, Approved, Rejected, Deleted, Sold, Rented

        public DateTime? ApprovedAt { get; set; }
        public string? MainImage { get; set; }

        public DateTime? VipExpiryDate { get; set; }
        public int? Views { get; set; } = 0;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; } = DateTime.Now;

        // Trường lưu vết thời gian giao dịch hoàn tất
        public DateTime? SoldAt { get; set; }

        public bool IsAutoApproved { get; set; } = false;
        public bool IsDuplicate { get; set; } = false;
        public string? DuplicateReason { get; set; }

        public bool? IsDeleted { get; set; } = false;
        public string? RejectionReason { get; set; }

        [ForeignKey("TypeID")]
        public virtual PropertyType? PropertyType { get; set; }

        [ForeignKey("WardID")]
        public virtual Ward? Ward { get; set; }

        [ForeignKey("UserID")]
        public virtual User? User { get; set; }

        [ForeignKey("PackageID")]
        public virtual PostServicePackage? PostServicePackage { get; set; }
    }
}