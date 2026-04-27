using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Projects")]
    public class Project
    {
        [Key]
        public int ProjectID { get; set; }

        [Required(ErrorMessage = "Tên dự án không được để trống")]
        [StringLength(255)]
        public string ProjectName { get; set; }

        [Required(ErrorMessage = "Tên Chủ đầu tư không được để trống")]
        [StringLength(255)]
        public string Investor { get; set; }

        [Required(ErrorMessage = "Mô tả ngắn không được để trống")]
        public string Description { get; set; }

        public string? ContentHtml { get; set; }

        // --- BỔ SUNG CÁC TRƯỜNG CHUYÊN NGHIỆP ---
        [StringLength(500)]
        public string? AddressDetail { get; set; } // Địa chỉ chính xác số nhà, tên đường

        [Column(TypeName = "decimal(18,2)")]
        public decimal? PriceMin { get; set; } // Giá thấp nhất (Vd: 2 tỷ)

        [Column(TypeName = "decimal(18,2)")]
        public decimal? PriceMax { get; set; } // Giá cao nhất

        public string? PriceUnit { get; set; } = "Tỷ"; // Đơn vị: Tỷ, Triệu/m2

        public double? AreaMin { get; set; } // Diện tích nhỏ nhất (Vd: 45m2)
        public double? AreaMax { get; set; } // Diện tích lớn nhất

        [StringLength(255)]
        public string? Scale { get; set; } // Quy mô: 3 tòa, 1500 căn hộ...

        public string? ConstructionDensity { get; set; } // Mật độ xây dựng (Vd: 25%)

        public string? Utilities { get; set; } // Tiện ích: Hồ bơi, Công viên, Gym...

        [StringLength(100)]
        public string? ProjectType { get; set; } // Loại hình: Căn hộ, Đất nền, Biệt thự
        // ---------------------------------------

        [Required]
        public int AreaID { get; set; }

        [Required]
        public int WardID { get; set; }

        public string? MainImage { get; set; }
        public string? Thumbnail { get; set; }
        public string? LegalDocs { get; set; }

        [StringLength(50)]
        public string ProjectStatus { get; set; } = "Đang mở bán";

        [StringLength(50)]
        public string ApprovalStatus { get; set; } = "Approved";

        [Required]
        public int OwnerUserID { get; set; }

        public DateTime? PublishedAt { get; set; } = DateTime.Now;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; }
        public bool IsDeleted { get; set; } = false;

        [ForeignKey("OwnerUserID")]
        public virtual User? Owner { get; set; }

        [ForeignKey("AreaID")]
        public virtual Area? Area { get; set; }

        [ForeignKey("WardID")]
        public virtual Ward? Ward { get; set; }
    }
}