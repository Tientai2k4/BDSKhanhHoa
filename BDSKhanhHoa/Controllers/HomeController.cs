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
            var now = DateTime.Now;

            // ---------------------------------------------------------
            // TỰ ĐỘNG HẠ CẤP TIN VIP HẾT HẠN VỀ TIN THƯỜNG
            // Quy ước hạng:
            // 1 = Kim Cương, 2 = Vàng, 3 = Bạc, 4 = Đồng, 5 = Tin Thường
            // ---------------------------------------------------------
            var normalPackage = await _context.PostServicePackages
                .AsNoTracking()
                .Where(p => p.PackageType == "Tin Thường")
                .OrderBy(p => p.PriorityLevel <= 0 ? 9999 : p.PriorityLevel)
                .ThenBy(p => p.Price)
                .FirstOrDefaultAsync();

            if (normalPackage == null)
            {
                normalPackage = await _context.PostServicePackages
                    .AsNoTracking()
                    .Where(p => p.PriorityLevel == 5)
                    .OrderBy(p => p.Price)
                    .FirstOrDefaultAsync();
            }

            if (normalPackage != null)
            {
                var expiredVipProperties = await _context.Properties
                    .Include(p => p.PostServicePackage)
                    .Where(p => p.VipExpiryDate.HasValue
                             && p.VipExpiryDate.Value < now
                             && p.PackageID != normalPackage.PackageID
                             && p.PostServicePackage != null
                             && p.PostServicePackage.PackageType != "Tin Thường")
                    .ToListAsync();

                if (expiredVipProperties.Any())
                {
                    foreach (var prop in expiredVipProperties)
                    {
                        prop.PackageID = normalPackage.PackageID;
                        prop.VipExpiryDate = null;
                    }

                    await _context.SaveChangesAsync();
                }
            }

            // ---------------------------------------------------------
            // 0. LẤY DANH SÁCH TIN ĐÃ LƯU CỦA USER ĐANG ĐĂNG NHẬP
            // ---------------------------------------------------------
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            List<int> favoritedIds = new List<int>();

            if (int.TryParse(userIdClaim, out int userId))
            {
                favoritedIds = await _context.Favorites
                    .AsNoTracking()
                    .Where(f => f.UserID == userId)
                    .Select(f => f.PropertyID)
                    .ToListAsync();
            }

            ViewBag.FavoritedIds = favoritedIds;

            // ---------------------------------------------------------
            // 1. TIN TRANG CHỦ: VIP CAO NHẤT TRƯỚC, TRONG CÙNG VIP THÌ TIN MỚI NHẤT TRƯỚC
            // PriorityLevel càng nhỏ càng cao:
            // Kim Cương 1 -> Vàng 2 -> Bạc 3 -> Đồng 4 -> Tin Thường 5 -> Không gói 9999
            // ---------------------------------------------------------
            var properties = await _context.Properties
                .AsNoTracking()
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PropertyType)
                .Include(p => p.PostServicePackage)
                .Where(p => p.Status == "Approved" && p.IsDeleted == false)
                .OrderBy(p =>
                    p.PostServicePackage == null || p.PostServicePackage.PriorityLevel <= 0
                        ? 9999
                        : p.PostServicePackage.PriorityLevel)
                .ThenByDescending(p => p.CreatedAt)
                .ThenByDescending(p => p.PropertyID)
                .Take(24)
                .ToListAsync();

            // ---------------------------------------------------------
            // 2. DỰ ÁN NỔI BẬT
            // ---------------------------------------------------------
            ViewBag.LatestProjects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Area)
                .Where(p => p.ApprovalStatus == "Approved" && p.IsDeleted == false)
                .OrderByDescending(p => p.PublishedAt)
                .Take(4)
                .ToListAsync();

            // ---------------------------------------------------------
            // 3. BANNERS
            // ---------------------------------------------------------
            ViewBag.Banners = await _context.Banners
                .AsNoTracking()
                .Where(b => b.IsActive)
                .OrderBy(b => b.DisplayOrder)
                .ToListAsync();

            // ---------------------------------------------------------
            // 4. KHU VỰC KÈM SỐ LƯỢNG TIN
            // ---------------------------------------------------------
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

            // ---------------------------------------------------------
            // 5. LOẠI BẤT ĐỘNG SẢN
            // ---------------------------------------------------------
            ViewBag.Types = await _context.PropertyTypes
                .AsNoTracking()
                .Select(t => new { t.TypeID, t.TypeName, t.ParentID })
                .ToListAsync();

            // ---------------------------------------------------------
            // 6. TIN NÓNG & TIN THỊ TRƯỜNG
            // ---------------------------------------------------------
            ViewBag.HotNews = await _context.Blogs
                .AsNoTracking()
                .Where(b => b.IsDeleted == false)
                .OrderByDescending(b => b.Views)
                .Take(8)
                .ToListAsync();

            ViewBag.LatestNews = await _context.Blogs
                .AsNoTracking()
                .Where(b => b.IsDeleted == false)
                .OrderByDescending(b => b.CreatedAt)
                .Take(5)
                .ToListAsync();

            return View(properties);
        }
        // =====================================================
        // API xử lý đề xuất tìm kiếm thông minh (AUTO-SUGGEST)
        // =====================================================
        [HttpGet]
        public async Task<IActionResult> Suggest(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return Json(new List<object>());

            keyword = keyword.ToLower();

            // Tìm kiếm đa luồng: Tiêu đề BĐS, Tên Dự án, Địa chỉ
            var suggestions = await _context.Properties
                .AsNoTracking()
                .Include(p => p.Project)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.Status == "Approved" && p.IsDeleted == false &&
                            (p.Title.ToLower().Contains(keyword) ||
                             p.AddressDetail.ToLower().Contains(keyword) ||
                             (p.Project != null && p.Project.ProjectName.ToLower().Contains(keyword))))
                .Select(p => new {
                    title = p.Title,
                    address = $"{p.AddressDetail}, {p.Ward.WardName}, {p.Ward.Area.AreaName}",
                    type = p.Project != null ? "Dự án: " + p.Project.ProjectName : "Tin BĐS"
                })
                .Distinct()
                .Take(7)
                .ToListAsync();

            return Json(suggestions);
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
                _context.Favorites.Remove(existingFav);
            }
            else
            {
                _context.Favorites.Add(new Favorite { PropertyID = propertyId, UserID = userId, CreatedAt = DateTime.Now });
                isSaved = true;
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true, isSaved = isSaved });
        }

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