using System.ComponentModel.DataAnnotations;

namespace BDSKhanhHoa.Models
{
    public class AuditLog
    {
        [Key]
        public int LogID { get; set; }
        public int UserID { get; set; }
        public string? Action { get; set; }
        public string? Target { get; set; }
        public DateTime CreatedAt { get; set; }
        public virtual User? User { get; set; }
    }

}
