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

            // 1. Xử lý lưu Ảnh đại diện (Main Image)
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

            // 2. Lưu thông tin cơ bản BĐS
            prop.UserID = userId;
            prop.Status = "Pending";
            prop.Views = 0;
            prop.IsDeleted = false;
            prop.CreatedAt = DateTime.Now;
            prop.UpdatedAt = DateTime.Now;

            var package = await _context.PostServicePackages.FindAsync(prop.PackageID);
            if (package != null) prop.VipExpiryDate = DateTime.Now.AddDays(package.DurationDays);

            _context.Properties.Add(prop);
            await _context.SaveChangesAsync(); // Sinh ra PropertyID mới

            // 3. Xử lý lưu Danh sách Tiện ích / Thuộc tính
            var features = new List<PropertyFeature>();
            string bedrooms = Request.Form["Bedrooms"], bathrooms = Request.Form["Bathrooms"];
            string direction = Request.Form["Direction"], legalStatus = Request.Form["LegalStatus"];
            var amenities = Request.Form["Amenities"].ToList();

            if (!string.IsNullOrEmpty(bedrooms)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureName = "Phòng ngủ", FeatureValue = bedrooms });
            if (!string.IsNullOrEmpty(bathrooms)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureName = "Phòng vệ sinh", FeatureValue = bathrooms });
            if (!string.IsNullOrEmpty(direction)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureName = "Hướng nhà", FeatureValue = direction });
            if (!string.IsNullOrEmpty(legalStatus)) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureName = "Pháp lý", FeatureValue = legalStatus });
            if (amenities.Any()) features.Add(new PropertyFeature { PropertyID = prop.PropertyID, FeatureName = "Tiện ích", FeatureValue = string.Join(", ", amenities) });

            if (features.Any()) { _context.PropertyFeatures.AddRange(features); await _context.SaveChangesAsync(); }

            // =================================================================
            // 4. XỬ LÝ LƯU DANH SÁCH ẢNH PHỤ (GALLERY)
            // =================================================================
            if (AdditionalImages != null && AdditionalImages.Any())
            {
                string galleryDir = Path.Combine(_hostEnvironment.WebRootPath, "uploads/properties/gallery");
                if (!Directory.Exists(galleryDir)) Directory.CreateDirectory(galleryDir);

                var propertyImages = new List<PropertyImage>();

                // Giới hạn lưu tối đa 10 ảnh phụ để tránh đầy dung lượng máy chủ
                foreach (var file in AdditionalImages.Take(10))
                {
                    if (file.Length > 0)
                    {
                        string fileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);
                        string filePath = Path.Combine(galleryDir, fileName);

                        using (var stream = new FileStream(filePath, FileMode.Create))
                        {
                            await file.CopyToAsync(stream);
                        }

                        propertyImages.Add(new PropertyImage
                        {
                            PropertyID = prop.PropertyID,
                            ImageURL = "/uploads/properties/gallery/" + fileName,
                            IsMain = false
                        });
                    }
                }

                if (propertyImages.Any())
                {
                    _context.PropertyImages.AddRange(propertyImages);
                    await _context.SaveChangesAsync();
                }
            }

            TempData["Success"] = "Đăng tin thành công! Vui lòng chờ hệ thống kiểm duyệt.";
            return RedirectToAction("MyAds");
        }
        // ==========================================
        // 3. TÌM KIẾM NÂNG CAO (SEARCH) - ĐÃ CẬP NHẬT
        // ==========================================
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

            // Lọc theo Bán / Cho thuê
            if (transactionType == "buy")
                query = query.Where(p => p.PropertyType.ParentID == 1 || p.TypeID == 1);
            else if (transactionType == "rent")
                query = query.Where(p => p.PropertyType.ParentID == 2 || p.TypeID == 2);

            if (!string.IsNullOrEmpty(keyword))
                query = query.Where(p => p.Title.Contains(keyword) || p.AddressDetail.Contains(keyword));

            if (typeId.HasValue) query = query.Where(p => p.TypeID == typeId);
            if (areaId.HasValue) query = query.Where(p => p.Ward.AreaID == areaId);
            if (wardId.HasValue) query = query.Where(p => p.WardID == wardId);
            if (packageId.HasValue) query = query.Where(p => p.PackageID == packageId);

            if (minPrice.HasValue) query = query.Where(p => p.Price >= minPrice.Value * 1000000);
            if (maxPrice.HasValue) query = query.Where(p => p.Price <= maxPrice.Value * 1000000);
            if (minSize.HasValue) query = query.Where(p => p.AreaSize >= minSize.Value);
            if (maxSize.HasValue) query = query.Where(p => p.AreaSize <= maxSize.Value);

            // ==================== 3 BỘ LỌC MỚI ====================
            if (!string.IsNullOrEmpty(direction))
            {
                var dirIds = await _context.PropertyFeatures
                    .Where(f => f.FeatureName == "Hướng nhà" && f.FeatureValue == direction)
                    .Select(f => f.PropertyID)
                    .ToListAsync();
                query = query.Where(p => dirIds.Contains(p.PropertyID));
            }

            if (!string.IsNullOrEmpty(legalStatus))
            {
                var legalIds = await _context.PropertyFeatures
                    .Where(f => f.FeatureName == "Pháp lý" && f.FeatureValue == legalStatus)
                    .Select(f => f.PropertyID)
                    .ToListAsync();
                query = query.Where(p => legalIds.Contains(p.PropertyID));
            }

            if (amenities != null && amenities.Any())
            {
                var amenityIds = await _context.PropertyFeatures
                    .Where(f => f.FeatureName == "Tiện ích")
                    .Where(f => amenities.Any(a => f.FeatureValue.Contains(a)))
                    .Select(f => f.PropertyID)
                    .ToListAsync();
                query = query.Where(p => amenityIds.Contains(p.PropertyID));
            }
            // ======================================================

            // Lọc Phòng ngủ & Phòng tắm (giữ nguyên)
            if (!string.IsNullOrEmpty(bedrooms) || !string.IsNullOrEmpty(bathrooms))
            {
                if (!string.IsNullOrEmpty(bedrooms))
                {
                    var bedIds = await _context.PropertyFeatures
                        .Where(f => f.FeatureName == "Phòng ngủ" &&
                                   (bedrooms == "5" ? Convert.ToInt32(f.FeatureValue) >= 5 : f.FeatureValue == bedrooms))
                        .Select(f => f.PropertyID)
                        .ToListAsync();
                    query = query.Where(p => bedIds.Contains(p.PropertyID));
                }
                if (!string.IsNullOrEmpty(bathrooms))
                {
                    var bathIds = await _context.PropertyFeatures
                        .Where(f => f.FeatureName == "Phòng vệ sinh" &&
                                   (bathrooms == "4" ? Convert.ToInt32(f.FeatureValue) >= 4 : f.FeatureValue == bathrooms))
                        .Select(f => f.PropertyID)
                        .ToListAsync();
                    query = query.Where(p => bathIds.Contains(p.PropertyID));
                }
            }

            // Sắp xếp
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

            // Dữ liệu cho View
            var subTypes = await _context.PropertyTypes.Where(t => t.ParentID != null)
                .Select(t => new { t.TypeID, t.TypeName, t.ParentID }).ToListAsync();

            ViewBag.SubTypesJson = System.Text.Json.JsonSerializer.Serialize(subTypes);
            ViewBag.Areas = await _context.Areas.OrderBy(a => a.AreaName).ToListAsync();

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
                amenities,           // string[]
                packageId,
                sortOrder
            };

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalItems = totalItems;

            return View("Search", results);
        }

        [AllowAnonymous][Route("BatDongSan/NhaDatBan")] public Task<IActionResult> NhaDatBan() => Search("buy", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, 1);
        [AllowAnonymous][Route("BatDongSan/NhaDatChoThue")] public Task<IActionResult> NhaDatChoThue() => Search("rent", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, 1);

        // Details giữ nguyên
        [AllowAnonymous]
        [Route("Property/Details/{id}")]
        public async Task<IActionResult> Details(int id)
        {
            // ... (giữ nguyên code Details của bạn)
            var property = await _context.Properties
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.User)
                .Include(p => p.PostServicePackage)
                .FirstOrDefaultAsync(p => p.PropertyID == id && p.Status == "Approved" && p.IsDeleted == false);

            if (property == null) return RedirectToAction("Search");

            property.Views = (property.Views ?? 0) + 1;
            await _context.SaveChangesAsync();

            ViewBag.Features = await _context.PropertyFeatures.Where(f => f.PropertyID == id).ToListAsync();
            ViewBag.SimilarProperties = await _context.Properties.Include(p => p.Ward).ThenInclude(w => w.Area).Include(p => p.PropertyType)
                .Where(p => p.Ward.AreaID == property.Ward.AreaID && p.PropertyID != id && p.Status == "Approved" && p.IsDeleted == false)
                .OrderByDescending(p => p.CreatedAt).Take(4).ToListAsync();

            ViewBag.UserProperties = await _context.Properties.Include(p => p.Ward).ThenInclude(w => w.Area).Include(p => p.PropertyType)
                .Where(p => p.UserID == property.UserID && p.PropertyID != id && p.Status == "Approved" && p.IsDeleted == false)
                .OrderByDescending(p => p.CreatedAt).Take(4).ToListAsync();

            return View(property);
        }
    }
}