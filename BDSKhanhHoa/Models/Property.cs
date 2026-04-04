using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    public class Property
    {
        [Key]
        public int PropertyID { get; set; }

        public string Title { get; set; }
        public string? Description { get; set; }

        // Cập nhật: Thêm cột này để khớp với SQL của bạn
        public string? AddressDetail { get; set; }

        public decimal? Price { get; set; }
        public decimal? AreaSize { get; set; }
        public string? Status { get; set; } = "Pending";
        public string? MainImage { get; set; }

        public int WardID { get; set; }
        public int TypeID { get; set; }
        public int UserID { get; set; }
        public int PackageID { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // --- NAVIGATION PROPERTIES (Dùng để lấy dữ liệu bảng liên quan) ---

        [ForeignKey("TypeID")]
        public virtual PropertyType? PropertyType { get; set; }

        [ForeignKey("WardID")]
        public virtual Ward? Ward { get; set; }

        [ForeignKey("UserID")]
        public virtual User? User { get; set; }
    }
}