using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Security.Claims;
using System;

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
            // 0. LẤY DANH SÁCH TIN ĐÃ LƯU (YÊU THÍCH) CỦA USER ĐANG ĐĂNG NHẬP
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            List<int> favoritedIds = new List<int>();
            if (int.TryParse(userIdClaim, out int userId))
            {
                favoritedIds = await _context.Favorites
                    .Where(f => f.UserID == userId)
                    .Select(f => f.PropertyID)
                    .ToListAsync();
            }
            ViewBag.FavoritedIds = favoritedIds;

            // 1. TỐI ƯU HÓA: Ưu tiên VIP tuyệt đối kết hợp Tin Mới Nhất
            // - OrderBy PriorityLevel: Sắp xếp theo cấp bậc VIP (Kim cương = 10, Vàng = 40...) -> Số càng nhỏ càng ưu tiên.
            // - ThenByDescending CreatedAt: Trong cùng 1 gói VIP, tin nào mới đăng sẽ hiển thị trước.
            var properties = await _context.Properties
                .AsNoTracking()
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PropertyType)
                .Include(p => p.PostServicePackage)
                .Where(p => p.Status == "Approved" && p.IsDeleted == false)
                .OrderBy(p => p.PostServicePackage != null && p.PostServicePackage.PriorityLevel > 0 ? p.PostServicePackage.PriorityLevel : 9999)
                .ThenByDescending(p => p.CreatedAt)
                .Take(24)
                .ToListAsync();

            // 2. LẤY DANH SÁCH DỰ ÁN THỰC TẾ TỪ DATABASE (MỚI NHẤT LÊN ĐẦU)
            ViewBag.LatestProjects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Area)
                .Where(p => p.ApprovalStatus == "Approved" && p.IsDeleted == false)
                .OrderByDescending(p => p.PublishedAt)
                .Take(4)
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

        // =====================================================
        // API xử lý nút Lưu tin (Trái tim) bằng AJAX
        // =====================================================
        [HttpPost]
        public async Task<IActionResult> ToggleFavorite(int propertyId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId))
                return Json(new { success = false, message = "Vui lòng đăng nhập để lưu tin." });

            var existingFav = await _context.Favorites
                .FirstOrDefaultAsync(f => f.PropertyID == propertyId && f.UserID == userId);

            bool isSaved = false;

            if (existingFav != null)
            {
                _context.Favorites.Remove(existingFav); // Nếu đã lưu thì bỏ lưu
            }
            else
            {
                _context.Favorites.Add(new Favorite { PropertyID = propertyId, UserID = userId, CreatedAt = DateTime.Now });
                isSaved = true; // Lưu thành công
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true, isSaved = isSaved });
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