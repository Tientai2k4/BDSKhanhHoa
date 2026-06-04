using System;
using System.ComponentModel.DataAnnotations;

namespace BDSKhanhHoa.Models
{
    public class AIChatMessage
    {
        [Key]
        public int MessageID { get; set; }
        public int SessionID { get; set; }
        [Required]
        [StringLength(20)]
        public string Role { get; set; } = string.Empty;
        [Required]
        public string Content { get; set; } = string.Empty;
        [StringLength(80)]
        public string? Intent { get; set; }
        public string? ToolTrace { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public AIChatSession? Session { get; set; }
    }
}
