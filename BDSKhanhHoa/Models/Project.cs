using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("Projects")]
    public class Project
    {
        [Key]
        public int ProjectID { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập tên dự án")]
        [StringLength(255)]
        public string ProjectName { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập tên Chủ đầu tư")]
        [StringLength(255)]
        public string Investor { get; set; }

        public string? Description { get; set; }

        [StringLength(255)]
        public string? Scale { get; set; }

        public int AreaID { get; set; }
        public int WardID { get; set; }

        public string? MainImage { get; set; }
        public string? LegalDocs { get; set; }

        [StringLength(50)]
        public string? ProjectStatus { get; set; } // Sắp mở bán, Đang mở bán, Đã bàn giao

        public int UserID { get; set; }

        [StringLength(50)]
        public string? ApprovalStatus { get; set; } = "Pending";

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; } = DateTime.Now;
        public bool? IsDeleted { get; set; } = false;

        // Navigation Properties
        [ForeignKey("AreaID")]
        public virtual Area? Area { get; set; }

        [ForeignKey("WardID")]
        public virtual Ward? Ward { get; set; }

        [ForeignKey("UserID")]
        public virtual User? User { get; set; }

        public virtual ICollection<Property>? Properties { get; set; }
    }
}