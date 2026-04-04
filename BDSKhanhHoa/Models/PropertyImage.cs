namespace BDSKhanhHoa.Models
{
    public class PropertyImage
    {
        public int ImageID { get; set; }
        public int PropertyID { get; set; }
        public string ImageURL { get; set; }
        public bool IsMain { get; set; }
    }
}