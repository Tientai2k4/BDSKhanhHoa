using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDSKhanhHoa.Models
{
    [Table("PropertyTypes")]
    public class PropertyType
    {
        [Key]
        public int TypeID { get; set; }

        [Required(ErrorMessage = "Tên loại BĐS không được để trống")]
        [StringLength(100)]
        public string TypeName { get; set; }

        public string? Description { get; set; }

        // Logic phân cấp: Cha là "Bán" hoặc "Thuê"
        public int? ParentID { get; set; }

        public virtual ICollection<Property>? Properties { get; set; }
    }
}