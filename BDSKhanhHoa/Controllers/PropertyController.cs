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
            string transactionType,
            string keyword,
            int? typeId,
            int? areaId,
            int? wardId,
            decimal? minPrice,
            decimal? maxPrice,
            decimal? minSize,
            decimal? maxSize,
            string bedrooms,
            string bathrooms,
            string direction,
            string legalStatus,
            string[] amenities,
            int? packageId,
            string sortOrder,
            int page = 1)
        {
            int pageSize = 12;
            if (string.IsNullOrEmpty(transactionType)) transactionType = "buy";

            var query = _context.Properties
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.Status == "Approved" && p.IsDeleted == false)
                .AsQueryable();

            if (transactionType == "buy") query = query.Where(p => p.PropertyType.ParentID == 1 || p.TypeID == 1);
            else if (transactionType == "rent") query = query.Where(p => p.PropertyType.ParentID == 2 || p.TypeID == 2);

            if (!string.IsNullOrEmpty(keyword)) query = query.Where(p => p.Title.Contains(keyword) || p.AddressDetail.Contains(keyword));
            if (typeId.HasValue) query = query.Where(p => p.TypeID == typeId);
            if (areaId.HasValue) query = query.Where(p => p.Ward.AreaID == areaId);
            if (wardId.HasValue) query = query.Where(p => p.WardID == wardId);
            if (packageId.HasValue) query = query.Where(p => p.PackageID == packageId);

            if (minPrice.HasValue) query = query.Where(p => p.Price >= minPrice.Value * 1000000);
            if (maxPrice.HasValue) query = query.Where(p => p.Price <= maxPrice.Value * 1000000);
            if (minSize.HasValue) query = query.Where(p => p.AreaSize >= minSize.Value);
            if (maxSize.HasValue) query = query.Where(p => p.AreaSize <= maxSize.Value);

            if (!string.IsNullOrEmpty(direction))
            {
                var dirIds = await _context.PropertyFeatures.Where(f => f.FeatureName == "Hướng nhà" && f.FeatureValue == direction).Select(f => f.PropertyID).ToListAsync();
                query = query.Where(p => dirIds.Contains(p.PropertyID));
            }
            if (!string.IsNullOrEmpty(legalStatus))
            {
                var legalIds = await _context.PropertyFeatures.Where(f => f.FeatureName == "Pháp lý" && f.FeatureValue == legalStatus).Select(f => f.PropertyID).ToListAsync();
                query = query.Where(p => legalIds.Contains(p.PropertyID));
            }
            if (amenities != null && amenities.Any())
            {
                var amenityIds = await _context.PropertyFeatures.Where(f => f.FeatureName == "Tiện ích").Where(f => amenities.Any(a => f.FeatureValue.Contains(a))).Select(f => f.PropertyID).ToListAsync();
                query = query.Where(p => amenityIds.Contains(p.PropertyID));
            }
            if (!string.IsNullOrEmpty(bedrooms))
            {
                var bedIds = await _context.PropertyFeatures.Where(f => f.FeatureName == "Phòng ngủ" && (bedrooms == "5" ? Convert.ToInt32(f.FeatureValue) >= 5 : f.FeatureValue == bedrooms)).Select(f => f.PropertyID).ToListAsync();
                query = query.Where(p => bedIds.Contains(p.PropertyID));
            }
            if (!string.IsNullOrEmpty(bathrooms))
            {
                var bathIds = await _context.PropertyFeatures.Where(f => f.FeatureName == "Phòng vệ sinh" && (bathrooms == "4" ? Convert.ToInt32(f.FeatureValue) >= 4 : f.FeatureValue == bathrooms)).Select(f => f.PropertyID).ToListAsync();
                query = query.Where(p => bathIds.Contains(p.PropertyID));
            }

            query = sortOrder switch
            {
                "price_asc" => query.OrderBy(p => p.Price),
                "price_desc" => query.OrderByDescending(p => p.Price),
                "area_desc" => query.OrderByDescending(p => p.AreaSize),
                _ => query.OrderByDescending(p => p.PackageID).ThenByDescending(p => p.CreatedAt)
            };

            int totalItems = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            var results = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

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
        public Task<IActionResult> NhaDatBan() => Search("buy", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, 1);

        [AllowAnonymous]
        [Route("BatDongSan/NhaDatChoThue")]
        public Task<IActionResult> NhaDatChoThue() => Search("rent", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, 1);

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
            ViewBag.Packages = new SelectList(await _context.PostServicePackages.OrderByDescending(p => p.PriorityLevel).ToListAsync(), "PackageID", "PackageName");
            ViewBag.Projects = await _context.Projects.Where(p => p.ApprovalStatus == "Approved" && p.IsDeleted == false).ToListAsync();

            return View(new Property());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Property prop, IFormFile MainImageFile, List<IFormFile> AdditionalImages)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdClaim, out int userId)) return RedirectToAction("Login", "Account");

            if (MainImageFile != null && MainImageFile.Length > 0)
            {
                string uploadDir = Path.Combine(_hostEnvironment.WebRootPath, "uploads/properties");
                if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

                string fileName = Guid.NewGuid().ToString() + Path.GetExtension(MainImageFile.FileName);
                string filePath = Path.Combine(uploadDir, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create)) { await MainImageFile.CopyToAsync(stream); }
                prop.MainImage = "/uploads/properties/" + fileName;
            }
            else { prop.MainImage = "/images/no-image.jpg"; }

            prop.UserID = userId;
            prop.Status = "Pending";
            prop.Views = 0;
            prop.IsDeleted = false;
            prop.CreatedAt = DateTime.Now;
            prop.UpdatedAt = DateTime.Now;

            var package = await _context.PostServicePackages.FindAsync(prop.PackageID);
            if (package != null) prop.VipExpiryDate = DateTime.Now.AddDays(package.DurationDays);

            _context.Properties.Add(prop);
            await _context.SaveChangesAsync();

            var features = new List<PropertyFeature>();
            string bedrooms = Request.Form["Bedrooms"];
            string bathrooms = Request.Form["Bathrooms"];
            string direction = Request.Form["Direction"];
            string legalStatus = Request.Form["LegalStatus"];
            var amenities = Request.Form["Amenities"].ToList();

            if (!string.IsNullOrEmpty(bedrooms)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureName = "Phòng ngủ", FeatureValue = bedrooms });
            if (!string.IsNullOrEmpty(bathrooms)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureName = "Phòng vệ sinh", FeatureValue = bathrooms });
            if (!string.IsNullOrEmpty(direction)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureName = "Hướng nhà", FeatureValue = direction });
            if (!string.IsNullOrEmpty(legalStatus)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureName = "Pháp lý", FeatureValue = legalStatus });
            if (amenities.Any()) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureName = "Tiện ích", FeatureValue = string.Join(", ", amenities) });

            if (features.Any()) { _context.PropertyFeatures.AddRange(features); await _context.SaveChangesAsync(); }

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
                .Include(p => p.Ward)
                .FirstOrDefaultAsync(p => p.PropertyID == id && p.UserID == userId && p.IsDeleted == false);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy tin đăng hoặc bạn không có quyền sửa.";
                return RedirectToAction("MyAds");
            }

            ViewBag.ParentTypes = await _context.PropertyTypes.Where(t => t.ParentID == null).ToListAsync();
            var subTypes = await _context.PropertyTypes.Where(t => t.ParentID != null).Select(t => new { t.TypeID, t.TypeName, t.ParentID }).ToListAsync();
            ViewBag.SubTypesJson = System.Text.Json.JsonSerializer.Serialize(subTypes);

            ViewBag.Areas = new SelectList(await _context.Areas.OrderBy(a => a.AreaName).ToListAsync(), "AreaID", "AreaName", property.Ward?.AreaID);
            ViewBag.Wards = new SelectList(await _context.Wards.Where(w => w.AreaID == property.Ward.AreaID).ToListAsync(), "WardID", "WardName", property.WardID);
            ViewBag.Packages = new SelectList(await _context.PostServicePackages.OrderByDescending(p => p.PriorityLevel).ToListAsync(), "PackageID", "PackageName", property.PackageID);
            ViewBag.Projects = await _context.Projects.Where(p => p.ApprovalStatus == "Approved" && p.IsDeleted == false).ToListAsync();

            ViewBag.OldFeatures = await _context.PropertyFeatures.Where(f => f.PropertyID == id).ToListAsync();
            ViewBag.OldImages = await _context.PropertyImages.Where(i => i.PropertyID == id && i.IsMain == false).ToListAsync();

            return View(property);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, Property prop, IFormFile? MainImageFile, List<IFormFile>? AdditionalImages)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return RedirectToAction("Login", "Account");

            var existingProp = await _context.Properties.FirstOrDefaultAsync(p => p.PropertyID == id && p.UserID == userId);
            if (existingProp == null) return NotFound();

            existingProp.Title = prop.Title;
            existingProp.Description = prop.Description;
            existingProp.Price = prop.Price;
            existingProp.AreaSize = prop.AreaSize;
            existingProp.AddressDetail = prop.AddressDetail;
            existingProp.TypeID = prop.TypeID;
            existingProp.WardID = prop.WardID;
            existingProp.ProjectID = prop.ProjectID;
            existingProp.PackageID = prop.PackageID;

            existingProp.Status = "Pending";
            existingProp.UpdatedAt = DateTime.Now;

            if (MainImageFile != null && MainImageFile.Length > 0)
            {
                string uploadDir = Path.Combine(_hostEnvironment.WebRootPath, "uploads/properties");
                string fileName = Guid.NewGuid().ToString() + Path.GetExtension(MainImageFile.FileName);
                using (var stream = new FileStream(Path.Combine(uploadDir, fileName), FileMode.Create)) { await MainImageFile.CopyToAsync(stream); }
                existingProp.MainImage = "/uploads/properties/" + fileName;
            }

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
                        propertyImages.Add(new PropertyImage { PropertyID = id, ImageURL = "/uploads/properties/gallery/" + fileName, IsMain = false });
                    }
                }
                if (propertyImages.Any()) { _context.PropertyImages.AddRange(propertyImages); }
            }

            var oldFeatures = await _context.PropertyFeatures.Where(f => f.PropertyID == id).ToListAsync();
            _context.PropertyFeatures.RemoveRange(oldFeatures);

            var newFeatures = new List<PropertyFeature>();
            string bedrooms = Request.Form["Bedrooms"], bathrooms = Request.Form["Bathrooms"], direction = Request.Form["Direction"], legalStatus = Request.Form["LegalStatus"];
            var amenities = Request.Form["Amenities"].ToList();

            if (!string.IsNullOrEmpty(bedrooms)) newFeatures.Add(new PropertyFeature { PropertyID = id, FeatureName = "Phòng ngủ", FeatureValue = bedrooms });
            if (!string.IsNullOrEmpty(bathrooms)) newFeatures.Add(new PropertyFeature { PropertyID = id, FeatureName = "Phòng vệ sinh", FeatureValue = bathrooms });
            if (!string.IsNullOrEmpty(direction)) newFeatures.Add(new PropertyFeature { PropertyID = id, FeatureName = "Hướng nhà", FeatureValue = direction });
            if (!string.IsNullOrEmpty(legalStatus)) newFeatures.Add(new PropertyFeature { PropertyID = id, FeatureName = "Pháp lý", FeatureValue = legalStatus });
            if (amenities.Any()) newFeatures.Add(new PropertyFeature { PropertyID = id, FeatureName = "Tiện ích", FeatureValue = string.Join(", ", amenities) });

            if (newFeatures.Any()) { _context.PropertyFeatures.AddRange(newFeatures); }

            await _context.SaveChangesAsync();
            TempData["Success"] = "Đã cập nhật tin đăng. Vui lòng chờ Admin duyệt lại!";
            return RedirectToAction("MyAds");
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
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã xóa tin đăng thành công!";
            }
            return RedirectToAction("MyAds");
        }

        // ==========================================
        // 5. TRANG CHI TIẾT BẤT ĐỘNG SẢN (DETAILS)
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
                .FirstOrDefaultAsync(p => p.PropertyID == id && p.IsDeleted == false);

            if (property == null) return RedirectToAction("Search");

            // Chặn xem tin chưa duyệt
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

            // Load Bình luận chỉ khi nào IsHidden == false (Đã duyệt)
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

        // ==========================================
        // CÁC API AJAX CỦA TRANG DETAILS
        // ==========================================

        [HttpPost]
        public async Task<IActionResult> SubmitReport([FromForm] int propertyId, [FromForm] string reason, [FromForm] string description)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId))
                return Json(new { success = false, message = "Bạn cần đăng nhập để báo cáo tin này." });

            var existingReport = await _context.PropertyReports.FirstOrDefaultAsync(r => r.PropertyID == propertyId && r.ReportedBy == userId && r.Status == "Pending");
            if (existingReport != null) return Json(new { success = false, message = "Bạn đã báo cáo tin này rồi. Hệ thống đang xem xét!" });

            _context.PropertyReports.Add(new PropertyReport
            {
                PropertyID = propertyId,
                ReportedBy = userId,
                Reason = reason,
                Description = description,
                Status = "Pending",
                CreatedAt = DateTime.Now
            });
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Gửi báo cáo thành công! Cảm ơn bạn đã đóng góp." });
        }

        [HttpPost]
        public async Task<IActionResult> BookAppointment([FromForm] int propertyId, [FromForm] DateTime appointmentDate, [FromForm] string note)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId))
                return Json(new { success = false, message = "Vui lòng đăng nhập để đặt lịch." });

            var property = await _context.Properties.FindAsync(propertyId);
            if (property == null) return Json(new { success = false, message = "Lỗi dữ liệu." });

            _context.Appointments.Add(new Appointment
            {
                PropertyID = propertyId,
                BuyerID = userId,
                SellerID = property.UserID,
                AppointmentDate = appointmentDate,
                Note = note ?? "",
                Status = "Pending",
                CreatedAt = DateTime.Now
            });
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Đã gửi yêu cầu hẹn gặp thành công!" });
        }

        [HttpPost]
        [AllowAnonymous]
        public async Task<IActionResult> SubmitConsultation([FromForm] int propertyId, [FromForm] string fullName, [FromForm] string phone, [FromForm] string email, [FromForm] string note)
        {
            _context.Consultations.Add(new Consultation
            {
                PropertyID = propertyId,
                FullName = fullName,
                Phone = phone,
                Email = email ?? "",
                Note = note ?? "",
                Status = "New",
                CreatedAt = DateTime.Now
            });
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Thông tin đã gửi. Chuyên viên tư vấn sẽ sớm gọi lại!" });
        }

        [HttpPost]
        public async Task<IActionResult> SubmitCoBroker([FromForm] int propertyId, [FromForm] string message)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return Json(new { success = false, message = "Vui lòng đăng nhập." });

            var property = await _context.Properties.FindAsync(propertyId);
            if (property == null || property.UserID == userId) return Json(new { success = false, message = "Bạn không thể xin bán hộ tài sản của chính mình." });

            var existingReq = await _context.CoBrokerRequests.FirstOrDefaultAsync(r => r.PropertyID == propertyId && r.RequesterID == userId && r.Status == "Pending");
            if (existingReq != null) return Json(new { success = false, message = "Bạn đã gửi yêu cầu, vui lòng chờ chủ nhà duyệt." });

            _context.CoBrokerRequests.Add(new CoBrokerRequest
            {
                PropertyID = propertyId,
                OwnerID = property.UserID,
                RequesterID = userId,
                Message = message ?? "",
                Status = "Pending",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            });
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Đã gửi đề xuất hợp tác đến Chủ nhà!" });
        }

        [HttpPost]
        public async Task<IActionResult> SubmitComment([FromForm] int propertyId, [FromForm] string content)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return Json(new { success = false, message = "Vui lòng đăng nhập để bình luận." });
            if (string.IsNullOrWhiteSpace(content)) return Json(new { success = false, message = "Nội dung không được để trống." });

            // QUAN TRỌNG: Lưu bình luận với IsHidden = true để CHỜ ADMIN DUYỆT mới hiện lên
            _context.Comments.Add(new Comment
            {
                PropertyID = propertyId,
                UserID = userId,
                Content = content,
                CreatedAt = DateTime.Now,
                IsHidden = true
            });

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Bình luận của bạn đã gửi và đang chờ Quản trị viên phê duyệt!" });
        }
    }
}