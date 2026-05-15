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
                SuggestedDescription = "Gói hiển thị cao nhất, nổi bật nhất, ưu tiên trên toàn bộ danh sách tìm kiếm.",
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
                SuggestedDescription = "Gói hiển thị nổi bật, đứng sau VIP Kim Cương.",
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

        public PostServicePackagesController(ApplicationDbContext context, IAuditLogService auditLogService)
        {
            _context = context;
            _auditLogService = auditLogService;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var packages = await _context.PostServicePackages
                .AsNoTracking()
                .OrderBy(p => p.PriorityLevel)
                .ThenByDescending(p => p.Price)
                .ThenByDescending(p => p.DurationDays)
                .ToListAsync();

            return View(packages);
        }

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
                Description = "Gói đăng tin cơ bản, không có hiệu ứng VIP, hiển thị sau các gói VIP."
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

            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
            await _auditLogService.LogAsync(
                userId,
                "Thêm gói đăng tin",
                "ServicePackages",
                $"PackageID: {package.PackageID} - {package.PackageName} - Hạng: {package.PackageType} - Priority: {package.PriorityLevel}",
                severity: "Info"
            );

            TempData["Success"] = $"Đã tạo gói \"{package.PackageName}\" thành công.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var package = await _context.PostServicePackages.FindAsync(id);

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
                return NotFound();
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

            try
            {
                _context.Update(package);
                await _context.SaveChangesAsync();

                int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                await _auditLogService.LogAsync(
                    userId,
                    "Cập nhật gói đăng tin",
                    "ServicePackages",
                    $"PackageID: {package.PackageID} - {package.PackageName} - Hạng: {package.PackageType} - Priority: {package.PriorityLevel}",
                    severity: "Info"
                );

                TempData["Success"] = $"Đã cập nhật gói \"{package.PackageName}\" thành công.";
                return RedirectToAction(nameof(Index));
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!PackageExists(package.PackageID))
                {
                    return NotFound();
                }

                throw;
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var package = await _context.PostServicePackages.FindAsync(id);

            if (package == null)
            {
                TempData["Error"] = "Không tìm thấy gói cần xóa.";
                return RedirectToAction(nameof(Index));
            }

            bool isUsed = await _context.Properties.AnyAsync(p => p.PackageID == id);

            if (isUsed)
            {
                TempData["Error"] = "Không thể xóa vì đang có tin bất động sản sử dụng gói này.";
                return RedirectToAction(nameof(Index));
            }

            _context.PostServicePackages.Remove(package);
            await _context.SaveChangesAsync();

            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
            await _auditLogService.LogAsync(
                userId,
                "Xóa gói đăng tin",
                "ServicePackages",
                $"PackageID: {id} - {package.PackageName}",
                severity: "Warning"
            );

            TempData["Success"] = $"Đã xóa gói \"{package.PackageName}\" thành công.";
            return RedirectToAction(nameof(Index));
        }

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

            // Chỉ tự động gán hạng hiển thị.
            // Không ép giá, không ép thời hạn, vì admin được quyền cấu hình.
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

            if (package.Price < 0)
            {
                ModelState.AddModelError(nameof(PostServicePackage.Price), "Giá gói không được nhỏ hơn 0.");
            }

            if (package.DurationDays < 0)
            {
                ModelState.AddModelError(nameof(PostServicePackage.DurationDays), "Thời hạn không được nhỏ hơn 0.");
            }

            // Gói VIP phải có ngày hết hạn để hệ thống còn tự hạ cấp khi VIP hết hạn.
            if (package.PackageType != "Tin Thường" && package.DurationDays <= 0)
            {
                ModelState.AddModelError(nameof(PostServicePackage.DurationDays), "Gói VIP phải có thời hạn lớn hơn 0 ngày.");
            }

            // Tin Thường được phép:
            // - Giá = 0 hoặc > 0
            // - Thời hạn = 0 hoặc > 0
            // 0 ngày có thể hiểu là không hết hạn, còn > 0 là gói tin thường theo ngày.
        }
        private bool PackageExists(int id)
        {
            return _context.PostServicePackages.Any(e => e.PackageID == id);
        }
    }
}