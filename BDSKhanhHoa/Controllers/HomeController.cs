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
            // Lấy danh sách bất động sản nổi bật (Đã duyệt và sắp xếp mới nhất)
            var vips = await _context.Properties
                .Include(p => p.Ward)
                    .ThenInclude(w => w.Area)
                .Include(p => p.PropertyType) // Nạp thông tin loại nhà đất
                .Where(p => p.Status == "Approved")
                .OrderByDescending(p => p.CreatedAt)
                .Take(8)
                .ToListAsync();

            // Lấy danh sách banner đang hoạt động
            ViewBag.Banners = await _context.Banners
                .Where(b => b.IsActive)
                .OrderBy(b => b.DisplayOrder)
                .ToListAsync();

            // Gửi dữ liệu Khu vực qua ViewBag
            ViewBag.Areas = await _context.Areas.OrderBy(a => a.AreaName).ToListAsync();

            // SỬA LỖI TẠI ĐÂY: Dùng Select() để ngắt vòng lặp vô tận (Self referencing loop)
            // Chỉ lấy đúng những thuộc tính JS cần thiết, giúp web chạy nhanh và không bị crash
            ViewBag.Types = await _context.PropertyTypes
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