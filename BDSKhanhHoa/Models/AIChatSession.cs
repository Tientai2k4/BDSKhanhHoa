using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BDSKhanhHoa.Models
{
    public class AIChatSession
    {
        [Key]
        public int SessionID { get; set; }

        [Required]
        [StringLength(100)]
        public string SessionKey { get; set; } = string.Empty;

        public int? UserID { get; set; }

        [StringLength(80)]
        public string? Scenario { get; set; }

        [StringLength(80)]
        public string? Stage { get; set; }

        [StringLength(80)]
        public string? PageType { get; set; }

        [StringLength(500)]
        public string? PageUrl { get; set; }

        [StringLength(500)]
        public string? PageTitle { get; set; }

        [StringLength(80)]
        public string? LastIntent { get; set; }

        public string? CollectedDataJson { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        public User? User { get; set; }

        public ICollection<AIChatMessage> Messages { get; set; } = new List<AIChatMessage>();
    }
}
