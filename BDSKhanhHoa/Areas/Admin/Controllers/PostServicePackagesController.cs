using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class PostServicePackagesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditLogService _auditLogService;

        private sealed class PackageTierOption
        {
            public string Type { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public int PriorityLevel { get; set; }
            public int DefaultDurationDays { get; set; }
            public decimal SuggestedPrice { get; set; }
            public string SuggestedPackageName { get; set; } = "";
            public string SuggestedDescription { get; set; } = "";
            public string Icon { get; set; } = "";
            public string CssKey { get; set; } = "";
        }

        private readonly List<PackageTierOption> _packageTiers = new()
        {
            new PackageTierOption
            {
                Type = "Kim Cương",
                DisplayName = "VIP Kim Cương - Hạng 1 cao nhất",
                PriorityLevel = 1,
                DefaultDurationDays = 30,
                SuggestedPrice = 500000,
                SuggestedPackageName = "VIP Kim Cương 30 Ngày",
                SuggestedDescription = "Gói hiển thị cao nhất, nổi bật nhất, ưu tiên trên toàn bộ danh sách tìm kiếm. Tin đăng mới dùng gói này có thể được duyệt tự động theo chính sách hệ thống.",
                Icon = "bi-gem",
                CssKey = "diamond"
            },
            new PackageTierOption
            {
                Type = "Vàng",
                DisplayName = "VIP Vàng - Hạng 2",
                PriorityLevel = 2,
                DefaultDurationDays = 30,
                SuggestedPrice = 300000,
                SuggestedPackageName = "VIP Vàng 30 Ngày",
                SuggestedDescription = "Gói hiển thị nổi bật, đứng sau VIP Kim Cương, phù hợp tin cần tăng độ tiếp cận.",
                Icon = "bi-star-fill",
                CssKey = "gold"
            },
            new PackageTierOption
            {
                Type = "Bạc",
                DisplayName = "VIP Bạc - Hạng 3",
                PriorityLevel = 3,
                DefaultDurationDays = 30,
                SuggestedPrice = 200000,
                SuggestedPackageName = "VIP Bạc 30 Ngày",
                SuggestedDescription = "Gói tăng độ ưu tiên hiển thị, đứng sau VIP Vàng.",
                Icon = "bi-shield-fill",
                CssKey = "silver"
            },
            new PackageTierOption
            {
                Type = "Đồng",
                DisplayName = "VIP Đồng - Hạng 4",
                PriorityLevel = 4,
                DefaultDurationDays = 30,
                SuggestedPrice = 100000,
                SuggestedPackageName = "VIP Đồng 30 Ngày",
                SuggestedDescription = "Gói VIP cơ bản, ưu tiên hơn tin thường.",
                Icon = "bi-award-fill",
                CssKey = "bronze"
            },
            new PackageTierOption
            {
                Type = "Tin Thường",
                DisplayName = "Tin Thường - Hạng 5 thấp nhất",
                PriorityLevel = 5,
                DefaultDurationDays = 30,
                SuggestedPrice = 0,
                SuggestedPackageName = "Tin Thường 30 Ngày",
                SuggestedDescription = "Gói đăng tin cơ bản, không có hiệu ứng VIP, hiển thị sau các gói VIP.",
                Icon = "bi-tag-fill",
                CssKey = "normal"
            }
        };

        public PostServicePackagesController(
            ApplicationDbContext context,
            IAuditLogService auditLogService)
        {
            _context = context;
            _auditLogService = auditLogService;
        }

        // =====================================================
        // DANH SÁCH GÓI
        // =====================================================
        [HttpGet]
        public async Task<IActionResult> Index(string status = "all", string keyword = "")
        {
            var query = _context.PostServicePackages
                .AsNoTracking()
                .AsQueryable();

            if (status == "active")
            {
                query = query.Where(p => p.IsActive);
            }
            else if (status == "inactive")
            {
                query = query.Where(p => !p.IsActive);
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim().ToLower();

                query = query.Where(p =>
                    p.PackageName.ToLower().Contains(keyword) ||
                    p.PackageType.ToLower().Contains(keyword) ||
                    (p.Description != null && p.Description.ToLower().Contains(keyword)));
            }

            var packages = await query
                .OrderBy(p => p.IsActive ? 0 : 1)
                .ThenBy(p => p.PriorityLevel)
                .ThenByDescending(p => p.Price)
                .ThenByDescending(p => p.DurationDays)
                .ToListAsync();

            ViewBag.Status = status;
            ViewBag.Keyword = keyword;

            ViewBag.TotalCount = await _context.PostServicePackages.CountAsync();
            ViewBag.ActiveCount = await _context.PostServicePackages.CountAsync(p => p.IsActive);
            ViewBag.InactiveCount = await _context.PostServicePackages.CountAsync(p => !p.IsActive);
            ViewBag.UsedInPropertiesCount = await _context.Properties
                .Where(p => p.PackageID != null)
                .Select(p => p.PackageID)
                .Distinct()
                .CountAsync();

            ViewBag.UsedInTransactionsCount = await _context.Transactions
                .Where(t => t.PackageID != null)
                .Select(t => t.PackageID)
                .Distinct()
                .CountAsync();

            return View(packages);
        }

        // =====================================================
        // TẠO GÓI
        // =====================================================
        [HttpGet]
        public IActionResult Create()
        {
            var model = new PostServicePackage
            {
                PackageType = "Tin Thường",
                PackageName = "Tin Thường 30 Ngày",
                Price = 0,
                DurationDays = 30,
                PriorityLevel = 5,
                Description = "Gói đăng tin cơ bản, không có hiệu ứng VIP, hiển thị sau các gói VIP.",
                IsActive = true
            };

            PrepareViewData(model.PackageType);
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(PostServicePackage package)
        {
            NormalizePackageByType(package);
            ValidatePackage(package);

            if (!ModelState.IsValid)
            {
                PrepareViewData(package.PackageType);
                return View(package);
            }

            string normalizedName = package.PackageName.Trim().ToLower();

            bool isDuplicateName = await _context.PostServicePackages
                .AnyAsync(p => p.PackageName.ToLower() == normalizedName);

            if (isDuplicateName)
            {
                ModelState.AddModelError(nameof(PostServicePackage.PackageName), "Tên gói này đã tồn tại trên hệ thống.");
                PrepareViewData(package.PackageType);
                return View(package);
            }

            _context.PostServicePackages.Add(package);
            await _context.SaveChangesAsync();

            int userId = GetCurrentUserId();

            await _auditLogService.LogAsync(
                userId,
                "Thêm gói đăng tin",
                "ServicePackages",
                $"PackageID: {package.PackageID}",
                oldValues: null,
                newValues: BuildPackageAuditText(package),
                severity: "Info"
            );

            TempData["Success"] = $"Đã tạo gói \"{package.PackageName}\" thành công.";
            return RedirectToAction(nameof(Index));
        }

        // =====================================================
        // SỬA GÓI
        // =====================================================
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var package = await _context.PostServicePackages
                .FirstOrDefaultAsync(p => p.PackageID == id);

            if (package == null)
            {
                TempData["Error"] = "Không tìm thấy gói đăng tin.";
                return RedirectToAction(nameof(Index));
            }

            NormalizePackageByType(package);

            PrepareViewData(package.PackageType);
            return View(package);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, PostServicePackage package)
        {
            if (id != package.PackageID)
            {
                TempData["Error"] = "Dữ liệu gói không hợp lệ.";
                return RedirectToAction(nameof(Index));
            }

            var existingPackage = await _context.PostServicePackages
                .FirstOrDefaultAsync(p => p.PackageID == id);

            if (existingPackage == null)
            {
                TempData["Error"] = "Không tìm thấy gói đăng tin.";
                return RedirectToAction(nameof(Index));
            }

            NormalizePackageByType(package);
            ValidatePackage(package);

            if (!ModelState.IsValid)
            {
                PrepareViewData(package.PackageType);
                return View(package);
            }

            string normalizedName = package.PackageName.Trim().ToLower();

            bool isDuplicateName = await _context.PostServicePackages
                .AnyAsync(p => p.PackageName.ToLower() == normalizedName && p.PackageID != id);

            if (isDuplicateName)
            {
                ModelState.AddModelError(nameof(PostServicePackage.PackageName), "Tên gói này đã tồn tại trên hệ thống.");
                PrepareViewData(package.PackageType);
                return View(package);
            }

            string oldValues = BuildPackageAuditText(existingPackage);

            existingPackage.PackageType = package.PackageType;
            existingPackage.PackageName = package.PackageName;
            existingPackage.Price = package.Price;
            existingPackage.DurationDays = package.DurationDays;
            existingPackage.PriorityLevel = package.PriorityLevel;
            existingPackage.Description = package.Description;
            existingPackage.IsActive = package.IsActive;

            await _context.SaveChangesAsync();

            int userId = GetCurrentUserId();

            await _auditLogService.LogAsync(
                userId,
                "Cập nhật gói đăng tin",
                "ServicePackages",
                $"PackageID: {existingPackage.PackageID}",
                oldValues: oldValues,
                newValues: BuildPackageAuditText(existingPackage),
                severity: "Info"
            );

            TempData["Success"] = $"Đã cập nhật gói \"{existingPackage.PackageName}\" thành công.";
            return RedirectToAction(nameof(Index));
        }

        // =====================================================
        // NGỪNG DÙNG GÓI
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Deactivate(int id)
        {
            var package = await _context.PostServicePackages
                .FirstOrDefaultAsync(p => p.PackageID == id);

            if (package == null)
            {
                TempData["Error"] = "Không tìm thấy gói đăng tin cần ngừng dùng.";
                return RedirectToAction(nameof(Index));
            }

            if (!package.IsActive)
            {
                TempData["Error"] = $"Gói \"{package.PackageName}\" hiện đã ở trạng thái ngừng dùng.";
                return RedirectToAction(nameof(Index));
            }

            string oldValues = BuildPackageAuditText(package);

            package.IsActive = false;
            await _context.SaveChangesAsync();

            int userId = GetCurrentUserId();

            await _auditLogService.LogAsync(
                userId,
                "Ngừng dùng gói đăng tin",
                "ServicePackages",
                $"PackageID: {package.PackageID}",
                oldValues: oldValues,
                newValues: BuildPackageAuditText(package),
                severity: "Warning"
            );

            TempData["Success"] =
                $"Đã ngừng dùng gói \"{package.PackageName}\". Gói này sẽ không còn nên được hiển thị cho người dùng mua/chọn mới, nhưng lịch sử giao dịch và tin cũ vẫn được giữ nguyên.";

            return RedirectToAction(nameof(Index));
        }

        // =====================================================
        // MỞ LẠI GÓI
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Activate(int id)
        {
            var package = await _context.PostServicePackages
                .FirstOrDefaultAsync(p => p.PackageID == id);

            if (package == null)
            {
                TempData["Error"] = "Không tìm thấy gói đăng tin cần mở lại.";
                return RedirectToAction(nameof(Index));
            }

            if (package.IsActive)
            {
                TempData["Error"] = $"Gói \"{package.PackageName}\" hiện đang được sử dụng.";
                return RedirectToAction(nameof(Index));
            }

            string oldValues = BuildPackageAuditText(package);

            package.IsActive = true;
            await _context.SaveChangesAsync();

            int userId = GetCurrentUserId();

            await _auditLogService.LogAsync(
                userId,
                "Mở lại gói đăng tin",
                "ServicePackages",
                $"PackageID: {package.PackageID}",
                oldValues: oldValues,
                newValues: BuildPackageAuditText(package),
                severity: "Info"
            );

            TempData["Success"] = $"Đã mở lại gói \"{package.PackageName}\" thành công.";
            return RedirectToAction(nameof(Index));
        }

        // =====================================================
        // XÓA GÓI
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var package = await _context.PostServicePackages
                .FirstOrDefaultAsync(p => p.PackageID == id);

            if (package == null)
            {
                TempData["Error"] = "Không tìm thấy gói đăng tin cần xóa.";
                return RedirectToAction(nameof(Index));
            }

            int propertyCount = await _context.Properties
                .CountAsync(p => p.PackageID == id);

            int transactionCount = await _context.Transactions
                .CountAsync(t => t.PackageID == id);

            if (propertyCount > 0 || transactionCount > 0)
            {
                var reasons = new List<string>();

                if (propertyCount > 0)
                {
                    reasons.Add($"{propertyCount:N0} tin bất động sản");
                }

                if (transactionCount > 0)
                {
                    reasons.Add($"{transactionCount:N0} giao dịch/thanh toán");
                }

                TempData["Error"] =
                    $"Không thể xóa gói \"{package.PackageName}\" vì gói này đã được sử dụng trong {string.Join(" và ", reasons)}. " +
                    "Để bảo toàn lịch sử hóa đơn, thanh toán và dữ liệu tin đăng cũ, hệ thống không cho xóa gói đã phát sinh dữ liệu. " +
                    "Bạn hãy dùng chức năng Ngừng dùng gói thay vì xóa.";

                int userIdCheck = GetCurrentUserId();

                await _auditLogService.LogAsync(
                    userIdCheck,
                    "Chặn xóa gói đăng tin đang được sử dụng",
                    "ServicePackages",
                    $"PackageID: {package.PackageID}",
                    oldValues: BuildPackageAuditText(package),
                    newValues: $"Không xóa. Lý do: Có {propertyCount} tin bất động sản và {transactionCount} giao dịch/thanh toán đang tham chiếu gói này.",
                    severity: "Warning"
                );

                return RedirectToAction(nameof(Index));
            }

            string oldValues = BuildPackageAuditText(package);

            _context.PostServicePackages.Remove(package);
            await _context.SaveChangesAsync();

            int userId = GetCurrentUserId();

            await _auditLogService.LogAsync(
                userId,
                "Xóa gói đăng tin",
                "ServicePackages",
                $"PackageID: {id}",
                oldValues: oldValues,
                newValues: "Gói đăng tin đã được xóa khỏi hệ thống.",
                severity: "Warning"
            );

            TempData["Success"] = $"Đã xóa gói \"{package.PackageName}\" thành công.";
            return RedirectToAction(nameof(Index));
        }

        // =====================================================
        // DỮ LIỆU CHO VIEW
        // =====================================================
        private void PrepareViewData(string? selectedType = null)
        {
            ViewBag.PackageTypes = new SelectList(_packageTiers, "Type", "DisplayName", selectedType);

            ViewBag.TierOptionsJson = JsonSerializer.Serialize(_packageTiers.Select(t => new
            {
                type = t.Type,
                displayName = t.DisplayName,
                priorityLevel = t.PriorityLevel,
                defaultDurationDays = t.DefaultDurationDays,
                suggestedPrice = t.SuggestedPrice,
                suggestedPackageName = t.SuggestedPackageName,
                suggestedDescription = t.SuggestedDescription,
                icon = t.Icon,
                cssKey = t.CssKey
            }));
        }

        private PackageTierOption? GetTierOption(string? packageType)
        {
            if (string.IsNullOrWhiteSpace(packageType))
            {
                return null;
            }

            return _packageTiers.FirstOrDefault(t => t.Type == packageType.Trim());
        }

        // =====================================================
        // CHUẨN HÓA VÀ KIỂM TRA
        // =====================================================
        private void NormalizePackageByType(PostServicePackage package)
        {
            package.PackageType = package.PackageType?.Trim() ?? "";
            package.PackageName = package.PackageName?.Trim() ?? "";
            package.Description = package.Description?.Trim();

            var tier = GetTierOption(package.PackageType);

            if (tier == null)
            {
                return;
            }

            package.PriorityLevel = tier.PriorityLevel;

            if (string.IsNullOrWhiteSpace(package.PackageName))
            {
                package.PackageName = tier.SuggestedPackageName;
            }

            if (string.IsNullOrWhiteSpace(package.Description))
            {
                package.Description = tier.SuggestedDescription;
            }
        }

        private void ValidatePackage(PostServicePackage package)
        {
            var tier = GetTierOption(package.PackageType);

            if (tier == null)
            {
                ModelState.AddModelError(nameof(PostServicePackage.PackageType), "Vui lòng chọn đúng phân loại gói.");
                return;
            }

            if (string.IsNullOrWhiteSpace(package.PackageName))
            {
                ModelState.AddModelError(nameof(PostServicePackage.PackageName), "Vui lòng nhập tên hiển thị của gói.");
            }

            if (package.PackageName != null && package.PackageName.Length > 100)
            {
                ModelState.AddModelError(nameof(PostServicePackage.PackageName), "Tên gói không được vượt quá 100 ký tự.");
            }

            if (package.Price < 0)
            {
                ModelState.AddModelError(nameof(PostServicePackage.Price), "Giá gói không được nhỏ hơn 0.");
            }

            if (package.DurationDays < 0)
            {
                ModelState.AddModelError(nameof(PostServicePackage.DurationDays), "Thời hạn không được nhỏ hơn 0.");
            }

            if (package.DurationDays > 3650)
            {
                ModelState.AddModelError(nameof(PostServicePackage.DurationDays), "Thời hạn không được vượt quá 3650 ngày.");
            }

            if (package.PackageType != "Tin Thường" && package.DurationDays <= 0)
            {
                ModelState.AddModelError(nameof(PostServicePackage.DurationDays), "Gói VIP phải có thời hạn lớn hơn 0 ngày.");
            }

            if (package.Description != null && package.Description.Length > 500)
            {
                ModelState.AddModelError(nameof(PostServicePackage.Description), "Mô tả không được vượt quá 500 ký tự.");
            }
        }

        // =====================================================
        // HELPER
        // =====================================================
        private int GetCurrentUserId()
        {
            string? userIdText = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (int.TryParse(userIdText, out int userId))
            {
                return userId;
            }

            return 0;
        }

        private static string BuildPackageAuditText(PostServicePackage package)
        {
            return JsonSerializer.Serialize(new
            {
                package.PackageID,
                package.PackageType,
                package.PackageName,
                package.Price,
                package.DurationDays,
                package.PriorityLevel,
                package.Description,
                package.IsActive,
                TrangThai = package.IsActive ? "Đang dùng" : "Ngừng dùng"
            }, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        private bool PackageExists(int id)
        {
            return _context.PostServicePackages.Any(e => e.PackageID == id);
        }
    }
}