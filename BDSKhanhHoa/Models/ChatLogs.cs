using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("ChatLogs")]
    public class ChatLogs
    {
        [Key]
        public int LogID { get; set; }

        public int? UserID { get; set; }

        public string? UserMessage { get; set; }

        public string? BotResponse { get; set; }

        public DateTime? CreatedAt { get; set; } = DateTime.Now;
    }
}