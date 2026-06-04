using System;
using System.ComponentModel.DataAnnotations;

namespace BDSKhanhHoa.Models
{
    public class AIKnowledgeArticle
    {
        [Key]
        public int ArticleID { get; set; }
        [Required]
        [StringLength(255)]
        public string Title { get; set; } = string.Empty;
        [Required]
        [StringLength(80)]
        public string Category { get; set; } = "General";
        [Required]
        public string Content { get; set; } = string.Empty;
        public bool IsPublished { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
