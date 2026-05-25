using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class PropertyController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _hostEnvironment;
        private readonly IAuditLogService _auditLogService;
        public PropertyController(ApplicationDbContext context, IWebHostEnvironment hostEnvironment, IAuditLogService auditLogService)
        {
            _context = context;
            _hostEnvironment = hostEnvironment;
            _auditLogService = auditLogService;
        }


        // =====================================================
        // KHÓA TIN ĐÃ BÁN / ĐÃ CHO THUÊ
        // Không cần SQL mới, không cần Model mới.
        // Chỉ dùng lại Property.Status = Sold / Rented.
        // =====================================================
        private const int SystemGiftNormalQuantity = 5;
        private const int SystemGiftValidDays = 30;
        private const int NormalPropertyVisibleDays = 30;

        private static bool IsLockedProperty(Property? property)
        {
            return property != null &&
                   (property.Status == "Sold" || property.Status == "Rented" || property.Status == "Expired");
        }

        private static string GetLockedPropertyMessage(Property property)
        {
            return property.Status switch
            {
                "Sold" => "Tin đăng này đã bán nên hệ thống đã khóa toàn bộ thao tác.",
                "Rented" => "Tin đăng này đã cho thuê nên hệ thống đã khóa toàn bộ thao tác.",
                "Expired" => "Tin đăng này đã hết hạn nên hệ thống đã khóa thao tác. Vui lòng đăng tin mới hoặc dùng gói mới.",
                _ => "Tin đăng này đang bị khóa thao tác."
            };
        }

        private static string GetPropertyStatusText(string? status)
        {
            return status switch
            {
                "Approved" => "Đã duyệt",
                "Pending" => "Chờ duyệt",
                "Rejected" => "Bị từ chối",
                "Sold" => "Đã bán",
                "Rented" => "Đã cho thuê",
                "Expired" => "Tin hết hạn",
                "Draft" => "Bản nháp",
                null or "" => "Chưa xác định",
                _ => status
            };
        }

        private static bool IsDiamondPackage(PostServicePackage? package)
        {
            return package != null
                && !string.IsNullOrWhiteSpace(package.PackageType)
                && package.PackageType.Contains("Kim Cương", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNormalPackage(PostServicePackage? package)
        {
            return package != null
                && !string.IsNullOrWhiteSpace(package.PackageType)
                && package.PackageType.Contains("Tin Thường", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSystemGiftNormalTransaction(Transaction t)
        {
            return t.PaymentMethod == "System Gift" &&
                   t.Type == "Tặng lượt đăng tin thường";
        }

        private static DateTime GetCreditExpiredAt(Transaction transaction)
        {
            if (IsSystemGiftNormalTransaction(transaction))
            {
                return transaction.CreatedAt.AddDays(SystemGiftValidDays);
            }

            return DateTime.MaxValue;
        }

        private static bool IsCreditStillValid(Transaction transaction)
        {
            return GetCreditExpiredAt(transaction) >= DateTime.Now;
        }

        private async Task NormalizePackageAndExpiredPropertiesAsync(int? onlyUserId = null)
        {
            await ExpireSystemGiftCreditsAsync(onlyUserId);

            // Bước 1: VIP hết hạn thì hạ xuống Tin Thường trước.
            await DowngradeExpiredVipPropertiesAsync(onlyUserId);

            // Bước 2: Sau khi đã hạ VIP xong, tin Tin Thường nào hết 30 ngày thì khóa Expired.
            await ExpireNormalPropertiesAsync(onlyUserId);
        }

        private async Task ExpireSystemGiftCreditsAsync(int? onlyUserId = null)
        {
            DateTime giftCutoff = DateTime.Now.AddDays(-SystemGiftValidDays);

            var query = _context.Transactions
                .Where(t =>
                    t.PropertyID == null &&
                    t.Status == "Success" &&
                    t.Quantity > 0 &&
                    t.PaymentMethod == "System Gift" &&
                    t.Type == "Tặng lượt đăng tin thường" &&
                    t.CreatedAt < giftCutoff);

            if (onlyUserId.HasValue)
            {
                query = query.Where(t => t.UserID == onlyUserId.Value);
            }

            var expiredCredits = await query.ToListAsync();

            foreach (var credit in expiredCredits)
            {
                credit.Quantity = 0;
                credit.Status = "Expired";
                credit.Description = string.IsNullOrWhiteSpace(credit.Description)
                    ? "Lượt Tin Thường được hệ thống tặng đã hết hạn sau 30 ngày."
                    : credit.Description + Environment.NewLine + "[HẾT HẠN] Lượt Tin Thường được hệ thống tặng đã hết hạn sau 30 ngày.";
            }

            if (expiredCredits.Any())
            {
                await _context.SaveChangesAsync();
            }
        }

        private async Task ExpireNormalPropertiesAsync(int? onlyUserId = null)
        {
            DateTime now = DateTime.Now;

            var query = _context.Properties
                .Include(p => p.PostServicePackage)
                .Where(p =>
                    p.IsDeleted == false &&
                    p.Status == "Approved" &&
                    p.PostServicePackage != null &&
                    p.PostServicePackage.PackageType != null &&
                    p.PostServicePackage.PackageType.Contains("Tin Thường") &&
                    (
                        (p.VipExpiryDate.HasValue && p.VipExpiryDate.Value < now) ||
                        (!p.VipExpiryDate.HasValue && p.ApprovedAt.HasValue && p.ApprovedAt.Value.AddDays(NormalPropertyVisibleDays) < now) ||
                        (!p.VipExpiryDate.HasValue && !p.ApprovedAt.HasValue && p.CreatedAt.AddDays(NormalPropertyVisibleDays) < now)
                    ));

            if (onlyUserId.HasValue)
            {
                query = query.Where(p => p.UserID == onlyUserId.Value);
            }

            var expiredNormalProperties = await query.ToListAsync();

            foreach (var prop in expiredNormalProperties)
            {
                DateTime startDate = prop.ApprovedAt ?? prop.CreatedAt;
                DateTime expiredAt = prop.VipExpiryDate ?? startDate.AddDays(NormalPropertyVisibleDays);

                prop.Status = "Expired";
                prop.UpdatedAt = now;
                prop.RejectionReason =
                    $"Tin hết hạn. Tin dùng gói Tin Thường chỉ hiển thị tối đa {NormalPropertyVisibleDays} ngày, hết hạn lúc {expiredAt:dd/MM/yyyy HH:mm}.";

                string note = $"[TIN HẾT HẠN - {now:dd/MM/yyyy HH:mm}] Tin dùng gói Tin Thường đã hết hạn hiển thị. Hệ thống khóa sửa, xóa, đánh dấu giao dịch và các thao tác tương tác khác.";

                if (string.IsNullOrWhiteSpace(prop.Description))
                {
                    prop.Description = note;
                }
                else if (!prop.Description.Contains("[TIN HẾT HẠN"))
                {
                    prop.Description = note + Environment.NewLine + Environment.NewLine + prop.Description;
                }
            }

            if (expiredNormalProperties.Any())
            {
                await _context.SaveChangesAsync();
            }
        }
        private async Task DowngradeExpiredVipPropertiesAsync(int? onlyUserId = null)
        {
            DateTime now = DateTime.Now;

            var normalPackage = await _context.PostServicePackages
                .OrderByDescending(p => p.IsActive)
                .ThenBy(p => p.PriorityLevel)
                .FirstOrDefaultAsync(p =>
                    p.PackageType != null &&
                    p.PackageType.Contains("Tin Thường"));

            if (normalPackage == null)
            {
                return;
            }

            var query = _context.Properties
                .Include(p => p.PostServicePackage)
                .Where(p =>
                    p.IsDeleted == false &&
                    p.Status == "Approved" &&
                    p.VipExpiryDate.HasValue &&
                    p.VipExpiryDate.Value < now &&
                    p.PostServicePackage != null &&
                    p.PostServicePackage.PackageType != null &&
                    !p.PostServicePackage.PackageType.Contains("Tin Thường"));

            if (onlyUserId.HasValue)
            {
                query = query.Where(p => p.UserID == onlyUserId.Value);
            }

            var expiredVipProperties = await query.ToListAsync();

            foreach (var prop in expiredVipProperties)
            {
                DateTime vipExpiredAt = prop.VipExpiryDate!.Value;
                DateTime normalExpiredAt = vipExpiredAt.AddDays(NormalPropertyVisibleDays);
                string oldPackageName = prop.PostServicePackage?.PackageName ?? "Gói VIP";

                prop.PackageID = normalPackage.PackageID;
                prop.UpdatedAt = now;
                prop.IsAutoApproved = false;

                if (normalExpiredAt >= now)
                {
                    prop.VipExpiryDate = normalExpiredAt;
                    prop.Status = "Approved";

                    string note =
                        $"[HẠ VIP - {now:dd/MM/yyyy HH:mm}] Tin dùng gói '{oldPackageName}' đã hết hạn lúc {vipExpiredAt:dd/MM/yyyy HH:mm}. " +
                        $"Hệ thống tự chuyển về Tin Thường và cho hiển thị tiếp tối đa {NormalPropertyVisibleDays} ngày, đến {normalExpiredAt:dd/MM/yyyy HH:mm}.";

                    if (string.IsNullOrWhiteSpace(prop.Description))
                    {
                        prop.Description = note;
                    }
                    else if (!prop.Description.Contains("[HẠ VIP"))
                    {
                        prop.Description = note + Environment.NewLine + Environment.NewLine + prop.Description;
                    }
                }
                else
                {
                    prop.Status = "Expired";
                    prop.VipExpiryDate = normalExpiredAt;
                    prop.RejectionReason =
                        $"Tin hết hạn. Gói VIP '{oldPackageName}' đã hết hạn lúc {vipExpiredAt:dd/MM/yyyy HH:mm}. " +
                        $"Sau đó hệ thống hạ về Tin Thường trong {NormalPropertyVisibleDays} ngày và đã hết hạn lúc {normalExpiredAt:dd/MM/yyyy HH:mm}.";

                    string note =
                        $"[TIN HẾT HẠN - {now:dd/MM/yyyy HH:mm}] Gói VIP '{oldPackageName}' đã hết hạn, " +
                        $"tin đã được hạ xuống Tin Thường {NormalPropertyVisibleDays} ngày và hiện đã hết hạn hiển thị. Hệ thống khóa toàn bộ thao tác.";

                    if (string.IsNullOrWhiteSpace(prop.Description))
                    {
                        prop.Description = note;
                    }
                    else if (!prop.Description.Contains("[TIN HẾT HẠN"))
                    {
                        prop.Description = note + Environment.NewLine + Environment.NewLine + prop.Description;
                    }
                }
            }

            if (expiredVipProperties.Any())
            {
                await _context.SaveChangesAsync();
            }
        }
        [AllowAnonymous]
        public IActionResult Index()
        {
            return RedirectToAction("Search");
        }

        // ==========================================
        // API TÌM KIẾM ĐỀ XUẤT THÔNG MINH (AUTO-SUGGEST)
        // ==========================================
        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> Suggest(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return Json(new List<string>());

            var suggestions = await _context.Properties
                .AsNoTracking()
                .Where(p => p.Status == "Approved" && p.IsDeleted == false && p.Title.Contains(keyword))
                .Select(p => p.Title)
                .Distinct()
                .Take(5)
                .ToListAsync();

            return Json(suggestions);
        }

        [AllowAnonymous]
        [Route("Property/Search")]
        public async Task<IActionResult> Search(
           string? transactionType = null,
           string? keyword = null,
           int? typeId = null,
           int? areaId = null,
           int? wardId = null,
           decimal? minPrice = null,
           decimal? maxPrice = null,
           string? priceRange = null,
           decimal? minSize = null,
           decimal? maxSize = null,
           string? bedrooms = null,
           string? bathrooms = null,
           string? direction = null,
           string? legalStatus = null,
           string[]? amenities = null,
           int? packageId = null,
           string? sortOrder = null,
           int page = 1)
        {
            int pageSize = 12;
            page = Math.Max(1, page);

            transactionType = string.Equals(transactionType, "rent", StringComparison.OrdinalIgnoreCase)
                ? "rent"
                : "buy";

            keyword = keyword?.Trim();

            await NormalizePackageAndExpiredPropertiesAsync();

            // =====================================================
            // Gói hết hạn đã được chuẩn hóa bởi NormalizePackageAndExpiredPropertiesAsync()
            // - VIP hết hạn: hạ về Tin Thường và cho thêm tối đa 30 ngày hiển thị thường.
            // - Tin Thường hết hạn: chuyển Expired, không hiển thị công khai.
            // =====================================================

            // =====================================================
            // LẤY DANH SÁCH LOẠI BĐS
            // Root: ParentID == null
            // Con: ParentID != null
            // Không hard-code TypeID = 1 / 2 nữa
            // =====================================================
            var allTypes = await _context.PropertyTypes
                .AsNoTracking()
                .Select(t => new
                {
                    t.TypeID,
                    t.TypeName,
                    t.ParentID
                })
                .ToListAsync();

            var rootTypes = allTypes
                .Where(t => t.ParentID == null)
                .ToList();

            var buyRoot = rootTypes.FirstOrDefault(t =>
                !string.IsNullOrWhiteSpace(t.TypeName) &&
                (
                    t.TypeName.Contains("bán", StringComparison.OrdinalIgnoreCase) ||
                    t.TypeName.Contains("mua", StringComparison.OrdinalIgnoreCase) ||
                    t.TypeName.Contains("mua bán", StringComparison.OrdinalIgnoreCase) ||
                    t.TypeName.Contains("nhà đất bán", StringComparison.OrdinalIgnoreCase)
                ));

            var rentRoot = rootTypes.FirstOrDefault(t =>
                !string.IsNullOrWhiteSpace(t.TypeName) &&
                (
                    t.TypeName.Contains("thuê", StringComparison.OrdinalIgnoreCase) ||
                    t.TypeName.Contains("cho thuê", StringComparison.OrdinalIgnoreCase) ||
                    t.TypeName.Contains("nhà đất cho thuê", StringComparison.OrdinalIgnoreCase)
                ));

            // Fallback an toàn nếu dữ liệu cũ đang dùng TypeID 1 / 2
            int buyRootId = buyRoot?.TypeID ?? 1;
            int rentRootId = rentRoot?.TypeID ?? 2;

            int selectedRootId = transactionType == "rent" ? rentRootId : buyRootId;

            var selectedChildTypeIds = allTypes
                .Where(t => t.ParentID == selectedRootId)
                .Select(t => t.TypeID)
                .ToList();

            // Nếu có tin đang lưu thẳng TypeID = root thì vẫn cho hiển thị.
            selectedChildTypeIds.Add(selectedRootId);

            // Nếu typeId từ URL cũ không thuộc tab hiện tại thì bỏ đi.
            // Ví dụ đang ở tab Cho thuê mà typeId lại là loại con của Mua bán.
            if (typeId.HasValue && !selectedChildTypeIds.Contains(typeId.Value))
            {
                typeId = null;
            }

            // =====================================================
            // XỬ LÝ PRICE RANGE NẾU CÓ
            // minPrice/maxPrice trên giao diện tính theo TRIỆU
            // Database lưu theo VNĐ
            // =====================================================
            if (!string.IsNullOrWhiteSpace(priceRange) && !minPrice.HasValue && !maxPrice.HasValue)
            {
                var parts = priceRange.Split('-', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 2)
                {
                    if (decimal.TryParse(parts[0], out decimal pMin))
                    {
                        minPrice = pMin;
                    }

                    if (decimal.TryParse(parts[1], out decimal pMax))
                    {
                        maxPrice = pMax;
                    }
                }
            }

            // =====================================================
            // QUERY CHÍNH
            // Chỉ lấy tin Approved, chưa xóa, chưa bán, chưa thuê
            // Và bắt buộc thuộc đúng nhóm Mua bán / Cho thuê
            // =====================================================
            var query = _context.Properties
                .AsNoTracking()
                .Include(p => p.PropertyType)
                .Include(p => p.Ward)
                    .ThenInclude(w => w.Area)
                .Include(p => p.PostServicePackage)
                .Where(p => p.Status == "Approved"
                         && p.IsDeleted == false
                         && p.PropertyType != null
                         && selectedChildTypeIds.Contains(p.TypeID))
                .AsQueryable();

            if (typeId.HasValue)
            {
                query = query.Where(p => p.TypeID == typeId.Value);
            }

            if (areaId.HasValue)
            {
                query = query.Where(p => p.Ward != null && p.Ward.AreaID == areaId.Value);
            }

            if (wardId.HasValue)
            {
                query = query.Where(p => p.WardID == wardId.Value);
            }

            if (packageId.HasValue)
            {
                query = query.Where(p => p.PackageID == packageId.Value);
            }

            if (minPrice.HasValue && minPrice.Value > 0)
            {
                query = query.Where(p => p.Price.HasValue && p.Price.Value >= minPrice.Value * 1000000m);
            }

            if (maxPrice.HasValue && maxPrice.Value > 0)
            {
                query = query.Where(p => p.Price.HasValue && p.Price.Value <= maxPrice.Value * 1000000m);
            }

            if (minSize.HasValue && minSize.Value > 0)
            {
                query = query.Where(p => p.AreaSize.HasValue && p.AreaSize.Value >= minSize.Value);
            }

            if (maxSize.HasValue && maxSize.Value > 0)
            {
                query = query.Where(p => p.AreaSize.HasValue && p.AreaSize.Value <= maxSize.Value);
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                string cleanKeyword = keyword.Trim();

                query = query.Where(p =>
                    p.Title.Contains(cleanKeyword) ||
                    (p.AddressDetail != null && p.AddressDetail.Contains(cleanKeyword)) ||
                    (p.Description != null && p.Description.Contains(cleanKeyword)) ||
                    (p.Project != null && p.Project.ProjectName.Contains(cleanKeyword)));
            }

            if (!string.IsNullOrWhiteSpace(direction))
            {
                query = query.Where(p => _context.PropertyFeatures.Any(f =>
                    f.PropertyID == p.PropertyID &&
                    f.FeatureName == "Hướng nhà" &&
                    f.FeatureValue == direction));
            }

            if (!string.IsNullOrWhiteSpace(legalStatus))
            {
                query = query.Where(p => _context.PropertyFeatures.Any(f =>
                    f.PropertyID == p.PropertyID &&
                    f.FeatureName == "Pháp lý" &&
                    f.FeatureValue == legalStatus));
            }

            if (!string.IsNullOrWhiteSpace(bedrooms))
            {
                if (bedrooms == "5")
                {
                    var highBeds = new[] { "5", "6", "7", "8", "9", "10", "10+", "5+" };

                    query = query.Where(p => _context.PropertyFeatures.Any(f =>
                        f.PropertyID == p.PropertyID &&
                        f.FeatureName == "Phòng ngủ" &&
                        highBeds.Contains(f.FeatureValue)));
                }
                else
                {
                    query = query.Where(p => _context.PropertyFeatures.Any(f =>
                        f.PropertyID == p.PropertyID &&
                        f.FeatureName == "Phòng ngủ" &&
                        f.FeatureValue == bedrooms));
                }
            }

            if (!string.IsNullOrWhiteSpace(bathrooms))
            {
                if (bathrooms == "4")
                {
                    var highBaths = new[] { "4", "5", "6", "7", "8", "9", "10", "10+", "4+" };

                    query = query.Where(p => _context.PropertyFeatures.Any(f =>
                        f.PropertyID == p.PropertyID &&
                        f.FeatureName == "Phòng vệ sinh" &&
                        highBaths.Contains(f.FeatureValue)));
                }
                else
                {
                    query = query.Where(p => _context.PropertyFeatures.Any(f =>
                        f.PropertyID == p.PropertyID &&
                        f.FeatureName == "Phòng vệ sinh" &&
                        f.FeatureValue == bathrooms));
                }
            }

            if (amenities != null && amenities.Any())
            {
                foreach (var amenity in amenities.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    string cleanAmenity = amenity.Trim();

                    query = query.Where(p => _context.PropertyFeatures.Any(f =>
                        f.PropertyID == p.PropertyID &&
                        f.FeatureName == "Tiện ích" &&
                        f.FeatureValue.Contains(cleanAmenity)));
                }
            }

            // =====================================================
            // SẮP XẾP
            // VIP luôn ưu tiên trước, sau đó mới theo điều kiện người dùng chọn
            // =====================================================
            query = sortOrder switch
            {
                "price_asc" => query
                    .OrderBy(p => p.PostServicePackage != null && p.PostServicePackage.PriorityLevel > 0
                        ? p.PostServicePackage.PriorityLevel
                        : 9999)
                    .ThenBy(p => p.Price ?? decimal.MaxValue)
                    .ThenByDescending(p => p.CreatedAt),

                "price_desc" => query
                    .OrderBy(p => p.PostServicePackage != null && p.PostServicePackage.PriorityLevel > 0
                        ? p.PostServicePackage.PriorityLevel
                        : 9999)
                    .ThenByDescending(p => p.Price ?? 0)
                    .ThenByDescending(p => p.CreatedAt),

                "area_asc" => query
                    .OrderBy(p => p.PostServicePackage != null && p.PostServicePackage.PriorityLevel > 0
                        ? p.PostServicePackage.PriorityLevel
                        : 9999)
                    .ThenBy(p => p.AreaSize ?? decimal.MaxValue)
                    .ThenByDescending(p => p.CreatedAt),

                "area_desc" => query
                    .OrderBy(p => p.PostServicePackage != null && p.PostServicePackage.PriorityLevel > 0
                        ? p.PostServicePackage.PriorityLevel
                        : 9999)
                    .ThenByDescending(p => p.AreaSize ?? 0)
                    .ThenByDescending(p => p.CreatedAt),

                _ => query
                    .OrderBy(p => p.PostServicePackage != null && p.PostServicePackage.PriorityLevel > 0
                        ? p.PostServicePackage.PriorityLevel
                        : 9999)
                    .ThenByDescending(p => p.CreatedAt)
            };

            int totalItems = await query.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));

            if (page > totalPages)
            {
                page = totalPages;
            }

            var results = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // =====================================================
            // VIEWBAG
            // =====================================================
            ViewBag.LatestProjects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Area)
                .Where(p => p.ApprovalStatus == "Approved" && p.IsDeleted == false)
                .OrderByDescending(p => p.PublishedAt)
                .Take(4)
                .ToListAsync();

            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            List<int> favoritedIds = new List<int>();

            if (int.TryParse(userIdClaim, out int userIdToken))
            {
                favoritedIds = await _context.Favorites
                    .AsNoTracking()
                    .Where(f => f.UserID == userIdToken)
                    .Select(f => f.PropertyID)
                    .ToListAsync();
            }

            ViewBag.FavoritedIds = favoritedIds;

            var subTypes = allTypes
                .Where(t => t.ParentID != null)
                .Select(t => new
                {
                    t.TypeID,
                    t.TypeName,
                    t.ParentID
                })
                .ToList();

            ViewBag.SubTypesJson = System.Text.Json.JsonSerializer.Serialize(subTypes);
            ViewBag.BuyRootId = buyRootId;
            ViewBag.RentRootId = rentRootId;
            ViewBag.Areas = await _context.Areas
                .AsNoTracking()
                .OrderBy(a => a.AreaName)
                .ToListAsync();

            ViewBag.CurrentFilters = new
            {
                transactionType,
                keyword,
                typeId,
                areaId,
                wardId,
                minPrice,
                maxPrice,
                minSize,
                maxSize,
                bedrooms,
                bathrooms,
                direction,
                legalStatus,
                amenities,
                packageId,
                sortOrder
            };

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalItems = totalItems;

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return PartialView("_PropertyGridPartial", results);
            }

            return View("Search", results);
        }

        [AllowAnonymous]
        [Route("BatDongSan/NhaDatBan")]
        public async Task<IActionResult> NhaDatBan()
        {
            return await Search(transactionType: "buy", page: 1);
        }

        [AllowAnonymous]
        [Route("BatDongSan/NhaDatChoThue")]
        public async Task<IActionResult> NhaDatChoThue()
        {
            return await Search(transactionType: "rent", page: 1);
        }

        [HttpGet]
        public async Task<IActionResult> MyAds()
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdClaim, out int userId))
            {
                await HttpContext.SignOutAsync();
                return RedirectToAction("Login", "Account");
            }

            await NormalizePackageAndExpiredPropertiesAsync(userId);

            var myProperties = await _context.Properties
                .Include(p => p.PropertyType)
                .Include(p => p.PostServicePackage)
                .Where(p => p.UserID == userId && p.IsDeleted == false)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            return View(myProperties);
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAsTransacted(int id, string transactionStatus)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdClaim, out int userId))
            {
                return Unauthorized();
            }

            if (transactionStatus != "Sold" && transactionStatus != "Rented")
            {
                TempData["Error"] = "Trạng thái giao dịch không hợp lệ.";
                return RedirectToAction("MyAds");
            }

            var property = await _context.Properties
                .FirstOrDefaultAsync(p => p.PropertyID == id && p.UserID == userId && p.IsDeleted == false);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy thông tin bất động sản hoặc bạn không có quyền thao tác.";
                return RedirectToAction("MyAds");
            }

            if (IsLockedProperty(property))
            {
                TempData["Error"] = GetLockedPropertyMessage(property);
                return RedirectToAction("MyAds");
            }

            var oldPriceText = property.Price.HasValue && property.Price.Value > 0
                ? property.Price.Value.ToString("N0", new System.Globalization.CultureInfo("vi-VN")) + " đ"
                : "Thỏa thuận";

            property.Status = transactionStatus;
            property.SoldAt = DateTime.Now;
            property.UpdatedAt = DateTime.Now;

            string doneText = transactionStatus == "Sold" ? "ĐÃ BÁN" : "ĐÃ CHO THUÊ";
            string historyLine = $"[{doneText} - Giá ghi nhận: {oldPriceText} - Thời gian: {DateTime.Now:dd/MM/yyyy HH:mm}]";

            if (string.IsNullOrWhiteSpace(property.Description))
            {
                property.Description = historyLine;
            }
            else if (!property.Description.StartsWith("[ĐÃ BÁN") && !property.Description.StartsWith("[ĐÃ CHO THUÊ"))
            {
                property.Description = historyLine + Environment.NewLine + Environment.NewLine + property.Description;
            }

            await _context.SaveChangesAsync();

            string msg = transactionStatus == "Sold" ? "đã bán" : "đã cho thuê";
            TempData["Success"] = $"Đã ghi nhận bất động sản {msg} thành công. Tin đã được khóa toàn bộ thao tác.";

            return RedirectToAction("MyAds");
        }
        private async Task<int> GetRemainingPackageCountAsync(int userId, int packageId)
        {
            await ExpireSystemGiftCreditsAsync(userId);

            return await _context.Transactions
                .Where(t =>
                    t.UserID == userId &&
                    t.PackageID == packageId &&
                    t.PropertyID == null &&
                    t.Status == "Success" &&
                    t.Quantity > 0)
                .Where(t =>
                    !(t.PaymentMethod == "System Gift" && t.Type == "Tặng lượt đăng tin thường") ||
                    t.CreatedAt.AddDays(SystemGiftValidDays) >= DateTime.Now)
                .SumAsync(t => (int?)t.Quantity) ?? 0;
        }

        private async Task<bool> ConsumeOnePackageCreditAsync(int userId, int packageId, int propertyId)
        {
            await ExpireSystemGiftCreditsAsync(userId);

            var credit = await _context.Transactions
                .Where(t =>
                    t.UserID == userId &&
                    t.PackageID == packageId &&
                    t.PropertyID == null &&
                    t.Status == "Success" &&
                    t.Quantity > 0)
                .Where(t =>
                    !(t.PaymentMethod == "System Gift" && t.Type == "Tặng lượt đăng tin thường") ||
                    t.CreatedAt.AddDays(SystemGiftValidDays) >= DateTime.Now)
                .OrderBy(t => t.PaymentMethod == "System Gift" ? 0 : 1)
                .ThenBy(t => t.CreatedAt)
                .FirstOrDefaultAsync();

            if (credit == null)
            {
                return false;
            }

            if (credit.Quantity <= 1)
            {
                credit.Quantity = 1;
                credit.PropertyID = propertyId;
                _context.Update(credit);
            }
            else
            {
                credit.Quantity -= 1;
                _context.Update(credit);

                _context.Transactions.Add(new Transaction
                {
                    UserID = userId,
                    PackageID = packageId,
                    PropertyID = propertyId,
                    Quantity = 1,
                    Amount = 0,
                    Type = "Sử dụng lượt đăng",
                    PaymentMethod = credit.PaymentMethod == "System Gift" ? "System Gift Used" : "Wallet",
                    TransactionCode = "USE" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + "_" + userId + "_" + propertyId,
                    Status = "Success",
                    Description = credit.PaymentMethod == "System Gift"
                        ? "Sử dụng 1 lượt Tin Thường được hệ thống tặng. Lượt tặng có hạn 30 ngày kể từ ngày tặng."
                        : "Sử dụng 1 lượt đăng từ ví gói tin",
                    CreatedAt = DateTime.Now
                });
            }

            return true;
        }

        [HttpGet]
        public async Task<IActionResult> GetAvailablePackages()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId))
            {
                return Unauthorized();
            }

            await NormalizePackageAndExpiredPropertiesAsync(userId);

            bool hasReceivedGift = await _context.Transactions.AnyAsync(t =>
                t.UserID == userId &&
                t.PaymentMethod == "System Gift" &&
                t.Type == "Tặng lượt đăng tin thường");

            if (!hasReceivedGift)
            {
                var normalPackage = await _context.PostServicePackages
                    .FirstOrDefaultAsync(p => p.PackageType == "Tin Thường" && p.IsActive);

                if (normalPackage != null)
                {
                    _context.Transactions.Add(new Transaction
                    {
                        UserID = userId,
                        PackageID = normalPackage.PackageID,
                        PropertyID = null,
                        Quantity = SystemGiftNormalQuantity,
                        Amount = 0,
                        Type = "Tặng lượt đăng tin thường",
                        PaymentMethod = "System Gift",
                        TransactionCode = "WELCOME" + DateTime.Now.ToString("yyyyMMddHHmmss") + userId,
                        Status = "Success",
                        Description = $"Hệ thống tặng {SystemGiftNormalQuantity} lượt Tin Thường. Lượt tặng có hiệu lực tối đa {SystemGiftValidDays} ngày kể từ ngày tặng.",
                        CreatedAt = DateTime.Now
                    });

                    await _context.SaveChangesAsync();
                }
            }

            DateTime giftCutoff = DateTime.Now.AddDays(-SystemGiftValidDays);

            var availableCredits = await _context.Transactions
                .Where(t =>
                    t.UserID == userId &&
                    t.PropertyID == null &&
                    t.Status == "Success" &&
                    t.PackageID != null &&
                    t.Quantity > 0)
                .Where(t =>
                    !(t.PaymentMethod == "System Gift" && t.Type == "Tặng lượt đăng tin thường") ||
                    t.CreatedAt >= giftCutoff)
                .GroupBy(t => t.PackageID)
                .Select(g => new
                {
                    PackageID = g.Key,
                    Count = g.Sum(x => x.Quantity),
                    GiftExpiredAt = g
                        .Where(x => x.PaymentMethod == "System Gift" && x.Type == "Tặng lượt đăng tin thường")
                        .Select(x => (DateTime?)x.CreatedAt.AddDays(SystemGiftValidDays))
                        .OrderBy(x => x)
                        .FirstOrDefault()
                })
                .Where(x => x.Count > 0)
                .ToListAsync();

            var packageIds = availableCredits
                .Where(a => a.PackageID.HasValue)
                .Select(a => a.PackageID!.Value)
                .ToList();

            var packages = await _context.PostServicePackages
                .Where(p => packageIds.Contains(p.PackageID) && p.IsActive)
                .ToListAsync();

            var resultData = availableCredits
                .Where(a => a.PackageID.HasValue)
                .Select(a =>
                {
                    var p = packages.FirstOrDefault(pkg => pkg.PackageID == a.PackageID!.Value);

                    if (p == null)
                    {
                        return null;
                    }

                    return new
                    {
                        id = p.PackageID,
                        name = p.PackageName,
                        type = p.PackageType,
                        priority = p.PriorityLevel,
                        price = p.Price,
                        durationDays = p.DurationDays,
                        availableCount = a.Count,
                        giftExpiredAt = a.GiftExpiredAt?.ToString("dd/MM/yyyy"),
                        note = p.PackageType == "Tin Thường"
                            ? $"Tin Thường hiển thị tối đa {NormalPropertyVisibleDays} ngày sau khi được duyệt."
                            : ""
                    };
                })
                .Where(x => x != null)
                .OrderBy(x => x!.priority)
                .ToList();

            return Json(new
            {
                success = true,
                data = resultData
            });
        }

        [HttpGet]
        public async Task<IActionResult> Create()
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdClaim, out int userId)) return RedirectToAction("Login", "Account");

            await NormalizePackageAndExpiredPropertiesAsync(userId);

            var currentUser = await _context.Users.FindAsync(userId);
            if (currentUser == null || string.IsNullOrWhiteSpace(currentUser.Phone))
            {
                TempData["Error"] = "Bạn phải cập nhật Số Điện Thoại trong tài khoản trước khi đăng tin để khách hàng có thể liên hệ!";
                return RedirectToAction("Profile", "Account");
            }

            var isBusiness = await _context.BusinessProfiles
                .AnyAsync(b => b.UserID == userId && b.VerificationStatus == "Approved");

            if (isBusiness)
            {
                TempData["Error"] = "Tài khoản Doanh nghiệp chỉ được phép đăng Dự án, không được đăng tin lẻ.";
                return RedirectToAction("Index", "Dashboard");
            }

            ViewBag.ParentTypes = await _context.PropertyTypes.Where(t => t.ParentID == null).ToListAsync();
            var subTypes = await _context.PropertyTypes.Where(t => t.ParentID != null).Select(t => new { t.TypeID, t.TypeName, t.ParentID }).ToListAsync();
            ViewBag.SubTypesJson = System.Text.Json.JsonSerializer.Serialize(subTypes);
            ViewBag.Areas = new SelectList(await _context.Areas.OrderBy(a => a.AreaName).ToListAsync(), "AreaID", "AreaName");
            ViewBag.MasterFeatures = await _context.PropertyFeatures.Where(f => f.PropertyID == null).ToListAsync();

            return View(new Property());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Property prop, IFormFile MainImageFile, List<IFormFile> AdditionalImages)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return RedirectToAction("Login", "Account");

            await NormalizePackageAndExpiredPropertiesAsync(userId);

            var currentUser = await _context.Users.FindAsync(userId);
            if (currentUser == null || string.IsNullOrWhiteSpace(currentUser.Phone))
            {
                TempData["Error"] = "Tài khoản chưa có Số Điện Thoại, không thể đăng tin.";
                return RedirectToAction("Profile", "Account");
            }

            var isBusiness = await _context.BusinessProfiles
                .AnyAsync(b => b.UserID == userId && b.VerificationStatus == "Approved");
            if (isBusiness) return Forbid();

            if (!ModelState.IsValid)
            {
                ViewBag.ParentTypes = await _context.PropertyTypes.Where(t => t.ParentID == null).ToListAsync();
                var subTypes = await _context.PropertyTypes.Where(t => t.ParentID != null).Select(t => new { t.TypeID, t.TypeName, t.ParentID }).ToListAsync();
                ViewBag.SubTypesJson = System.Text.Json.JsonSerializer.Serialize(subTypes);
                ViewBag.Areas = new SelectList(await _context.Areas.OrderBy(a => a.AreaName).ToListAsync(), "AreaID", "AreaName");
                ViewBag.MasterFeatures = await _context.PropertyFeatures.Where(f => f.PropertyID == null).ToListAsync();
                TempData["Error"] = "Vui lòng kiểm tra lại các thông tin bắt buộc.";
                return View(prop);
            }

            var selectedPackage = await _context.PostServicePackages
                .FirstOrDefaultAsync(p => p.PackageID == prop.PackageID && p.IsActive);

            if (selectedPackage == null)
            {
                TempData["Error"] = "Gói tin không tồn tại hoặc đã ngừng sử dụng.";
                return RedirectToAction("Create");
            }

            int remainingCredits = await GetRemainingPackageCountAsync(userId, selectedPackage.PackageID);

            if (remainingCredits <= 0)
            {
                TempData["Error"] = "Bạn đã hết lượt đăng tin cho gói này. Vui lòng mua thêm!";
                return RedirectToAction("Create");
            }

            if (MainImageFile != null && MainImageFile.Length > 0)
            {
                string uploadDir = Path.Combine(_hostEnvironment.WebRootPath, "uploads/properties");
                if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);
                string fileName = Guid.NewGuid().ToString() + Path.GetExtension(MainImageFile.FileName);
                using (var stream = new FileStream(Path.Combine(uploadDir, fileName), FileMode.Create)) { await MainImageFile.CopyToAsync(stream); }
                prop.MainImage = "/uploads/properties/" + fileName;
            }
            else { prop.MainImage = "/images/no-image.jpg"; }

            prop.UserID = userId;
            prop.Views = 0;
            prop.IsDeleted = false;
            prop.CreatedAt = DateTime.Now;
            prop.UpdatedAt = DateTime.Now;

            bool isDiamond = !string.IsNullOrEmpty(selectedPackage.PackageType) &&
                             selectedPackage.PackageType.Contains("Kim Cương", StringComparison.OrdinalIgnoreCase);

            if (isDiamond)
            {
                prop.Status = "Approved";
                prop.IsAutoApproved = true;
                prop.ApprovedAt = DateTime.Now;
                prop.VipExpiryDate = DateTime.Now.AddDays(Math.Max(1, selectedPackage.DurationDays));
                TempData["Success"] = $"Tin VIP '{selectedPackage.PackageName}' của bạn đã được hệ thống duyệt tự động và hiển thị ngay lập tức!";
            }
            else
            {
                prop.Status = "Pending";
                prop.IsAutoApproved = false;
                prop.ApprovedAt = null;
                prop.VipExpiryDate = null;
                TempData["Success"] = IsNormalPackage(selectedPackage)
                    ? $"Đăng tin thành công với gói Tin Thường. Sau khi Admin duyệt, tin sẽ hiển thị tối đa {NormalPropertyVisibleDays} ngày."
                    : $"Đăng tin thành công với gói '{selectedPackage.PackageName}'. Vui lòng chờ quản trị viên kiểm duyệt để được hiển thị.";
            }

            using var dbTransaction = await _context.Database.BeginTransactionAsync();
            try
            {
                _context.Properties.Add(prop);
                await _context.SaveChangesAsync();

                bool consumed = await ConsumeOnePackageCreditAsync(userId, selectedPackage.PackageID, prop.PropertyID);

                if (!consumed)
                {
                    await dbTransaction.RollbackAsync();
                    TempData["Error"] = "Ví của bạn không đủ lượt đăng cho gói đã chọn. Vui lòng mua thêm gói.";
                    return RedirectToAction("Create");
                }

                await _context.SaveChangesAsync();

                var features = new List<PropertyFeature>();
                string bedrooms = Request.Form["Bedrooms"], bathrooms = Request.Form["Bathrooms"],
                       direction = Request.Form["Direction"], legalStatus = Request.Form["LegalStatus"];
                var amenities = Request.Form["Amenities"].ToList();

                string bedroomsRaw = Request.Form["Bedrooms"].ToString();
                string bathroomsRaw = Request.Form["Bathrooms"].ToString();

                int bedroomsValue = 0;
                int bathroomsValue = 0;

                if (!string.IsNullOrWhiteSpace(bedroomsRaw))
                {
                    int.TryParse(bedroomsRaw, out bedroomsValue);
                }

                if (!string.IsNullOrWhiteSpace(bathroomsRaw))
                {
                    int.TryParse(bathroomsRaw, out bathroomsValue);
                }

                if (bedroomsValue < 0) bedroomsValue = 0;
                if (bathroomsValue < 0) bathroomsValue = 0;

                features.Add(new PropertyFeature
                {
                    PropertyID = prop.PropertyID,
                    FeatureGroup = "Cấu trúc",
                    FeatureName = "Phòng ngủ",
                    FeatureValue = bedroomsValue.ToString()
                });

                features.Add(new PropertyFeature
                {
                    PropertyID = prop.PropertyID,
                    FeatureGroup = "Cấu trúc",
                    FeatureName = "Phòng vệ sinh",
                    FeatureValue = bathroomsValue.ToString()
                });
                if (!string.IsNullOrEmpty(direction)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureGroup = "Hướng nhà", FeatureName = "Hướng nhà", FeatureValue = direction });
                if (!string.IsNullOrEmpty(legalStatus)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureGroup = "Pháp lý", FeatureName = "Pháp lý", FeatureValue = legalStatus });
                if (amenities.Any()) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureGroup = "Tiện ích", FeatureName = "Tiện ích", FeatureValue = string.Join(", ", amenities) });

                if (features.Any())
                {
                    _context.PropertyFeatures.AddRange(features);
                    await _context.SaveChangesAsync();
                }

                if (AdditionalImages != null && AdditionalImages.Any())
                {
                    string galleryDir = Path.Combine(_hostEnvironment.WebRootPath, "uploads/properties/gallery");
                    if (!Directory.Exists(galleryDir)) Directory.CreateDirectory(galleryDir);
                    foreach (var file in AdditionalImages.Take(10))
                    {
                        if (file.Length > 0)
                        {
                            string fileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);
                            using (var stream = new FileStream(Path.Combine(galleryDir, fileName), FileMode.Create)) { await file.CopyToAsync(stream); }
                            _context.PropertyImages.Add(new PropertyImage { PropertyID = prop.PropertyID, ImageURL = "/uploads/properties/gallery/" + fileName, IsMain = false });
                        }
                    }
                    await _context.SaveChangesAsync();
                }

                await dbTransaction.CommitAsync();
            }
            catch (Exception)
            {
                await dbTransaction.RollbackAsync();
                TempData["Error"] = "Lỗi hệ thống khi lưu dữ liệu. Vui lòng thử lại.";
                return RedirectToAction("Create");
            }

            return RedirectToAction("MyAds");
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return RedirectToAction("Login", "Account");

            await NormalizePackageAndExpiredPropertiesAsync(userId);

            var property = await _context.Properties
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PropertyType)
                .FirstOrDefaultAsync(p => p.PropertyID == id && p.UserID == userId && p.IsDeleted == false);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy tin đăng hoặc bạn không có quyền sửa.";
                return RedirectToAction("MyAds");
            }

            if (IsLockedProperty(property))
            {
                TempData["Error"] = GetLockedPropertyMessage(property);
                return RedirectToAction("MyAds");
            }

            ViewBag.MasterFeatures = await _context.PropertyFeatures.Where(f => f.PropertyID == null).ToListAsync();
            await LoadEditViewBags(property);

            return View(property);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, Property prop, IFormFile? MainImageFile, List<IFormFile>? AdditionalImages, List<int>? DeletedImageIds)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return RedirectToAction("Login", "Account");

            await NormalizePackageAndExpiredPropertiesAsync(userId);

            var existingProp = await _context.Properties.FirstOrDefaultAsync(p => p.PropertyID == id && p.UserID == userId && p.IsDeleted == false);
            if (existingProp == null) return NotFound();

            if (IsLockedProperty(existingProp))
            {
                TempData["Error"] = GetLockedPropertyMessage(existingProp);
                return RedirectToAction("MyAds");
            }

            if (!ModelState.IsValid)
            {
                prop.MainImage = existingProp.MainImage;
                prop.PropertyType = await _context.PropertyTypes.FindAsync(prop.TypeID);

                await LoadEditViewBags(existingProp);
                ViewBag.MasterFeatures = await _context.PropertyFeatures.Where(f => f.PropertyID == null).ToListAsync();
                TempData["Error"] = "Dữ liệu không hợp lệ, vui lòng kiểm tra lại.";
                return View(prop);
            }

            var currentPackage = await _context.PostServicePackages
                .FirstOrDefaultAsync(p => p.PackageID == existingProp.PackageID);

            var newPackage = await _context.PostServicePackages
                .FirstOrDefaultAsync(p => p.PackageID == prop.PackageID);

            if (currentPackage == null)
            {
                prop.MainImage = existingProp.MainImage;
                prop.PropertyType = await _context.PropertyTypes.FindAsync(prop.TypeID);

                await LoadEditViewBags(existingProp);
                ViewBag.MasterFeatures = await _context.PropertyFeatures
                    .Where(f => f.PropertyID == null)
                    .ToListAsync();

                TempData["Error"] = "Không tìm thấy gói hiện tại của tin đăng. Vui lòng liên hệ quản trị viên.";
                return View(prop);
            }

            if (newPackage == null)
            {
                prop.MainImage = existingProp.MainImage;
                prop.PropertyType = await _context.PropertyTypes.FindAsync(prop.TypeID);

                await LoadEditViewBags(existingProp);
                ViewBag.MasterFeatures = await _context.PropertyFeatures
                    .Where(f => f.PropertyID == null)
                    .ToListAsync();

                TempData["Error"] = "Gói tin bạn chọn không tồn tại hoặc đã ngừng sử dụng.";
                return View(prop);
            }

            bool isChangingPackage = existingProp.PackageID != prop.PackageID;
            bool isUpgradePackage = false;

            // =====================================================
            // QUY TẮC GÓI KHI SỬA TIN
            // =====================================================
            // 1. Sửa nội dung, ảnh, giá, vị trí, mô tả: KHÔNG trừ lượt.
            // 2. Tin bị tạm dừng / bị từ chối, người bán sửa gửi lại duyệt: KHÔNG trừ lượt nếu giữ nguyên gói.
            // 3. Chỉ trừ 1 lượt khi đổi sang gói VIP cao hơn.
            // 4. Không cho hạ VIP.
            // 5. Không cho đổi ngang sang gói cùng hạng khác.
            // Lưu ý: PriorityLevel càng nhỏ thì hạng VIP càng cao.
            // Ví dụ: Kim Cương = 1, Vàng = 2, Bạc = 3, Đồng = 4, Tin Thường = 5.
            // =====================================================

            if (isChangingPackage)
            {
                if (!newPackage.IsActive)
                {
                    prop.MainImage = existingProp.MainImage;
                    prop.PropertyType = await _context.PropertyTypes.FindAsync(prop.TypeID);

                    await LoadEditViewBags(existingProp);
                    ViewBag.MasterFeatures = await _context.PropertyFeatures
                        .Where(f => f.PropertyID == null)
                        .ToListAsync();

                    TempData["Error"] = $"Gói '{newPackage.PackageName}' hiện đã ngừng sử dụng, không thể chọn để nâng cấp.";
                    return View(prop);
                }

                if (newPackage.PriorityLevel > currentPackage.PriorityLevel)
                {
                    prop.MainImage = existingProp.MainImage;
                    prop.PropertyType = await _context.PropertyTypes.FindAsync(prop.TypeID);

                    await LoadEditViewBags(existingProp);
                    ViewBag.MasterFeatures = await _context.PropertyFeatures
                        .Where(f => f.PropertyID == null)
                        .ToListAsync();

                    TempData["Error"] =
                        $"Không được hạ cấp từ '{currentPackage.PackageName}' xuống '{newPackage.PackageName}'. " +
                        "Bạn chỉ được giữ nguyên gói hiện tại hoặc nâng cấp lên gói VIP cao hơn.";

                    return View(prop);
                }

                if (newPackage.PriorityLevel == currentPackage.PriorityLevel)
                {
                    prop.MainImage = existingProp.MainImage;
                    prop.PropertyType = await _context.PropertyTypes.FindAsync(prop.TypeID);

                    await LoadEditViewBags(existingProp);
                    ViewBag.MasterFeatures = await _context.PropertyFeatures
                        .Where(f => f.PropertyID == null)
                        .ToListAsync();

                    TempData["Error"] =
                        $"Không được đổi ngang từ '{currentPackage.PackageName}' sang '{newPackage.PackageName}'. " +
                        "Vui lòng giữ nguyên gói hiện tại hoặc nâng cấp lên gói VIP cao hơn.";

                    return View(prop);
                }

                if (newPackage.PriorityLevel < currentPackage.PriorityLevel)
                {
                    isUpgradePackage = true;
                }
            }

            if (isUpgradePackage)
            {
                int remainingCredits = await GetRemainingPackageCountAsync(userId, newPackage.PackageID);

                if (remainingCredits <= 0)
                {
                    prop.MainImage = existingProp.MainImage;
                    prop.PropertyType = await _context.PropertyTypes.FindAsync(prop.TypeID);

                    await LoadEditViewBags(existingProp);
                    ViewBag.MasterFeatures = await _context.PropertyFeatures
                        .Where(f => f.PropertyID == null)
                        .ToListAsync();

                    TempData["Error"] =
                        $"Ví của bạn không đủ lượt để nâng cấp lên gói '{newPackage.PackageName}'. " +
                        "Vui lòng mua thêm gói trước khi nâng cấp.";

                    return View(prop);
                }

                bool consumed = await ConsumeOnePackageCreditAsync(userId, newPackage.PackageID, existingProp.PropertyID);

                if (!consumed)
                {
                    prop.MainImage = existingProp.MainImage;
                    prop.PropertyType = await _context.PropertyTypes.FindAsync(prop.TypeID);

                    await LoadEditViewBags(existingProp);
                    ViewBag.MasterFeatures = await _context.PropertyFeatures
                        .Where(f => f.PropertyID == null)
                        .ToListAsync();

                    TempData["Error"] = "Ví của bạn không đủ lượt để nâng cấp gói. Vui lòng mua thêm gói.";
                    return View(prop);
                }

                existingProp.PackageID = newPackage.PackageID;

                if (newPackage.DurationDays > 0)
                {
                    existingProp.VipExpiryDate = DateTime.Now.AddDays(newPackage.DurationDays);
                }
                else
                {
                    existingProp.VipExpiryDate = null;
                }
            }
            else
            {
                // Giữ nguyên gói cũ, tuyệt đối không trừ lượt khi chỉ sửa nội dung hoặc gửi lại duyệt.
                prop.PackageID = existingProp.PackageID;
            }

            existingProp.Title = prop.Title;
            existingProp.Description = prop.Description;
            existingProp.Price = prop.Price;
            existingProp.AreaSize = prop.AreaSize;
            existingProp.Width = prop.Width;
            existingProp.Length = prop.Length;
            existingProp.AddressDetail = prop.AddressDetail;
            existingProp.TypeID = prop.TypeID;
            existingProp.WardID = prop.WardID;
            existingProp.UpdatedAt = DateTime.Now;

            var packageToApplyStatus = await _context.PostServicePackages.FindAsync(existingProp.PackageID);
            bool isDiamondStatus = IsDiamondPackage(packageToApplyStatus);

            // =====================================================
            // QUY TẮC DUYỆT TIN SAU KHI CHỈNH SỬA
            // =====================================================
            // 1. Chỉ đăng mới bằng gói Kim Cương mới được duyệt tự động.
            // 2. Mọi thao tác chỉnh sửa nội dung, đổi ảnh, đổi vị trí, đổi giá,
            //    đổi loại BĐS, đổi gói... đều phải quay về Chờ duyệt.
            // 3. Tránh người dùng đăng tin Kim Cương xong sửa thành tin khác
            //    để lợi dụng duyệt tự động.
            // =====================================================

            existingProp.Status = "Pending";
            existingProp.IsAutoApproved = false;
            existingProp.ApprovedAt = null;
            existingProp.RejectionReason = null;
            existingProp.IsDuplicate = false;
            existingProp.DuplicateReason = null;

            if (isUpgradePackage && packageToApplyStatus != null && packageToApplyStatus.DurationDays > 0)
            {
                existingProp.VipExpiryDate = DateTime.Now.AddDays(packageToApplyStatus.DurationDays);
            }

            if (isUpgradePackage)
            {
                TempData["Success"] =
                    $"Đã lưu thay đổi và nâng cấp tin lên gói '{packageToApplyStatus?.PackageName}'. " +
                    "Hệ thống đã trừ 1 lượt gói nâng cấp từ ví. Tin đã chuyển về trạng thái Chờ duyệt để Admin kiểm tra.";
            }
            else if (isDiamondStatus)
            {
                TempData["Success"] =
                    "Đã lưu thay đổi. Hệ thống không trừ thêm lượt gói vì bạn giữ nguyên gói hiện tại. " +
                    "Tin VIP Kim Cương đã chuyển về trạng thái Chờ duyệt để Admin kiểm tra thủ công.";
            }
            else
            {
                TempData["Success"] =
                    "Đã lưu thay đổi. Hệ thống không trừ thêm lượt gói vì bạn giữ nguyên gói hiện tại. " +
                    "Tin đăng đã chuyển về trạng thái Chờ duyệt để Admin kiểm tra.";
            }

            existingProp.IsDuplicate = false;
            existingProp.RejectionReason = null;

            if (MainImageFile != null && MainImageFile.Length > 0)
            {
                if (!string.IsNullOrEmpty(existingProp.MainImage) && existingProp.MainImage != "/images/no-image.jpg")
                {
                    string oldFilePath = Path.Combine(_hostEnvironment.WebRootPath, existingProp.MainImage.TrimStart('/'));
                    if (System.IO.File.Exists(oldFilePath)) System.IO.File.Delete(oldFilePath);
                }
                string uploadDir = Path.Combine(_hostEnvironment.WebRootPath, "uploads/properties");
                if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);
                string fileName = Guid.NewGuid().ToString() + Path.GetExtension(MainImageFile.FileName);
                using (var stream = new FileStream(Path.Combine(uploadDir, fileName), FileMode.Create)) { await MainImageFile.CopyToAsync(stream); }
                existingProp.MainImage = "/uploads/properties/" + fileName;
            }

            if (DeletedImageIds != null && DeletedImageIds.Any())
            {
                var imagesToDelete = await _context.PropertyImages.Where(img => DeletedImageIds.Contains(img.ImageID) && img.PropertyID == id).ToListAsync();
                foreach (var img in imagesToDelete)
                {
                    string filePath = Path.Combine(_hostEnvironment.WebRootPath, img.ImageURL.TrimStart('/'));
                    if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath);
                }
                _context.PropertyImages.RemoveRange(imagesToDelete);
            }

            if (AdditionalImages != null && AdditionalImages.Any())
            {
                string galleryDir = Path.Combine(_hostEnvironment.WebRootPath, "uploads/properties/gallery");
                if (!Directory.Exists(galleryDir)) Directory.CreateDirectory(galleryDir);
                foreach (var file in AdditionalImages.Take(10))
                {
                    if (file.Length > 0)
                    {
                        string fileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);
                        using (var stream = new FileStream(Path.Combine(galleryDir, fileName), FileMode.Create)) { await file.CopyToAsync(stream); }
                        _context.PropertyImages.Add(new PropertyImage { PropertyID = id, ImageURL = "/uploads/properties/gallery/" + fileName, IsMain = false });
                    }
                }
            }

            var oldFeatures = await _context.PropertyFeatures.Where(f => f.PropertyID == id).ToListAsync();
            _context.PropertyFeatures.RemoveRange(oldFeatures);

            string bds_bedrooms = Request.Form["Bedrooms"], bds_bathrooms = Request.Form["Bathrooms"],
                   bds_direction = Request.Form["Direction"], bds_legal = Request.Form["LegalStatus"];
            var bds_amenities = Request.Form["Amenities"].ToList();

            int bdsBedroomsValue = 0;
            int bdsBathroomsValue = 0;

            if (!string.IsNullOrWhiteSpace(bds_bedrooms))
            {
                int.TryParse(bds_bedrooms, out bdsBedroomsValue);
            }

            if (!string.IsNullOrWhiteSpace(bds_bathrooms))
            {
                int.TryParse(bds_bathrooms, out bdsBathroomsValue);
            }

            if (bdsBedroomsValue < 0) bdsBedroomsValue = 0;
            if (bdsBathroomsValue < 0) bdsBathroomsValue = 0;

            _context.PropertyFeatures.Add(new PropertyFeature
            {
                PropertyID = id,
                FeatureGroup = "Cấu trúc",
                FeatureName = "Phòng ngủ",
                FeatureValue = bdsBedroomsValue.ToString()
            });

            _context.PropertyFeatures.Add(new PropertyFeature
            {
                PropertyID = id,
                FeatureGroup = "Cấu trúc",
                FeatureName = "Phòng vệ sinh",
                FeatureValue = bdsBathroomsValue.ToString()
            });
            if (!string.IsNullOrEmpty(bds_direction)) _context.PropertyFeatures.Add(new PropertyFeature { PropertyID = id, FeatureGroup = "Hướng nhà", FeatureName = "Hướng nhà", FeatureValue = bds_direction });
            if (!string.IsNullOrEmpty(bds_legal)) _context.PropertyFeatures.Add(new PropertyFeature { PropertyID = id, FeatureGroup = "Pháp lý", FeatureName = "Pháp lý", FeatureValue = bds_legal });
            if (bds_amenities.Any()) _context.PropertyFeatures.Add(new PropertyFeature { PropertyID = id, FeatureGroup = "Tiện ích", FeatureName = "Tiện ích", FeatureValue = string.Join(", ", bds_amenities) });

            await _context.SaveChangesAsync();
            return RedirectToAction("MyAds");
        }

        private async Task LoadEditViewBags(Property property)
        {
            ViewBag.ParentTypes = await _context.PropertyTypes.Where(t => t.ParentID == null).ToListAsync();
            var subTypes = await _context.PropertyTypes.Where(t => t.ParentID != null).Select(t => new { t.TypeID, t.TypeName, t.ParentID }).ToListAsync();
            ViewBag.SubTypesJson = System.Text.Json.JsonSerializer.Serialize(subTypes);

            ViewBag.Areas = new SelectList(await _context.Areas.OrderBy(a => a.AreaName).ToListAsync(), "AreaID", "AreaName", property.Ward?.AreaID);

            if (property.Ward != null)
            {
                ViewBag.Wards = new SelectList(await _context.Wards.Where(w => w.AreaID == property.Ward.AreaID).ToListAsync(), "WardID", "WardName", property.WardID);
            }
            else
            {
                ViewBag.Wards = new SelectList(new List<Ward>(), "WardID", "WardName");
            }

            ViewBag.OldFeatures = await _context.PropertyFeatures.Where(f => f.PropertyID == property.PropertyID).ToListAsync();
            ViewBag.OldImages = await _context.PropertyImages.Where(i => i.PropertyID == property.PropertyID && i.IsMain == false).ToListAsync();
            var currentPackage = await _context.PostServicePackages
         .AsNoTracking()
         .FirstOrDefaultAsync(p => p.PackageID == property.PackageID);

            ViewBag.CurrentPackageID = property.PackageID;
            ViewBag.CurrentPackageName = currentPackage?.PackageName ?? "Gói hiện tại";
            ViewBag.CurrentPackageType = currentPackage?.PackageType ?? "";
            ViewBag.CurrentPackagePriority = currentPackage?.PriorityLevel ?? 9999;
            ViewBag.CurrentPackagePrice = currentPackage?.Price ?? 0;
            ViewBag.CurrentPackageDurationDays = currentPackage?.DurationDays ?? 0;

        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMyAd(int id)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdClaim, out int userId))
            {
                return Unauthorized();
            }

            var property = await _context.Properties
                .FirstOrDefaultAsync(p => p.PropertyID == id && p.UserID == userId && p.IsDeleted == false);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy tin đăng hoặc bạn không có quyền xóa.";
                return RedirectToAction("MyAds");
            }

            if (IsLockedProperty(property))
            {
                TempData["Error"] = GetLockedPropertyMessage(property);
                return RedirectToAction("MyAds");
            }

            property.IsDeleted = true;
            property.Status = "Deleted";
            property.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();
            TempData["Success"] = "Đã xóa tin đăng thành công!";

            return RedirectToAction("MyAds");
        }

        [AllowAnonymous]
        [Route("Property/Details/{id}")]
        public async Task<IActionResult> Details(int id)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int.TryParse(userIdClaim, out int currentUserId);
            bool isAdminOrStaff = User.IsInRole("Admin") || User.IsInRole("Staff");

            await NormalizePackageAndExpiredPropertiesAsync();

            var property = await _context.Properties
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.User)
                .Include(p => p.PostServicePackage)
                .Include(p => p.Project)
                .FirstOrDefaultAsync(p => p.PropertyID == id && p.IsDeleted == false);

            if (property == null) return RedirectToAction("Search");

            bool isPublicVisibleStatus =
      property.Status == "Approved" ||
      property.Status == "Sold" ||
      property.Status == "Rented" ||
      property.Status == "Expired";

            if (!isPublicVisibleStatus && !isAdminOrStaff && property.UserID != currentUserId)
            {
                TempData["Error"] = "Tin đăng này đang chờ kiểm duyệt hoặc đã bị ẩn.";
                return RedirectToAction("Search");
            }

            if (property.Status == "Approved")
            {
                property.Views = (property.Views ?? 0) + 1;
                await _context.SaveChangesAsync();
            }

            ViewBag.Features = await _context.PropertyFeatures.Where(f => f.PropertyID == id).ToListAsync();
            ViewBag.PropertyImages = await _context.PropertyImages.Where(img => img.PropertyID == id && img.IsMain == false).ToListAsync();

            ViewBag.SimilarProperties = await _context.Properties
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PropertyType)
                .Where(p => p.Ward.AreaID == property.Ward.AreaID && p.PropertyID != id && p.Status == "Approved" && p.IsDeleted == false)
                .OrderByDescending(p => p.CreatedAt).Take(4).ToListAsync();

            ViewBag.UserProperties = await _context.Properties
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PropertyType)
                .Where(p => p.UserID == property.UserID && p.PropertyID != id && p.Status == "Approved" && p.IsDeleted == false)
                .OrderByDescending(p => p.CreatedAt).Take(4).ToListAsync();

            ViewBag.Comments = await _context.Comments
                .Include(c => c.User)
                .Where(c => c.PropertyID == id && c.IsHidden == false)
                .OrderByDescending(c => c.CreatedAt).ToListAsync();

            bool isFavorited = false;
            User currentUserInfo = null;
            if (currentUserId > 0)
            {
                isFavorited = await _context.Favorites.AnyAsync(f => f.PropertyID == id && f.UserID == currentUserId);
                currentUserInfo = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserID == currentUserId);
            }

            ViewBag.IsFavorited = isFavorited;
            ViewBag.CurrentUserInfo = currentUserInfo;

            return View(property);
        }

        // ==========================================
        // CÁC API TƯƠNG TÁC (ĐÃ FIX: TỰ ĐỘNG SINH THÔNG BÁO)
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> SubmitReport(
       [FromForm] int propertyId,
       [FromForm] string reason,
       [FromForm] string description)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId))
            {
                return Json(new { success = false, message = "Bạn cần đăng nhập." });
            }

            var lockCheck = await CheckPropertyInteractionLockedAsync(propertyId);
            if (lockCheck.IsLocked)
            {
                return Json(new { success = false, message = lockCheck.Message });
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                return Json(new { success = false, message = "Vui lòng chọn lý do báo cáo." });
            }

            var property = await _context.Properties
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PropertyID == propertyId && p.IsDeleted == false);

            if (property == null)
            {
                return Json(new { success = false, message = "Không tìm thấy tin đăng." });
            }

            if (property.UserID == userId)
            {
                return Json(new { success = false, message = "Bạn không thể tự báo cáo tin của mình." });
            }

            var existingReport = await _context.PropertyReports
                .FirstOrDefaultAsync(r =>
                    r.PropertyID == propertyId &&
                    r.ReportedBy == userId &&
                    r.Status == "Pending");

            if (existingReport != null)
            {
                return Json(new { success = false, message = "Bạn đã báo cáo tin này rồi. Vui lòng chờ Admin xử lý." });
            }

            _context.PropertyReports.Add(new PropertyReport
            {
                PropertyID = propertyId,
                ReportedBy = userId,
                Reason = reason.Trim(),
                Description = description?.Trim(),
                Status = "Pending",
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Gửi báo cáo thành công. Cảm ơn bạn đã hỗ trợ hệ thống kiểm duyệt nội dung." });
        }

        private async Task<(bool IsLocked, string Message)> CheckPropertyInteractionLockedAsync(int propertyId)
        {
            await NormalizePackageAndExpiredPropertiesAsync();

            var property = await _context.Properties
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PropertyID == propertyId && p.IsDeleted == false);

            if (property == null)
            {
                return (true, "Tin đăng không tồn tại hoặc đã bị xóa.");
            }

            if (property.Status == "Sold")
            {
                return (true, "Tin đăng này đã bán nên hệ thống đã khóa toàn bộ thao tác.");
            }

            if (property.Status == "Rented")
            {
                return (true, "Tin đăng này đã cho thuê nên hệ thống đã khóa toàn bộ thao tác.");
            }

            if (property.Status == "Expired")
            {
                return (true, "Tin đăng này đã hết hạn hiển thị nên hệ thống đã khóa toàn bộ thao tác.");
            }

            if (property.Status != "Approved")
            {
                return (true, "Tin đăng chưa được duyệt hoặc đang bị tạm ẩn nên không thể thao tác.");
            }

            return (false, "");
        }
        [HttpPost]
        public async Task<IActionResult> BookAppointment(
        [FromForm] int propertyId,
        [FromForm] string customerName,
        [FromForm] string customerPhone,
        [FromForm] string meetingLocation,
        [FromForm] DateTime appointmentDate,
        [FromForm] string note)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập." });
            }

            var lockCheck = await CheckPropertyInteractionLockedAsync(propertyId);
            if (lockCheck.IsLocked)
            {
                return Json(new { success = false, message = lockCheck.Message });
            }

            if (string.IsNullOrWhiteSpace(customerName))
            {
                return Json(new { success = false, message = "Vui lòng nhập họ tên người đặt lịch." });
            }

            if (string.IsNullOrWhiteSpace(customerPhone) || customerPhone.Trim().Length != 10 || !customerPhone.Trim().All(char.IsDigit))
            {
                return Json(new { success = false, message = "Số điện thoại không hợp lệ. Vui lòng nhập đúng 10 số." });
            }

            if (string.IsNullOrWhiteSpace(meetingLocation))
            {
                return Json(new { success = false, message = "Vui lòng nhập địa điểm hẹn." });
            }

            if (appointmentDate <= DateTime.Now)
            {
                return Json(new { success = false, message = "Thời gian hẹn phải lớn hơn thời gian hiện tại." });
            }

            var property = await _context.Properties
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PropertyID == propertyId && p.IsDeleted == false);

            if (property == null)
            {
                return Json(new { success = false, message = "Không tìm thấy bất động sản." });
            }

            if (property.UserID == userId)
            {
                return Json(new { success = false, message = "Bạn không thể tự đặt lịch xem tin của mình." });
            }

            _context.Appointments.Add(new Appointment
            {
                PropertyID = propertyId,
                BuyerID = userId,
                SellerID = property.UserID,
                CustomerName = customerName.Trim(),
                CustomerPhone = customerPhone.Trim(),
                MeetingLocation = meetingLocation.Trim(),
                AppointmentDate = appointmentDate,
                Note = note ?? "",
                Status = "Pending",
                CreatedAt = DateTime.Now
            });

            _context.Notifications.Add(new Notification
            {
                UserID = property.UserID,
                Title = "Lịch hẹn xem nhà mới",
                Content = $"Khách hàng {customerName.Trim()} ({customerPhone.Trim()}) vừa đặt lịch hẹn xem bất động sản '{property.Title}' vào lúc {appointmentDate:HH:mm dd/MM/yyyy}.",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem chi tiết",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Đã gửi yêu cầu hẹn gặp thành công!" });
        }
        [HttpPost]
        public async Task<IActionResult> SubmitConsultation(
        [FromForm] int propertyId,
        [FromForm] string fullName,
        [FromForm] string phone,
        [FromForm] string email,
        [FromForm] string note)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int senderId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập để gửi yêu cầu tư vấn." });
            }

            var lockCheck = await CheckPropertyInteractionLockedAsync(propertyId);
            if (lockCheck.IsLocked)
            {
                return Json(new { success = false, message = lockCheck.Message });
            }

            if (string.IsNullOrWhiteSpace(fullName))
            {
                return Json(new { success = false, message = "Vui lòng nhập họ tên." });
            }

            if (string.IsNullOrWhiteSpace(phone) || phone.Trim().Length != 10 || !phone.Trim().All(char.IsDigit))
            {
                return Json(new { success = false, message = "Số điện thoại không hợp lệ. Vui lòng nhập đúng 10 số." });
            }

            var property = await _context.Properties
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PropertyID == propertyId && p.IsDeleted == false);

            if (property == null)
            {
                return Json(new { success = false, message = "Không tìm thấy bất động sản." });
            }

            if (property.UserID == senderId)
            {
                return Json(new { success = false, message = "Bạn không thể tự gửi yêu cầu tư vấn cho tin của mình." });
            }

            _context.Consultations.Add(new Consultation
            {
                PropertyID = propertyId,
                FullName = fullName.Trim(),
                Phone = phone.Trim(),
                Email = email ?? "",
                Note = note ?? "",
                SenderID = senderId,
                Status = "New",
                CreatedAt = DateTime.Now
            });

            _context.Notifications.Add(new Notification
            {
                UserID = property.UserID,
                Title = "Yêu cầu tư vấn mới",
                Content = $"Khách hàng {fullName.Trim()} ({phone.Trim()}) đang quan tâm và cần tư vấn về bất động sản '{property.Title}'.",
                ActionUrl = "/Consultations/Index",
                ActionText = "Xem chi tiết",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Đã gửi thông tin tư vấn!" });
        }
        [HttpPost]
        public async Task<IActionResult> SubmitComment(
        [FromForm] int propertyId,
        [FromForm] string content)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập." });
            }

            var lockCheck = await CheckPropertyInteractionLockedAsync(propertyId);
            if (lockCheck.IsLocked)
            {
                return Json(new { success = false, message = lockCheck.Message });
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return Json(new { success = false, message = "Nội dung bình luận không được để trống." });
            }

            var property = await _context.Properties
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PropertyID == propertyId && p.IsDeleted == false);

            if (property == null)
            {
                return Json(new { success = false, message = "Không tìm thấy tin đăng." });
            }

            _context.Comments.Add(new Comment
            {
                PropertyID = propertyId,
                UserID = userId,
                Content = content.Trim(),
                CreatedAt = DateTime.Now,
                IsHidden = true
            });

            if (property.UserID != userId)
            {
                _context.Notifications.Add(new Notification
                {
                    UserID = property.UserID,
                    Title = "Bình luận mới trên tin đăng",
                    Content = $"Một khách hàng vừa để lại bình luận trên bất động sản '{property.Title}' của bạn. Vui lòng kiểm tra và phản hồi.",
                    ActionUrl = $"/Property/Details/{propertyId}",
                    ActionText = "Xem bình luận",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }

            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Bình luận đã gửi và đang chờ duyệt." });
        }

        [HttpPost]
        public async Task<IActionResult> SendMessage(
       [FromForm] int receiverId,
       [FromForm] int propertyId,
       [FromForm] string messageContent)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int senderId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập." });
            }

            var lockCheck = await CheckPropertyInteractionLockedAsync(propertyId);
            if (lockCheck.IsLocked)
            {
                return Json(new { success = false, message = lockCheck.Message });
            }

            if (senderId == receiverId)
            {
                return Json(new { success = false, message = "Không thể tự nhắn cho mình." });
            }

            if (string.IsNullOrWhiteSpace(messageContent))
            {
                return Json(new { success = false, message = "Vui lòng nhập nội dung tin nhắn." });
            }

            var property = await _context.Properties
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PropertyID == propertyId && p.IsDeleted == false);

            if (property == null)
            {
                return Json(new { success = false, message = "Không tìm thấy tin đăng." });
            }

            if (property.UserID != receiverId)
            {
                return Json(new { success = false, message = "Người nhận không khớp với chủ tin đăng." });
            }

            _context.UserMessages.Add(new UserMessage
            {
                SenderID = senderId,
                ReceiverID = receiverId,
                PropertyID = propertyId,
                MessageContent = messageContent.Trim(),
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            _context.Notifications.Add(new Notification
            {
                UserID = receiverId,
                Title = "Tin nhắn mới",
                Content = $"Bạn có tin nhắn mới liên quan đến bất động sản '{property.Title}'.",
                ActionUrl = "/UserMessages/Index",
                ActionText = "Xem tin nhắn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Tin nhắn đã gửi!" });
        }
    }
}