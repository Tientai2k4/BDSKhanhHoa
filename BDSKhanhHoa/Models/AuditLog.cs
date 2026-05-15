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

        [StringLength(255)]
        public string? Action { get; set; }

        [StringLength(100)]
        public string? ModuleName { get; set; }

        [StringLength(255)]
        public string? Target { get; set; }

        public string? OldValues { get; set; }

        public string? NewValues { get; set; }

        [StringLength(50)]
        public string? IPAddress { get; set; }

        [StringLength(500)]
        public string? UserAgent { get; set; }

        [StringLength(20)]
        public string Severity { get; set; } = "Info";

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [ForeignKey("UserID")]
        public virtual User? User { get; set; }
    }
}