using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("ProjectLeads")]
    public class ProjectLead
    {
        [Key]
        public int LeadID { get; set; }

        [Required]
        public int ProjectID { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập họ tên")]
        [StringLength(100)]
        public string Name { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập số điện thoại")]
        [StringLength(20)]
        public string Phone { get; set; }

        [StringLength(100)]
        public string? Email { get; set; }

        public string? Message { get; set; }

        [StringLength(50)]
        public string LeadStatus { get; set; } = "New"; // New, Contacted, Resolved

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public string? Note { get; set; } // Ghi chú cá nhân của CRM

        [ForeignKey("ProjectID")]
        public virtual Project? Project { get; set; }
    }
}