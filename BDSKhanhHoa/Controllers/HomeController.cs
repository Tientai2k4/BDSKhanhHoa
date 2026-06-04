using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    public partial class HomeController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IMemoryCache _cache;

        public HomeController(ApplicationDbContext context, IMemoryCache cache)
        {
            _context = context;
            _cache = cache;
        }

        // =====================================================
        // TRANG CHỦ - BẢN TỐI ƯU NHANH
        // Không xử lý nặng lặp lại nhiều lần, không kéo dữ liệu thừa quá nhiều.
        // =====================================================
        public async Task<IActionResult> Index()
        {
            int currentUserId = 0;
            string? userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (!string.IsNullOrWhiteSpace(userIdClaim))
            {
                int.TryParse(userIdClaim, out currentUserId);
            }

            List<int> favoritedIds = currentUserId > 0
                ? await _context.Favorites
                    .AsNoTracking()
                    .Where(f => f.UserID == currentUserId)
                    .Select(f => f.PropertyID)
                    .ToListAsync()
                : new List<int>();

            ViewBag.FavoritedIds = favoritedIds;

            List<Property> properties = await _cache.GetOrCreateAsync("HOME_FEATURED_PROPERTIES_FINAL", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);

                IQueryable<Property> basePropertyQuery = _context.Properties
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Include(p => p.Ward)
                        .ThenInclude(w => w.Area)
                    .Include(p => p.PropertyType)
                    .Include(p => p.PostServicePackage)
                    .Where(p => p.Status == "Approved" && p.IsDeleted == false);

                List<Property> featuredBuyProperties = await basePropertyQuery
                    .Where(p =>
                        p.PropertyType != null &&
                        (
                            p.PropertyType.ParentID == 1 ||
                            p.TypeID == 1
                        ))
                    .OrderBy(p =>
                        p.PostServicePackage == null || p.PostServicePackage.PriorityLevel <= 0
                            ? 9999
                            : p.PostServicePackage.PriorityLevel)
                    .ThenByDescending(p => p.CreatedAt)
                    .ThenByDescending(p => p.PropertyID)
                    .Take(24)
                    .ToListAsync();

                List<Property> featuredRentProperties = await basePropertyQuery
                    .Where(p =>
                        p.PropertyType != null &&
                        (
                            p.PropertyType.ParentID == 2 ||
                            p.TypeID == 2
                        ))
                    .OrderBy(p =>
                        p.PostServicePackage == null || p.PostServicePackage.PriorityLevel <= 0
                            ? 9999
                            : p.PostServicePackage.PriorityLevel)
                    .ThenByDescending(p => p.CreatedAt)
                    .ThenByDescending(p => p.PropertyID)
                    .Take(24)
                    .ToListAsync();

                HashSet<int> addedIds = new();
                List<Property> result = new();

                foreach (Property item in featuredBuyProperties.Take(16))
                {
                    if (addedIds.Add(item.PropertyID))
                    {
                        result.Add(item);
                    }
                }

                foreach (Property item in featuredRentProperties.Take(16))
                {
                    if (addedIds.Add(item.PropertyID))
                    {
                        result.Add(item);
                    }
                }

                foreach (Property item in featuredBuyProperties)
                {
                    if (addedIds.Add(item.PropertyID))
                    {
                        result.Add(item);
                    }
                }

                foreach (Property item in featuredRentProperties)
                {
                    if (addedIds.Add(item.PropertyID))
                    {
                        result.Add(item);
                    }
                }

                return result;
            }) ?? new List<Property>();

            ViewBag.HomeAllPropertyIds = properties
                .Select(p => p.PropertyID)
                .ToList();

            await LoadHomeViewBagsAsync();

            return View(properties);
        }
        // =====================================================
        // HẠ CẤP VIP HẾT HẠN - CHỈ CHẠY TỐI ĐA 1 LẦN / 30 PHÚT
        // Tránh mỗi lần mở trang chủ lại quét bảng Properties.
        // =====================================================
        private async Task DowngradeExpiredVipIfNeededAsync()
        {
            const string cacheKey = "HOME_EXPIRED_VIP_DOWNGRADE_CHECKED";

            if (_cache.TryGetValue(cacheKey, out bool _))
            {
                return;
            }

            _cache.Set(cacheKey, true, TimeSpan.FromMinutes(30));

            int? normalPackageId = await _cache.GetOrCreateAsync("NORMAL_POST_PACKAGE_ID", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);

                int? packageId = await _context.PostServicePackages
                    .AsNoTracking()
                    .Where(p => p.PackageType == "Tin Thường")
                    .OrderBy(p => p.PriorityLevel <= 0 ? 9999 : p.PriorityLevel)
                    .ThenBy(p => p.Price)
                    .Select(p => (int?)p.PackageID)
                    .FirstOrDefaultAsync();

                if (packageId.HasValue)
                {
                    return packageId;
                }

                return await _context.PostServicePackages
                    .AsNoTracking()
                    .Where(p => p.PriorityLevel == 5)
                    .OrderBy(p => p.Price)
                    .Select(p => (int?)p.PackageID)
                    .FirstOrDefaultAsync();
            });

            if (!normalPackageId.HasValue)
            {
                return;
            }

            DateTime now = DateTime.Now;

            await _context.Properties
                .Where(p =>
                    p.VipExpiryDate.HasValue &&
                    p.VipExpiryDate.Value < now &&
                    p.PackageID != normalPackageId.Value)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.PackageID, normalPackageId.Value)
                    .SetProperty(p => p.VipExpiryDate, (DateTime?)null));
        }

        // =====================================================
        // DỮ LIỆU PHỤ TRANG CHỦ - CACHE NGẮN ĐỂ KHÔNG QUERY LẶP
        // =====================================================
        private async Task LoadHomeViewBagsAsync()
        {
            ViewBag.LatestProjects = await _cache.GetOrCreateAsync("HOME_LATEST_PROJECTS", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);

                return await _context.Projects
                    .AsNoTracking()
                    .Include(p => p.Area)
                    .Where(p => p.ApprovalStatus == "Approved" && p.IsDeleted == false)
                    .OrderByDescending(p => p.PublishedAt)
                    .Take(12)
                    .ToListAsync();
            });

            ViewBag.Banners = await _cache.GetOrCreateAsync("HOME_ACTIVE_BANNERS", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);

                return await _context.Banners
                    .AsNoTracking()
                    .Where(b => b.IsActive)
                    .OrderBy(b => b.DisplayOrder)
                    .ToListAsync();
            });

            ViewBag.Areas = await _cache.GetOrCreateAsync("HOME_AREAS", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);

                return await _context.Areas
                    .AsNoTracking()
                    .OrderBy(a => a.AreaName)
                    .ToListAsync();
            });

            ViewBag.AreaPropertyCounts = await _cache.GetOrCreateAsync("HOME_AREA_PROPERTY_COUNTS", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);

                var rows = await _context.Properties
                    .AsNoTracking()
                    .Where(p => p.Status == "Approved" && p.IsDeleted == false)
                    .GroupBy(p => p.Ward.AreaID)
                    .Select(g => new { AreaID = g.Key, Count = g.Count() })
                    .ToListAsync();

                return rows.ToDictionary(x => x.AreaID, x => x.Count);
            });

            ViewBag.Types = await _cache.GetOrCreateAsync("HOME_PROPERTY_TYPES", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);

                return await _context.PropertyTypes
                    .AsNoTracking()
                    .Select(t => new { t.TypeID, t.TypeName, t.ParentID })
                    .ToListAsync();
            });

            ViewBag.HotNews = await _cache.GetOrCreateAsync("HOME_HOT_NEWS", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);

                return await _context.Blogs
                    .AsNoTracking()
                    .Where(b => b.IsDeleted == false)
                    .OrderByDescending(b => b.Views)
                    .Take(8)
                    .ToListAsync();
            });

            ViewBag.LatestNews = await _cache.GetOrCreateAsync("HOME_LATEST_NEWS", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);

                return await _context.Blogs
                    .AsNoTracking()
                    .Where(b => b.IsDeleted == false)
                    .OrderByDescending(b => b.CreatedAt)
                    .Take(5)
                    .ToListAsync();
            });
        }

        // =====================================================
        // API xử lý đề xuất tìm kiếm thông minh (AUTO-SUGGEST)
        // =====================================================
        [HttpGet]
        public async Task<IActionResult> Suggest(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return Json(new List<object>());
            }

            keyword = keyword.Trim();

            List<object> suggestions = await _context.Properties
                .AsNoTracking()
                .Where(p =>
                    p.Status == "Approved" &&
                    p.IsDeleted == false &&
                    (
                        EF.Functions.Like(p.Title, $"%{keyword}%") ||
                        EF.Functions.Like(p.AddressDetail ?? "", $"%{keyword}%") ||
                        (p.Project != null && EF.Functions.Like(p.Project.ProjectName, $"%{keyword}%"))
                    ))
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new
                {
                    title = p.Title,
                    address =
                        (p.AddressDetail ?? "") + ", " +
                        (p.Ward != null ? p.Ward.WardName : "") + ", " +
                        (p.Ward != null && p.Ward.Area != null ? p.Ward.Area.AreaName : ""),
                    type = p.Project != null ? "Dự án: " + p.Project.ProjectName : "Tin BĐS"
                })
                .Take(7)
                .Cast<object>()
                .ToListAsync();

            return Json(suggestions);
        }

        // =====================================================
        // API xử lý nút Lưu tin (Trái tim) bằng AJAX
        // =====================================================
        [HttpPost]
        public async Task<IActionResult> ToggleFavorite(int propertyId)
        {
            string? userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (!int.TryParse(userIdStr, out int userId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập để lưu tin." });
            }

            Favorite? existingFav = await _context.Favorites
                .FirstOrDefaultAsync(f => f.PropertyID == propertyId && f.UserID == userId);

            bool isSaved = false;

            if (existingFav != null)
            {
                _context.Favorites.Remove(existingFav);
            }
            else
            {
                _context.Favorites.Add(new Favorite
                {
                    PropertyID = propertyId,
                    UserID = userId,
                    CreatedAt = DateTime.Now
                });

                isSaved = true;
            }

            await _context.SaveChangesAsync();

            return Json(new { success = true, isSaved });
        }

        [HttpGet]
        public async Task<JsonResult> GetWardsByArea(int areaId)
        {
            var wards = await _context.Wards
                .AsNoTracking()
                .Where(w => w.AreaID == areaId)
                .OrderBy(w => w.WardName)
                .Select(w => new
                {
                    wardId = w.WardID,
                    wardName = w.WardName
                })
                .ToListAsync();

            return Json(wards);
        }
    }
}
