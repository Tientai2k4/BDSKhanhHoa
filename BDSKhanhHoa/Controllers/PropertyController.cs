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

        // ==========================================
        // 0. TRANG INDEX (Xem toàn bộ tin Bán & Cho thuê)
        // ==========================================
        [AllowAnonymous]
        public IActionResult Index()
        {
            return RedirectToAction("Search");
        }

        [AllowAnonymous]
        [Route("Property/Search")]
        public async Task<IActionResult> Search(
                    string? transactionType = null, string? keyword = null, int? typeId = null,
                    int? areaId = null, int? wardId = null, decimal? minPrice = null,
                    decimal? maxPrice = null, decimal? minSize = null, decimal? maxSize = null,
                    string? bedrooms = null, string? bathrooms = null, string? direction = null,
                    string? legalStatus = null, string[]? amenities = null, int? packageId = null,
                    string? sortOrder = null, int page = 1)
        {
            int pageSize = 12;
            page = Math.Max(1, page); // Bảo vệ phân trang không bị âm

            if (string.IsNullOrEmpty(transactionType)) transactionType = "buy";
            keyword = keyword?.Trim().ToLower(); // Chuẩn hóa từ khóa

            // 1. Dùng AsNoTracking() tăng 40% tốc độ truy xuất do đây là thao tác Read-Only
            var query = _context.Properties
                .AsNoTracking()
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.Status == "Approved" && p.IsDeleted == false)
                .AsQueryable();

            // 2. Lọc cơ bản
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

            // Tìm kiếm Text tương đối
            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(p => p.Title.ToLower().Contains(keyword) || p.AddressDetail.ToLower().Contains(keyword));
            }

            // 3. TỐI ƯU HÓA: Lọc Feature bằng Subquery (EXISTS) thay vì nạp ID vào RAM
            if (!string.IsNullOrEmpty(direction))
            {
                query = query.Where(p => _context.PropertyFeatures.Any(f => f.PropertyID == p.PropertyID && f.FeatureName == "Hướng nhà" && f.FeatureValue == direction));
            }

            if (!string.IsNullOrEmpty(legalStatus))
            {
                query = query.Where(p => _context.PropertyFeatures.Any(f => f.PropertyID == p.PropertyID && f.FeatureName == "Pháp lý" && f.FeatureValue == legalStatus));
            }

            if (!string.IsNullOrEmpty(bedrooms))
            {
                if (bedrooms == "5") // Xử lý logic 5+
                    query = query.Where(p => _context.PropertyFeatures.Any(f => f.PropertyID == p.PropertyID && f.FeatureName == "Phòng ngủ" && Convert.ToInt32(f.FeatureValue) >= 5));
                else
                    query = query.Where(p => _context.PropertyFeatures.Any(f => f.PropertyID == p.PropertyID && f.FeatureName == "Phòng ngủ" && f.FeatureValue == bedrooms));
            }

            if (!string.IsNullOrEmpty(bathrooms))
            {
                if (bathrooms == "4") // Xử lý logic 4+
                    query = query.Where(p => _context.PropertyFeatures.Any(f => f.PropertyID == p.PropertyID && f.FeatureName == "Phòng vệ sinh" && Convert.ToInt32(f.FeatureValue) >= 4));
                else
                    query = query.Where(p => _context.PropertyFeatures.Any(f => f.PropertyID == p.PropertyID && f.FeatureName == "Phòng vệ sinh" && f.FeatureValue == bathrooms));
            }

            // 4. Lọc Tiện ích (Logic chuẩn: Tìm kiếm AND - Phải thỏa mãn toàn bộ tiện ích người dùng chọn)
            if (amenities != null && amenities.Any())
            {
                foreach (var am in amenities)
                {
                    query = query.Where(p => _context.PropertyFeatures.Any(f => f.PropertyID == p.PropertyID && f.FeatureName == "Tiện ích" && f.FeatureValue.Contains(am)));
                }
            }

            // 5. Thuật toán sắp xếp (Ưu tiên VIP trước, sau đó mới đến các tiêu chí khác)
            query = sortOrder switch
            {
                "price_asc" => query.OrderByDescending(p => p.PackageID).ThenBy(p => p.Price),
                "price_desc" => query.OrderByDescending(p => p.PackageID).ThenByDescending(p => p.Price),
                "area_desc" => query.OrderByDescending(p => p.PackageID).ThenByDescending(p => p.AreaSize),
                _ => query.OrderByDescending(p => p.PackageID).ThenByDescending(p => p.CreatedAt)
            };

            // 6. Tính toán phân trang an toàn
            int totalItems = await query.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
            if (page > totalPages) page = totalPages; // Sửa lỗi vượt quá số trang

            var results = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

            // 7. Load dữ liệu phụ cho View
            var subTypes = await _context.PropertyTypes.Where(t => t.ParentID != null).Select(t => new { t.TypeID, t.TypeName, t.ParentID }).ToListAsync();
            ViewBag.SubTypesJson = System.Text.Json.JsonSerializer.Serialize(subTypes);
            ViewBag.Areas = await _context.Areas.OrderBy(a => a.AreaName).ToListAsync();

            ViewBag.CurrentFilters = new { transactionType, keyword, typeId, areaId, wardId, minPrice, maxPrice, minSize, maxSize, bedrooms, bathrooms, direction, legalStatus, amenities, packageId, sortOrder };
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalItems = totalItems;

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

        // ==========================================
        // 1. DANH SÁCH TIN CỦA TÔI
        // ==========================================
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
                .Where(p => p.UserID == userId && p.IsDeleted == false)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            return View(myProperties);
        }

        // =========================================================
        // API LẤY DANH SÁCH GÓI TIN ĐANG SỞ HỮU TRONG VÍ (AJAX)
        // =========================================================
        [HttpGet]
        public async Task<IActionResult> GetAvailablePackages()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return Unauthorized();

            // FIX LỖI VÔ HẠN GÓI: Dùng chuỗi tiếng Anh không dấu "System Gift" để check, tuyệt đối không bao giờ lỗi Unicode
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
                            PropertyID = null, // Vẫn bằng null để tính là 1 lượt chưa dùng
                            Quantity = 1,      // Đồng bộ với cấu trúc DB mới
                            Amount = 0,
                            Type = "Tặng lượt đăng tin thường",
                            PaymentMethod = "System Gift", // Cột này dùng làm "Chìa khóa" nhận diện an toàn
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
            }).OrderByDescending(x => x.priority).ToList();

            return Json(new { success = true, data = resultData });
        }

        // ==========================================
        // 2. ĐĂNG TIN MỚI (CREATE)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            ViewBag.ParentTypes = await _context.PropertyTypes.Where(t => t.ParentID == null).ToListAsync();
            var subTypes = await _context.PropertyTypes.Where(t => t.ParentID != null).Select(t => new { t.TypeID, t.TypeName, t.ParentID }).ToListAsync();
            ViewBag.SubTypesJson = System.Text.Json.JsonSerializer.Serialize(subTypes);
            ViewBag.Areas = new SelectList(await _context.Areas.OrderBy(a => a.AreaName).ToListAsync(), "AreaID", "AreaName");

            // Kéo danh sách Lớp Cha - Con từ Admin tạo truyền ra giao diện
            ViewBag.MasterFeatures = await _context.PropertyFeatures
                                                   .Where(f => f.PropertyID == null)
                                                   .ToListAsync();

            return View(new Property());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Property prop, IFormFile MainImageFile, List<IFormFile> AdditionalImages)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return RedirectToAction("Login", "Account");

            // 1. Kiểm tra Validate của Model (Tránh crash web)
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

            // Xử lý Upload Ảnh Đại Diện
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
            prop.Status = "Pending";
            prop.Views = 0;
            prop.IsDeleted = false;
            prop.CreatedAt = DateTime.Now;
            prop.UpdatedAt = DateTime.Now;
            prop.VipExpiryDate = DateTime.Now.AddDays(selectedPackage.DurationDays);

            // 2. SỬ DỤNG TRANSACTION ĐỂ ĐẢM BẢO TOÀN VẸN DỮ LIỆU
            using var dbTransaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Bước A: Lưu bài đăng
                _context.Properties.Add(prop);
                await _context.SaveChangesAsync();

                // Bước B: Cập nhật Transaction (Trừ gói tin)
                creditToUse.PropertyID = prop.PropertyID;
                _context.Update(creditToUse);
                await _context.SaveChangesAsync();

                // Bước C: Xử lý Tiện ích (Đã fix lỗi Null FeatureGroup)
                var features = new List<PropertyFeature>();
                string bedrooms = Request.Form["Bedrooms"], bathrooms = Request.Form["Bathrooms"], direction = Request.Form["Direction"], legalStatus = Request.Form["LegalStatus"];
                var amenities = Request.Form["Amenities"].ToList();

                if (!string.IsNullOrEmpty(bedrooms)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureGroup = "Cấu trúc", FeatureName = "Phòng ngủ", FeatureValue = bedrooms });
                if (!string.IsNullOrEmpty(bathrooms)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureGroup = "Cấu trúc", FeatureName = "Phòng vệ sinh", FeatureValue = bathrooms });
                if (!string.IsNullOrEmpty(direction)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureGroup = "Hướng nhà", FeatureName = "Hướng nhà", FeatureValue = direction });
                if (!string.IsNullOrEmpty(legalStatus)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureGroup = "Pháp lý", FeatureName = "Pháp lý", FeatureValue = legalStatus });
                if (amenities.Any()) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureGroup = "Tiện ích", FeatureName = "Tiện ích", FeatureValue = string.Join(", ", amenities) });

                if (features.Any()) { _context.PropertyFeatures.AddRange(features); await _context.SaveChangesAsync(); }

                // Bước D: Xử lý Ảnh Phụ
                if (AdditionalImages != null && AdditionalImages.Any())
                {
                    string galleryDir = Path.Combine(_hostEnvironment.WebRootPath, "uploads/properties/gallery");
                    if (!Directory.Exists(galleryDir)) Directory.CreateDirectory(galleryDir);

                    var propertyImages = new List<PropertyImage>();
                    foreach (var file in AdditionalImages.Take(10))
                    {
                        if (file.Length > 0)
                        {
                            string fileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);
                            using (var stream = new FileStream(Path.Combine(galleryDir, fileName), FileMode.Create)) { await file.CopyToAsync(stream); }
                            propertyImages.Add(new PropertyImage { PropertyID = prop.PropertyID, ImageURL = "/uploads/properties/gallery/" + fileName, IsMain = false });
                        }
                    }
                    if (propertyImages.Any()) { _context.PropertyImages.AddRange(propertyImages); await _context.SaveChangesAsync(); }
                }

                // Nếu mọi thứ thành công, Commit lưu vào DB thực
                await dbTransaction.CommitAsync();
            }
            catch (Exception)
            {
                // Nếu rớt mạng hoặc có lỗi, Rollback không trừ lượt, không lưu rác
                await dbTransaction.RollbackAsync();
                TempData["Error"] = "Đã xảy ra lỗi hệ thống khi đăng tin. Vui lòng thử lại!";
                return RedirectToAction("Create");
            }

            TempData["Success"] = "Đăng tin thành công! Vui lòng chờ hệ thống kiểm duyệt.";
            return RedirectToAction("MyAds");
        }

        // ==========================================
        // 3. SỬA TIN ĐĂNG (EDIT)
        // ==========================================
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

            // Kéo danh sách Lớp Cha - Con (Tiện ích, Hướng nhà...) do Admin quản lý truyền ra View
            ViewBag.MasterFeatures = await _context.PropertyFeatures
                .Where(f => f.PropertyID == null)
                .ToListAsync();

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
                await LoadEditViewBags(existingProp);
                ViewBag.MasterFeatures = await _context.PropertyFeatures.Where(f => f.PropertyID == null).ToListAsync();
                TempData["Error"] = "Vui lòng kiểm tra lại các thông tin bắt buộc.";
                return View(prop);
            }

            // 1. Xử lý Logic Gói Tin (Không hoàn trả gói cũ)
            if (existingProp.PackageID != prop.PackageID)
            {
                var newCredit = await _context.Transactions.FirstOrDefaultAsync(t =>
                    t.UserID == userId && t.PackageID == prop.PackageID && t.PropertyID == null && t.Status == "Success");

                if (newCredit == null)
                {
                    await LoadEditViewBags(existingProp);
                    ViewBag.MasterFeatures = await _context.PropertyFeatures.Where(f => f.PropertyID == null).ToListAsync();
                    TempData["Error"] = "Ví của bạn không đủ lượt cho gói tin này. Vui lòng mua thêm!";
                    return View(prop);
                }

                // Tiêu hao gói mới (Gói cũ vẫn giữ nguyên trạng thái đã sử dụng trong DB lịch sử)
                newCredit.PropertyID = existingProp.PropertyID;
                _context.Update(newCredit);

                var packageInfo = await _context.PostServicePackages.FindAsync(prop.PackageID);
                existingProp.VipExpiryDate = DateTime.Now.AddDays(packageInfo.DurationDays);
                existingProp.PackageID = prop.PackageID;
            }

            // 2. Cập nhật thông tin cơ bản
            existingProp.Title = prop.Title;
            existingProp.Description = prop.Description;
            existingProp.Price = prop.Price;
            existingProp.AreaSize = prop.AreaSize;
            existingProp.AddressDetail = prop.AddressDetail;
            existingProp.TypeID = prop.TypeID;
            existingProp.WardID = prop.WardID;
            existingProp.Status = "Pending"; // Đưa về chờ duyệt lại
            existingProp.UpdatedAt = DateTime.Now;

            // 3. Xử lý Ảnh Đại Diện (Xóa file vật lý cũ nếu có ảnh mới)
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

            // 4. Xóa ảnh phụ cũ theo yêu cầu từ giao diện
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

            // 5. Thêm ảnh phụ mới
            if (AdditionalImages != null && AdditionalImages.Any())
            {
                string galleryDir = Path.Combine(_hostEnvironment.WebRootPath, "uploads/properties/gallery");
                if (!Directory.Exists(galleryDir)) Directory.CreateDirectory(galleryDir);

                var propertyImages = new List<PropertyImage>();
                foreach (var file in AdditionalImages.Take(10)) // Limit
                {
                    if (file.Length > 0)
                    {
                        string fileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);
                        using (var stream = new FileStream(Path.Combine(galleryDir, fileName), FileMode.Create)) { await file.CopyToAsync(stream); }
                        propertyImages.Add(new PropertyImage { PropertyID = id, ImageURL = "/uploads/properties/gallery/" + fileName, IsMain = false });
                    }
                }
                if (propertyImages.Any()) { _context.PropertyImages.AddRange(propertyImages); }
            }

            // 6. Cập nhật Features (Tiện ích, phòng ngủ...) (Đã fix lỗi Null FeatureGroup)
            var oldFeatures = await _context.PropertyFeatures.Where(f => f.PropertyID == id).ToListAsync();
            _context.PropertyFeatures.RemoveRange(oldFeatures);

            var newFeatures = new List<PropertyFeature>();
            string bedrooms = Request.Form["Bedrooms"], bathrooms = Request.Form["Bathrooms"], direction = Request.Form["Direction"], legalStatus = Request.Form["LegalStatus"];
            var amenities = Request.Form["Amenities"].ToList();

            if (!string.IsNullOrEmpty(bedrooms)) newFeatures.Add(new PropertyFeature { PropertyID = id, FeatureGroup = "Cấu trúc", FeatureName = "Phòng ngủ", FeatureValue = bedrooms });
            if (!string.IsNullOrEmpty(bathrooms)) newFeatures.Add(new PropertyFeature { PropertyID = id, FeatureGroup = "Cấu trúc", FeatureName = "Phòng vệ sinh", FeatureValue = bathrooms });
            if (!string.IsNullOrEmpty(direction)) newFeatures.Add(new PropertyFeature { PropertyID = id, FeatureGroup = "Hướng nhà", FeatureName = "Hướng nhà", FeatureValue = direction });
            if (!string.IsNullOrEmpty(legalStatus)) newFeatures.Add(new PropertyFeature { PropertyID = id, FeatureGroup = "Pháp lý", FeatureName = "Pháp lý", FeatureValue = legalStatus });
            if (amenities.Any()) newFeatures.Add(new PropertyFeature { PropertyID = id, FeatureGroup = "Tiện ích", FeatureName = "Tiện ích", FeatureValue = string.Join(", ", amenities) });

            if (newFeatures.Any()) { _context.PropertyFeatures.AddRange(newFeatures); }

            await _context.SaveChangesAsync();
            TempData["Success"] = "Đã cập nhật tin đăng. Vui lòng chờ hệ thống kiểm duyệt lại!";
            return RedirectToAction("MyAds");
        }

        // Hàm hỗ trợ Load ViewBag để tái sử dụng, tránh lặp code và tránh crash khi ModelState fail
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

        // ==========================================
        // 4. XÓA TIN DÀNH CHO MEMBER
        // ==========================================
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

                // Giải phóng gói tin để rác không tồn đọng (Giao dịch vẫn giữ lại lịch sử)
                // Hoặc có thể không cần thiết lập gì thêm tùy logic kế toán.

                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã xóa tin đăng thành công!";
            }
            return RedirectToAction("MyAds");
        }
        // ==========================================
        // 5. TRANG CHI TIẾT VÀ CÁC API KHÁC
        // ==========================================
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
                // Đã thêm Include Project để lấy dữ liệu Tên dự án
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
            if (currentUserId > 0)
            {
                isFavorited = await _context.Favorites.AnyAsync(f => f.PropertyID == id && f.UserID == currentUserId);
            }
            ViewBag.IsFavorited = isFavorited;

            return View(property);
        }

        [HttpPost]
        public async Task<IActionResult> SubmitReport([FromForm] int propertyId, [FromForm] string reason, [FromForm] string description)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId))
                return Json(new { success = false, message = "Bạn cần đăng nhập để báo cáo tin này." });

            var existingReport = await _context.PropertyReports.FirstOrDefaultAsync(r => r.PropertyID == propertyId && r.ReportedBy == userId && r.Status == "Pending");
            if (existingReport != null) return Json(new { success = false, message = "Bạn đã báo cáo tin này rồi. Hệ thống đang xem xét!" });

            _context.PropertyReports.Add(new PropertyReport { PropertyID = propertyId, ReportedBy = userId, Reason = reason, Description = description, Status = "Pending", CreatedAt = DateTime.Now });
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Gửi báo cáo thành công! Cảm ơn bạn đã đóng góp." });
        }

        [HttpPost]
        public async Task<IActionResult> BookAppointment([FromForm] int propertyId, [FromForm] DateTime appointmentDate, [FromForm] string note)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return Json(new { success = false, message = "Vui lòng đăng nhập để đặt lịch." });

            var property = await _context.Properties.FindAsync(propertyId);
            if (property == null) return Json(new { success = false, message = "Lỗi dữ liệu." });

            _context.Appointments.Add(new Appointment { PropertyID = propertyId, BuyerID = userId, SellerID = property.UserID, AppointmentDate = appointmentDate, Note = note ?? "", Status = "Pending", CreatedAt = DateTime.Now });
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Đã gửi yêu cầu hẹn gặp thành công!" });
        }

        [HttpPost]
        [AllowAnonymous]
        public async Task<IActionResult> SubmitConsultation([FromForm] int propertyId, [FromForm] string fullName, [FromForm] string phone, [FromForm] string email, [FromForm] string note)
        {
            _context.Consultations.Add(new Consultation { PropertyID = propertyId, FullName = fullName, Phone = phone, Email = email ?? "", Note = note ?? "", Status = "New", CreatedAt = DateTime.Now });
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Thông tin đã gửi. Chuyên viên tư vấn sẽ sớm gọi lại!" });
        }

        [HttpPost]
        public async Task<IActionResult> SubmitComment([FromForm] int propertyId, [FromForm] string content)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return Json(new { success = false, message = "Vui lòng đăng nhập để bình luận." });
            if (string.IsNullOrWhiteSpace(content)) return Json(new { success = false, message = "Nội dung không được để trống." });

            _context.Comments.Add(new Comment { PropertyID = propertyId, UserID = userId, Content = content, CreatedAt = DateTime.Now, IsHidden = true });
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Bình luận của bạn đã gửi và đang chờ Quản trị viên phê duyệt!" });
        }

        // ==========================================
        // 6. API GỬI TIN NHẮN NỘI BỘ (CHAT TRỰC TIẾP)
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> SendMessage([FromForm] int receiverId, [FromForm] int propertyId, [FromForm] string messageContent)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int senderId))
                return Json(new { success = false, message = "Vui lòng đăng nhập để sử dụng tính năng chat nội bộ." });

            if (string.IsNullOrWhiteSpace(messageContent))
                return Json(new { success = false, message = "Nội dung tin nhắn không được để trống." });

            if (senderId == receiverId)
                return Json(new { success = false, message = "Bạn không thể tự gửi tin nhắn cho chính mình." });

            _context.UserMessages.Add(new UserMessage
            {
                SenderID = senderId,
                ReceiverID = receiverId,
                PropertyID = propertyId,
                MessageContent = messageContent,
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Tin nhắn đã được gửi tới người đăng! Vui lòng kiểm tra hộp thư." });
        }
    }
}