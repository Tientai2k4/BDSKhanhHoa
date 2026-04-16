using System.ComponentModel.DataAnnotations;

namespace BDSKhanhHoa.Models
{
    public class ChatLogs
    {
        [Key]                                      // ← DÒNG NÀY LÀ QUAN TRỌNG NHẤT
        public int LogID { get; set; }

        public int? UserID { get; set; }

        public string? UserMessage { get; set; }

        public string? BotResponse { get; set; }

        public DateTime? CreatedAt { get; set; } = DateTime.Now;
    }
}