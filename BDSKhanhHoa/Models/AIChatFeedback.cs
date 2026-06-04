using System;
using System.ComponentModel.DataAnnotations;

namespace BDSKhanhHoa.Models
{
    public class AIChatFeedback
    {
        [Key]
        public int FeedbackID { get; set; }
        public int SessionID { get; set; }
        public int? MessageID { get; set; }
        public int Rating { get; set; }
        public string? Comment { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public AIChatSession? Session { get; set; }
        public AIChatMessage? Message { get; set; }
    }
}
