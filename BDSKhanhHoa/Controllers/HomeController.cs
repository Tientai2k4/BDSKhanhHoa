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

        // =====================================================
        // Action hiển thị trang chủ
        // =====================================================
        public async Task<IActionResult> Index()
        {
            // 1. TỐI ƯU HÓA: Ưu tiên VIP tuyệt đối bằng PackageID gốc (4: Kim Cương > 3: Vàng > 2: Bạc > 1: Thường)
            var properties = await _context.Properties
                .AsNoTracking()
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PropertyType)
                .Include(p => p.PostServicePackage)
                .Where(p => p.Status == "Approved" && p.IsDeleted == false)
                .OrderByDescending(p => p.PackageID) // Trả lại chuẩn PackageID thay vì PriorityLevel
                .ThenByDescending(p => p.CreatedAt)  // Cùng VIP thì tin mới xếp trước
                .Take(24) // Lấy ra 24 tin để Bộ lọc JS có đủ dữ liệu thao tác
                .ToListAsync();

            // ── Banners
            ViewBag.Banners = await _context.Banners
                .AsNoTracking()
                .Where(b => b.IsActive)
                .OrderBy(b => b.DisplayOrder)
                .ToListAsync();

            // ── Khu vực (kèm số lượng tin để lọc Địa Điểm)
            var areas = await _context.Areas
                .AsNoTracking()
                .OrderBy(a => a.AreaName)
                .ToListAsync();

            var areaPropertyCounts = await _context.Properties
                .AsNoTracking()
                .Where(p => p.Status == "Approved" && p.IsDeleted == false)
                .GroupBy(p => p.Ward.AreaID)
                .Select(g => new { AreaID = g.Key, Count = g.Count() })
                .ToListAsync();

            ViewBag.Areas = areas;
            ViewBag.AreaPropertyCounts = areaPropertyCounts.ToDictionary(x => x.AreaID, x => x.Count);

            // ── PropertyTypes 
            ViewBag.Types = await _context.PropertyTypes
                .AsNoTracking()
                .Select(t => new { t.TypeID, t.TypeName, t.ParentID })
                .ToListAsync();

            // ── Tin nóng (HotNews)
            ViewBag.HotNews = await _context.Blogs
                .AsNoTracking()
                .Where(b => b.IsDeleted == false)
                .OrderByDescending(b => b.Views)
                .Take(8)
                .ToListAsync();

            // ── Tin tức thị trường (LatestNews)
            ViewBag.LatestNews = await _context.Blogs
                .AsNoTracking()
                .Where(b => b.IsDeleted == false)
                .OrderByDescending(b => b.CreatedAt)
                .Take(5)
                .ToListAsync();

            return View(properties);
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