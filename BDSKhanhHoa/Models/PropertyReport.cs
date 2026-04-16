using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("PropertyReports")]
    public class PropertyReport
    {
        [Key]
        public int ReportID { get; set; }

        [Required]
        public int PropertyID { get; set; }

        [Required]
        public int ReportedBy { get; set; }

        [Required]
        [StringLength(255)]
        public string Reason { get; set; }

        public string? Description { get; set; }

        [StringLength(50)]
        public string Status { get; set; } = "Pending"; // Pending, Processed, Rejected

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [ForeignKey("PropertyID")]
        public virtual Property? Property { get; set; }

        [ForeignKey("ReportedBy")]
        public virtual User? User { get; set; }
    }
}