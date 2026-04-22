using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;

namespace BDSKhanhHoa.Controllers
{
    public partial class HomeController : Controller
    {
        private readonly ApplicationDbContext _context;

        public HomeController(ApplicationDbContext context)
        {
            _context = context;
        }

        // Action hiển thị trang chủ
        public async Task<IActionResult> Index()
        {
            // TỐI ƯU HÓA: Ưu tiên gói tin VIP (PackageID cao) xếp trước, sau đó mới tới thời gian tạo
            // Nạp thêm PostServicePackage để lấy tên gói
            var vips = await _context.Properties
                .AsNoTracking() // Tối ưu hiệu năng trang chủ (chỉ đọc)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PropertyType)
                .Include(p => p.PostServicePackage)
                .Where(p => p.Status == "Approved" && p.IsDeleted == false)
                .OrderByDescending(p => p.PackageID) // Ưu tiên Kim Cương -> Vàng -> Bạc -> Thường
                .ThenByDescending(p => p.CreatedAt)  // Cùng gói thì tin nào mới xếp trước
                .Take(12) // Lấy 12 tin để giao diện đẹp hơn (3 hàng x 4 cột)
                .ToListAsync();

            // Lấy danh sách banner đang hoạt động
            ViewBag.Banners = await _context.Banners
                .AsNoTracking()
                .Where(b => b.IsActive)
                .OrderBy(b => b.DisplayOrder)
                .ToListAsync();

            // Gửi dữ liệu Khu vực qua ViewBag
            ViewBag.Areas = await _context.Areas
                .AsNoTracking()
                .OrderBy(a => a.AreaName)
                .ToListAsync();

            // Chỉ lấy đúng những thuộc tính JS cần thiết, giúp web chạy nhanh và không bị crash
            ViewBag.Types = await _context.PropertyTypes
                .AsNoTracking()
                .Select(t => new {
                    t.TypeID,
                    t.TypeName,
                    t.ParentID
                })
                .ToListAsync();

            return View(vips);
        }

        // API trả về danh sách Phường/Xã theo AreaID (Gọi từ AJAX)
        [HttpGet]
        public async Task<JsonResult> GetWardsByArea(int areaId)
        {
            var wards = await _context.Wards
                .Where(w => w.AreaID == areaId)
                .OrderBy(w => w.WardName)
                .Select(w => new {
                    wardId = w.WardID,
                    wardName = w.WardName
                })
                .ToListAsync();

            return Json(wards);
        }
    }
}