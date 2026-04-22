using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("PropertyFeatures")]
    public class PropertyFeature
    {
        [Key]
        public int FeatureID { get; set; }

        // QUAN TRỌNG: NULL tức là Dữ liệu gốc (Master Data) do Admin tạo.
        // Khi Client chọn và lưu tin, nó sẽ copy các dòng này và gán PropertyID cụ thể vào.
        public int? PropertyID { get; set; }

        // Lớp cha: Pháp lý, Hướng nhà, Tiện ích...
        [StringLength(100)]
        public string? FeatureGroup { get; set; }

        // Lớp con: Sổ hồng, Đông Nam, Hồ bơi...
        [StringLength(255)]
        public string? FeatureName { get; set; }

        [StringLength(255)]
        public string? FeatureValue { get; set; }

        [ForeignKey("PropertyID")]
        public virtual Property? Property { get; set; }
    }
}