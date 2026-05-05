using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
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

        public PropertyController(ApplicationDbContext context, IWebHostEnvironment hostEnvironment)
        {
            _context = context;
            _hostEnvironment = hostEnvironment;
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

        // ==========================================
        // 0. TRANG SEARCH CHÍNH (ĐÃ TỐI ƯU SẮP XẾP)
        // ==========================================
        [AllowAnonymous]
        [Route("Property/Search")]
        public async Task<IActionResult> Search(
       string? transactionType = null, string? keyword = null, int? typeId = null,
       int? areaId = null, int? wardId = null, decimal? minPrice = null,
       decimal? maxPrice = null, string? priceRange = null, decimal? minSize = null, decimal? maxSize = null,
       string? bedrooms = null, string? bathrooms = null, string? direction = null,
       string? legalStatus = null, string[]? amenities = null, int? packageId = null,
       string? sortOrder = null, int page = 1)
        {
            int pageSize = 12;
            page = Math.Max(1, page);

            if (string.IsNullOrEmpty(transactionType)) transactionType = "buy";
            keyword = keyword?.Trim().ToLower();

            if (!string.IsNullOrEmpty(priceRange) && !minPrice.HasValue && !maxPrice.HasValue)
            {
                var parts = priceRange.Split('-');
                if (parts.Length == 2)
                {
                    if (decimal.TryParse(parts[0], out decimal pMin)) minPrice = pMin;
                    if (decimal.TryParse(parts[1], out decimal pMax)) maxPrice = pMax;
                }
            }

            var query = _context.Properties
                .AsNoTracking()
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PostServicePackage)
                .Where(p => p.Status == "Approved" && p.IsDeleted == false)
                .AsQueryable();

            if (transactionType == "buy")
                query = query.Where(p => p.PropertyType.ParentID == 1 || p.TypeID == 1);
            else if (transactionType == "rent")
                query = query.Where(p => p.PropertyType.ParentID == 2 || p.TypeID == 2);

            if (typeId.HasValue) query = query.Where(p => p.TypeID == typeId);
            if (areaId.HasValue) query = query.Where(p => p.Ward.AreaID == areaId);
            if (wardId.HasValue) query = query.Where(p => p.WardID == wardId);
            if (packageId.HasValue) query = query.Where(p => p.PackageID == packageId);

            if (minPrice.HasValue) query = query.Where(p => p.Price >= minPrice.Value * 1000000);
            if (maxPrice.HasValue) query = query.Where(p => p.Price <= maxPrice.Value * 1000000);
            if (minSize.HasValue) query = query.Where(p => p.AreaSize >= minSize.Value);
            if (maxSize.HasValue) query = query.Where(p => p.AreaSize <= maxSize.Value);

            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(p => p.Title.ToLower().Contains(keyword) || p.AddressDetail.ToLower().Contains(keyword));
            }

            if (!string.IsNullOrEmpty(direction))
                query = query.Where(p => _context.PropertyFeatures.Any(f => f.PropertyID == p.PropertyID && f.FeatureName == "Hướng nhà" && f.FeatureValue == direction));

            if (!string.IsNullOrEmpty(legalStatus))
                query = query.Where(p => _context.PropertyFeatures.Any(f => f.PropertyID == p.PropertyID && f.FeatureName == "Pháp lý" && f.FeatureValue == legalStatus));

            if (!string.IsNullOrEmpty(bedrooms))
            {
                if (bedrooms == "5")
                {
                    var highBeds = new[] { "5", "6", "7", "8", "9", "10", "10+", "5+" };
                    query = query.Where(p => _context.PropertyFeatures.Any(f => f.PropertyID == p.PropertyID && f.FeatureName == "Phòng ngủ" && highBeds.Contains(f.FeatureValue)));
                }
                else
                {
                    query = query.Where(p => _context.PropertyFeatures.Any(f => f.PropertyID == p.PropertyID && f.FeatureName == "Phòng ngủ" && f.FeatureValue == bedrooms));
                }
            }

            if (!string.IsNullOrEmpty(bathrooms))
            {
                if (bathrooms == "4")
                {
                    var highBaths = new[] { "4", "5", "6", "7", "8", "9", "10+", "4+" };
                    query = query.Where(p => _context.PropertyFeatures.Any(f => f.PropertyID == p.PropertyID && f.FeatureName == "Phòng vệ sinh" && highBaths.Contains(f.FeatureValue)));
                }
                else
                {
                    query = query.Where(p => _context.PropertyFeatures.Any(f => f.PropertyID == p.PropertyID && f.FeatureName == "Phòng vệ sinh" && f.FeatureValue == bathrooms));
                }
            }

            if (amenities != null && amenities.Any())
            {
                foreach (var am in amenities)
                {
                    query = query.Where(p => _context.PropertyFeatures.Any(f => f.PropertyID == p.PropertyID && f.FeatureName == "Tiện ích" && f.FeatureValue.Contains(am)));
                }
            }

            // SẮP XẾP KHOA HỌC CHUẨN XÁC
            // Ưu tiên 1: PriorityLevel (Số càng nhỏ càng nằm trên: Kim cương=10, Vàng=40. Tin thường cho số 9999 để nằm chót)
            // Ưu tiên 2: Mới tới các tiêu chí Giá hoặc Diện tích NẰM TRONG CÙNG 1 LOẠI VIP
            query = sortOrder switch
            {
                "price_asc" => query
                    .OrderBy(p => p.PostServicePackage != null && p.PostServicePackage.PriorityLevel > 0 ? p.PostServicePackage.PriorityLevel : 9999)
                    .ThenBy(p => p.Price), // Thấp -> Cao

                "price_desc" => query
                    .OrderBy(p => p.PostServicePackage != null && p.PostServicePackage.PriorityLevel > 0 ? p.PostServicePackage.PriorityLevel : 9999)
                    .ThenByDescending(p => p.Price), // Cao -> Thấp

                "area_desc" => query
                    .OrderBy(p => p.PostServicePackage != null && p.PostServicePackage.PriorityLevel > 0 ? p.PostServicePackage.PriorityLevel : 9999)
                    .ThenByDescending(p => p.AreaSize), // Rộng -> Hẹp

                _ => query
                    .OrderBy(p => p.PostServicePackage != null && p.PostServicePackage.PriorityLevel > 0 ? p.PostServicePackage.PriorityLevel : 9999)
                    .ThenByDescending(p => p.CreatedAt) // Mặc định: Tin VIP ưu tiên, nếu cùng VIP thì tin mới nhất lên trước
            };

            int totalItems = await query.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
            if (page > totalPages) page = totalPages;

            var results = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

            // 1. GỬI RA VIEW DANH SÁCH DỰ ÁN THỰC TẾ
            ViewBag.LatestProjects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Area)
                .Where(p => p.ApprovalStatus == "Approved" && p.IsDeleted == false)
                .OrderByDescending(p => p.PublishedAt)
                .Take(4)
                .ToListAsync();

            // 2. GỬI RA VIEW DANH SÁCH ID TIN ĐÃ LƯU ĐỂ HIỂN THỊ TIM ĐỎ
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            List<int> favoritedIds = new List<int>();
            if (int.TryParse(userIdClaim, out int userIdToken))
            {
                favoritedIds = await _context.Favorites
                    .Where(f => f.UserID == userIdToken)
                    .Select(f => f.PropertyID)
                    .ToListAsync();
            }
            ViewBag.FavoritedIds = favoritedIds;

            var subTypes = await _context.PropertyTypes.Where(t => t.ParentID != null).Select(t => new { t.TypeID, t.TypeName, t.ParentID }).ToListAsync();
            ViewBag.SubTypesJson = System.Text.Json.JsonSerializer.Serialize(subTypes);
            ViewBag.Areas = await _context.Areas.OrderBy(a => a.AreaName).ToListAsync();

            ViewBag.CurrentFilters = new { transactionType, keyword, typeId, areaId, wardId, minPrice, maxPrice, minSize, maxSize, bedrooms, bathrooms, direction, legalStatus, amenities, packageId, sortOrder };
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalItems = totalItems;

            // KIỂM TRA NẾU LÀ YÊU CẦU AJAX THÌ CHỈ TRẢ VỀ PARTIAL (HTML CỦA DANH SÁCH) MÀ KHÔNG LOAD LẠI BỘ LỌC
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
            if (!int.TryParse(userIdClaim, out int userId)) return Unauthorized();

            if (transactionStatus != "Sold" && transactionStatus != "Rented")
            {
                TempData["Error"] = "Trạng thái giao dịch không hợp lệ.";
                return RedirectToAction("MyAds");
            }

            var property = await _context.Properties.FirstOrDefaultAsync(p => p.PropertyID == id && p.UserID == userId);

            if (property != null)
            {
                property.Status = transactionStatus;
                property.SoldAt = DateTime.Now;
                property.UpdatedAt = DateTime.Now;

                await _context.SaveChangesAsync();

                string msg = transactionStatus == "Sold" ? "đã bán" : "đã cho thuê";
                TempData["Success"] = $"Chúc mừng! Đã ghi nhận bất động sản {msg} thành công.";
            }
            else
            {
                TempData["Error"] = "Không tìm thấy thông tin bất động sản.";
            }

            return RedirectToAction("MyAds");
        }

        [HttpGet]
        public async Task<IActionResult> GetAvailablePackages()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return Unauthorized();

            bool hasReceivedGift = await _context.Transactions.AnyAsync(t => t.UserID == userId && t.PaymentMethod == "System Gift");

            if (!hasReceivedGift)
            {
                var normalPackage = await _context.PostServicePackages.FirstOrDefaultAsync(p => p.PackageType == "Tin Thường" || p.Price == 0);
                if (normalPackage == null) normalPackage = await _context.PostServicePackages.OrderBy(p => p.Price).FirstOrDefaultAsync();

                if (normalPackage != null)
                {
                    for (int i = 0; i < 5; i++)
                    {
                        _context.Transactions.Add(new Transaction
                        {
                            UserID = userId,
                            PackageID = normalPackage.PackageID,
                            PropertyID = null,
                            Quantity = 1,
                            Amount = 0,
                            Type = "Tặng lượt đăng tin thường",
                            PaymentMethod = "System Gift",
                            TransactionCode = "WELCOME" + DateTime.Now.ToString("yyyyMMddHHmmss") + userId + i,
                            Status = "Success",
                            CreatedAt = DateTime.Now
                        });
                    }
                    await _context.SaveChangesAsync();
                }
            }

            var availableCredits = await _context.Transactions
                .Where(t => t.UserID == userId && t.PropertyID == null && t.Status == "Success" && t.PackageID != null)
                .GroupBy(t => t.PackageID)
                .Select(g => new {
                    PackageID = g.Key,
                    Count = g.Count()
                })
                .ToListAsync();

            var packageIds = availableCredits.Select(a => a.PackageID).ToList();
            var packages = await _context.PostServicePackages.Where(p => packageIds.Contains(p.PackageID)).ToListAsync();

            var resultData = availableCredits.Select(a => {
                var p = packages.First(pkg => pkg.PackageID == a.PackageID);
                return new
                {
                    id = p.PackageID,
                    name = p.PackageName,
                    type = p.PackageType,
                    priority = p.PriorityLevel,
                    price = p.Price,
                    availableCount = a.Count.ToString()
                };
            }).OrderBy(x => x.priority).ToList();

            return Json(new { success = true, data = resultData });
        }

        [HttpGet]
        public async Task<IActionResult> Create()
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdClaim, out int userId)) return RedirectToAction("Login", "Account");

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

            var selectedPackage = await _context.PostServicePackages.FindAsync(prop.PackageID);
            if (selectedPackage == null) return BadRequest("Gói tin không tồn tại.");

            var creditToUse = await _context.Transactions
                .Where(t => t.UserID == userId && t.PackageID == selectedPackage.PackageID && t.PropertyID == null && t.Status == "Success")
                .OrderBy(t => t.CreatedAt)
                .FirstOrDefaultAsync();

            if (creditToUse == null)
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
                prop.VipExpiryDate = DateTime.Now.AddDays(selectedPackage.DurationDays);
                TempData["Success"] = $"Tin VIP '{selectedPackage.PackageName}' của bạn đã được hệ thống duyệt tự động và hiển thị ngay lập tức!";
            }
            else
            {
                prop.Status = "Pending";
                prop.IsAutoApproved = false;
                TempData["Success"] = $"Đăng tin thành công với gói '{selectedPackage.PackageName}'. Vui lòng chờ quản trị viên kiểm duyệt để được hiển thị.";
            }

            using var dbTransaction = await _context.Database.BeginTransactionAsync();
            try
            {
                _context.Properties.Add(prop);
                await _context.SaveChangesAsync();

                creditToUse.PropertyID = prop.PropertyID;
                _context.Update(creditToUse);
                await _context.SaveChangesAsync();

                var features = new List<PropertyFeature>();
                string bedrooms = Request.Form["Bedrooms"], bathrooms = Request.Form["Bathrooms"],
                       direction = Request.Form["Direction"], legalStatus = Request.Form["LegalStatus"];
                var amenities = Request.Form["Amenities"].ToList();

                if (!string.IsNullOrEmpty(bedrooms)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureGroup = "Cấu trúc", FeatureName = "Phòng ngủ", FeatureValue = bedrooms });
                if (!string.IsNullOrEmpty(bathrooms)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureGroup = "Cấu trúc", FeatureName = "Phòng vệ sinh", FeatureValue = bathrooms });
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

            var property = await _context.Properties
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PropertyType)
                .FirstOrDefaultAsync(p => p.PropertyID == id && p.UserID == userId && p.IsDeleted == false);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy tin đăng hoặc bạn không có quyền sửa.";
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

            var existingProp = await _context.Properties.FirstOrDefaultAsync(p => p.PropertyID == id && p.UserID == userId);
            if (existingProp == null) return NotFound();

            if (!ModelState.IsValid)
            {
                prop.MainImage = existingProp.MainImage;
                prop.PropertyType = await _context.PropertyTypes.FindAsync(prop.TypeID);

                await LoadEditViewBags(existingProp);
                ViewBag.MasterFeatures = await _context.PropertyFeatures.Where(f => f.PropertyID == null).ToListAsync();
                TempData["Error"] = "Dữ liệu không hợp lệ, vui lòng kiểm tra lại.";
                return View(prop);
            }

            var currentPackage = await _context.PostServicePackages.FindAsync(existingProp.PackageID);
            var newPackage = await _context.PostServicePackages.FindAsync(prop.PackageID);

            if (currentPackage != null && newPackage != null)
            {
                if (newPackage.PriorityLevel > currentPackage.PriorityLevel)
                {
                    prop.MainImage = existingProp.MainImage;
                    prop.PropertyType = await _context.PropertyTypes.FindAsync(prop.TypeID);

                    await LoadEditViewBags(existingProp);
                    ViewBag.MasterFeatures = await _context.PropertyFeatures.Where(f => f.PropertyID == null).ToListAsync();

                    TempData["Error"] = $"Hệ thống không cho phép hạ cấp từ '{currentPackage.PackageName}' xuống '{newPackage.PackageName}'. Vui lòng giữ nguyên hoặc nâng cấp lên gói cao hơn!";
                    return View(prop);
                }
            }

            bool isChangingPackage = (existingProp.PackageID != prop.PackageID);
            bool isResubmittingRejected = (existingProp.Status == "Rejected");

            if (isChangingPackage || isResubmittingRejected)
            {
                var newCredit = await _context.Transactions.FirstOrDefaultAsync(t =>
                    t.UserID == userId && t.PackageID == prop.PackageID && t.PropertyID == null && t.Status == "Success");

                if (newCredit == null)
                {
                    prop.MainImage = existingProp.MainImage;
                    prop.PropertyType = await _context.PropertyTypes.FindAsync(prop.TypeID);

                    await LoadEditViewBags(existingProp);
                    ViewBag.MasterFeatures = await _context.PropertyFeatures.Where(f => f.PropertyID == null).ToListAsync();
                    TempData["Error"] = isResubmittingRejected
                        ? "Tin bị từ chối đã được hoàn lượt về ví. Vui lòng chọn lại gói tin trong ví để nộp lại!"
                        : $"Ví của bạn không đủ lượt đăng cho gói '{newPackage?.PackageName}'. Vui lòng mua thêm gói.";
                    return View(prop);
                }

                newCredit.PropertyID = existingProp.PropertyID;
                _context.Update(newCredit);
                existingProp.PackageID = prop.PackageID;
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
            bool isDiamondStatus = packageToApplyStatus != null &&
                                   packageToApplyStatus.PackageType.Contains("Kim Cương", StringComparison.OrdinalIgnoreCase);

            if (isDiamondStatus)
            {
                existingProp.Status = "Approved";
                existingProp.IsAutoApproved = true;
                existingProp.ApprovedAt = DateTime.Now;

                if (isChangingPackage)
                {
                    existingProp.VipExpiryDate = DateTime.Now.AddDays(packageToApplyStatus.DurationDays);
                }

                TempData["Success"] = "Bạn đang sử dụng gói Kim Cương, tin VIP đã được tự động duyệt lại thành công.";
            }
            else
            {
                existingProp.Status = "Pending";
                existingProp.IsAutoApproved = false;
                TempData["Success"] = "Đã cập nhật nội dung. Tin đăng của bạn đã được chuyển sang trạng thái CHỜ DUYỆT để Admin kiểm tra.";
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

            if (!string.IsNullOrEmpty(bds_bedrooms)) _context.PropertyFeatures.Add(new PropertyFeature { PropertyID = id, FeatureGroup = "Cấu trúc", FeatureName = "Phòng ngủ", FeatureValue = bds_bedrooms });
            if (!string.IsNullOrEmpty(bds_bathrooms)) _context.PropertyFeatures.Add(new PropertyFeature { PropertyID = id, FeatureGroup = "Cấu trúc", FeatureName = "Phòng vệ sinh", FeatureValue = bds_bathrooms });
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
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMyAd(int id)
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var property = await _context.Properties.FirstOrDefaultAsync(p => p.PropertyID == id && p.UserID == userId);

            if (property != null)
            {
                property.IsDeleted = true;
                property.Status = "Deleted";
                property.UpdatedAt = DateTime.Now;

                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã xóa tin đăng thành công!";
            }
            return RedirectToAction("MyAds");
        }

        [AllowAnonymous]
        [Route("Property/Details/{id}")]
        public async Task<IActionResult> Details(int id)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int.TryParse(userIdClaim, out int currentUserId);
            bool isAdminOrStaff = User.IsInRole("Admin") || User.IsInRole("Staff");

            var property = await _context.Properties
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.User)
                .Include(p => p.PostServicePackage)
                .Include(p => p.Project)
                .FirstOrDefaultAsync(p => p.PropertyID == id && p.IsDeleted == false);

            if (property == null) return RedirectToAction("Search");

            if (property.Status != "Approved" && !isAdminOrStaff && property.UserID != currentUserId)
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
        public async Task<IActionResult> SubmitReport([FromForm] int propertyId, [FromForm] string reason, [FromForm] string description)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return Json(new { success = false, message = "Bạn cần đăng nhập." });

            var property = await _context.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.PropertyID == propertyId);
            if (property == null || property.Status != "Approved") return Json(new { success = false, message = "Hành động bị từ chối. Tin đăng chưa được duyệt." });

            var existingReport = await _context.PropertyReports.FirstOrDefaultAsync(r => r.PropertyID == propertyId && r.ReportedBy == userId && r.Status == "Pending");
            if (existingReport != null) return Json(new { success = false, message = "Bạn đã báo cáo tin này rồi." });

            _context.PropertyReports.Add(new PropertyReport { PropertyID = propertyId, ReportedBy = userId, Reason = reason, Description = description, Status = "Pending", CreatedAt = DateTime.Now });
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Gửi báo cáo thành công!" });
        }

        [HttpPost]
        public async Task<IActionResult> BookAppointment([FromForm] int propertyId, [FromForm] string customerName, [FromForm] string customerPhone, [FromForm] string meetingLocation, [FromForm] DateTime appointmentDate, [FromForm] string note)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return Json(new { success = false, message = "Vui lòng đăng nhập." });

            if (string.IsNullOrWhiteSpace(customerPhone) || customerPhone.Trim().Length != 10)
                return Json(new { success = false, message = "Số điện thoại không hợp lệ. Vui lòng nhập đúng 10 số." });

            var property = await _context.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.PropertyID == propertyId);
            if (property == null || property.Status != "Approved")
                return Json(new { success = false, message = "Bất động sản này đang chờ duyệt hoặc đã bị ẩn." });

            // 1. Lưu lịch hẹn
            _context.Appointments.Add(new Appointment
            {
                PropertyID = propertyId,
                BuyerID = userId,
                SellerID = property.UserID,
                CustomerName = customerName.Trim(),
                CustomerPhone = customerPhone.Trim(),
                MeetingLocation = meetingLocation?.Trim(),
                AppointmentDate = appointmentDate,
                Note = note ?? "",
                Status = "Pending",
                CreatedAt = DateTime.Now
            });

            // 2. TẠO THÔNG BÁO CHO CHỦ NHÀ
            _context.Notifications.Add(new Notification
            {
                UserID = property.UserID,
                Title = "Lịch hẹn xem nhà mới",
                Content = $"Khách hàng {customerName} ({customerPhone}) vừa đặt lịch hẹn xem bất động sản '{property.Title}' vào lúc {appointmentDate:HH:mm dd/MM/yyyy}.",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Đã gửi yêu cầu hẹn gặp thành công!" });
        }

        [HttpPost]
        [AllowAnonymous]
        public async Task<IActionResult> SubmitConsultation([FromForm] int propertyId, [FromForm] string fullName, [FromForm] string phone, [FromForm] string email, [FromForm] string note)
        {
            if (string.IsNullOrWhiteSpace(phone) || phone.Trim().Length != 10)
                return Json(new { success = false, message = "Số điện thoại không hợp lệ. Vui lòng nhập đúng 10 số." });

            var property = await _context.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.PropertyID == propertyId);
            if (property == null || property.Status != "Approved")
                return Json(new { success = false, message = "Bất động sản này không khả dụng." });

            int? senderId = null;
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrEmpty(userIdStr) && int.TryParse(userIdStr, out int parsedId))
            {
                senderId = parsedId;
            }

            // 1. Lưu yêu cầu tư vấn
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

            // 2. TẠO THÔNG BÁO CHO CHỦ NHÀ
            _context.Notifications.Add(new Notification
            {
                UserID = property.UserID,
                Title = "Yêu cầu tư vấn mới",
                Content = $"Khách hàng {fullName} ({phone}) đang quan tâm và cần tư vấn về bất động sản '{property.Title}'.",
                ActionUrl = "/Consultations/Index",
                ActionText = "Xem chi tiết",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Đã gửi thông tin tư vấn!" });
        }

        [HttpPost]
        public async Task<IActionResult> SubmitComment([FromForm] int propertyId, [FromForm] string content)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return Json(new { success = false, message = "Vui lòng đăng nhập." });
            if (string.IsNullOrWhiteSpace(content)) return Json(new { success = false, message = "Nội dung trống." });

            var property = await _context.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.PropertyID == propertyId);
            if (property == null || property.Status != "Approved") return Json(new { success = false, message = "Không thể bình luận vào tin chưa được duyệt." });

            // 1. Lưu bình luận
            _context.Comments.Add(new Comment { PropertyID = propertyId, UserID = userId, Content = content, CreatedAt = DateTime.Now, IsHidden = true });

            // 2. TẠO THÔNG BÁO CHO CHỦ NHÀ
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

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Bình luận đã gửi." });
        }
        [HttpPost]
        public async Task<IActionResult> SendMessage([FromForm] int receiverId, [FromForm] int propertyId, [FromForm] string messageContent)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int senderId)) return Json(new { success = false, message = "Vui lòng đăng nhập." });
            if (senderId == receiverId) return Json(new { success = false, message = "Không thể tự nhắn cho mình." });

            var property = await _context.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.PropertyID == propertyId);
            if (property == null || property.Status != "Approved") return Json(new { success = false, message = "Tin đăng chưa duyệt, không thể chat." });

            _context.UserMessages.Add(new UserMessage { SenderID = senderId, ReceiverID = receiverId, PropertyID = propertyId, MessageContent = messageContent, IsRead = false, CreatedAt = DateTime.Now });
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Tin nhắn đã gửi!" });
        }
    }
}