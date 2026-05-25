using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin, Staff")]
    public class AdminProjectController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;

        public AdminProjectController(ApplicationDbContext context, IWebHostEnvironment env)
        {
            _context = context;
            _env = env;
        }

        private static bool IsPublicProject(Project p)
        {
            return p.ApprovalStatus == "Approved" || p.ApprovalStatus == "Đã duyệt";
        }

        private static bool IsHiddenProject(Project p)
        {
            return p.ApprovalStatus == "Hidden" ||
                   p.ApprovalStatus == "Ẩn" ||
                   p.ApprovalStatus == "NotPublic" ||
                   p.ApprovalStatus == "Không công khai";
        }

        // --- HÀM HỖ TRỢ LẤY DANH SÁCH DOANH NGHIỆP ĐÃ ĐƯỢC DUYỆT ---
        private async Task<SelectList> GetBusinessOwnersSelectList(int? selectedId = null)
        {
            var businessOwners = await _context.BusinessProfiles
                .Include(b => b.User)
                .Where(b => b.VerificationStatus == "Approved" && b.User != null && !b.User.IsDeleted)
                .OrderBy(b => b.BusinessName)
                .Select(b => new
                {
                    UserID = b.UserID,
                    DisplayName = b.BusinessName + " (Tài khoản: " + b.User.Username + ")"
                })
                .ToListAsync();

            return new SelectList(businessOwners, "UserID", "DisplayName", selectedId);
        }

        private async Task LoadAdminProjectFilterDataAsync(int? selectedAreaId = null, int? selectedWardId = null, int? selectedOwnerId = null)
        {
            ViewBag.Areas = new SelectList(
                await _context.Areas
                    .AsNoTracking()
                    .OrderBy(a => a.AreaName)
                    .ToListAsync(),
                "AreaID",
                "AreaName",
                selectedAreaId
            );

            if (selectedAreaId.HasValue && selectedAreaId.Value > 0)
            {
                ViewBag.Wards = new SelectList(
                    await _context.Wards
                        .AsNoTracking()
                        .Where(w => w.AreaID == selectedAreaId.Value)
                        .OrderBy(w => w.WardName)
                        .ToListAsync(),
                    "WardID",
                    "WardName",
                    selectedWardId
                );
            }
            else
            {
                ViewBag.Wards = new SelectList(
                    await _context.Wards
                        .AsNoTracking()
                        .OrderBy(w => w.WardName)
                        .ToListAsync(),
                    "WardID",
                    "WardName",
                    selectedWardId
                );
            }

            ViewBag.OwnersFilter = new SelectList(
                await _context.Users
                    .AsNoTracking()
                    .Where(u => !u.IsDeleted)
                    .OrderBy(u => u.FullName)
                    .Select(u => new
                    {
                        u.UserID,
                        DisplayName = !string.IsNullOrWhiteSpace(u.FullName)
                            ? u.FullName + " (" + u.Email + ")"
                            : u.Username + " (" + u.Email + ")"
                    })
                    .ToListAsync(),
                "UserID",
                "DisplayName",
                selectedOwnerId
            );
        }

        // ==========================================
        // 1. DANH SÁCH DỰ ÁN ADMIN + BỘ LỌC NHANH
        // Lưu ý: dự án do admin tạo nên không dùng duyệt/từ chối/chờ duyệt.
        // Chỉ quản lý công khai / ẩn / sửa / xóa.
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index(
            string visibility = "",
            string keyword = "",
            int? areaId = null,
            int? wardId = null,
            int? ownerId = null,
            string projectStatus = "",
            string projectType = "",
            string legal = "",
            string price = "",
            string sort = "newest",
            int page = 1)
        {
            const int pageSize = 12;

            var query = _context.Projects
                .AsNoTracking()
                .Include(p => p.Owner)
                .Include(p => p.Area)
                .Include(p => p.Ward)
                .Where(p => p.IsDeleted == false)
                .AsQueryable();

            visibility = (visibility ?? string.Empty).Trim().ToLowerInvariant();

            if (visibility == "public")
            {
                query = query.Where(p => p.ApprovalStatus == "Approved" || p.ApprovalStatus == "Đã duyệt");
            }
            else if (visibility == "hidden")
            {
                query = query.Where(p =>
                    p.ApprovalStatus == "Hidden" ||
                    p.ApprovalStatus == "Ẩn" ||
                    p.ApprovalStatus == "NotPublic" ||
                    p.ApprovalStatus == "Không công khai");
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();
                query = query.Where(p =>
                    (p.ProjectName != null && EF.Functions.Like(p.ProjectName, $"%{keyword}%")) ||
                    (p.Investor != null && EF.Functions.Like(p.Investor, $"%{keyword}%")) ||
                    (p.AddressDetail != null && EF.Functions.Like(p.AddressDetail, $"%{keyword}%")) ||
                    (p.ProjectType != null && EF.Functions.Like(p.ProjectType, $"%{keyword}%")) ||
                    (p.ProjectStatus != null && EF.Functions.Like(p.ProjectStatus, $"%{keyword}%")) ||
                    (p.Scale != null && EF.Functions.Like(p.Scale, $"%{keyword}%")) ||
                    (p.Description != null && EF.Functions.Like(p.Description, $"%{keyword}%"))
                );
            }

            if (areaId.HasValue && areaId.Value > 0)
            {
                query = query.Where(p => p.AreaID == areaId.Value);
            }

            if (wardId.HasValue && wardId.Value > 0)
            {
                query = query.Where(p => p.WardID == wardId.Value);
            }

            if (ownerId.HasValue && ownerId.Value > 0)
            {
                query = query.Where(p => p.OwnerUserID == ownerId.Value);
            }

            if (!string.IsNullOrWhiteSpace(projectStatus))
            {
                projectStatus = projectStatus.Trim();
                query = query.Where(p => p.ProjectStatus == projectStatus);
            }

            if (!string.IsNullOrWhiteSpace(projectType))
            {
                projectType = projectType.Trim();
                query = query.Where(p => p.ProjectType != null && EF.Functions.Like(p.ProjectType, $"%{projectType}%"));
            }

            legal = (legal ?? string.Empty).Trim().ToLowerInvariant();
            if (legal == "has")
            {
                query = query.Where(p => p.LegalDocs != null && p.LegalDocs != "");
            }
            else if (legal == "none")
            {
                query = query.Where(p => p.LegalDocs == null || p.LegalDocs == "");
            }

            price = (price ?? string.Empty).Trim().ToLowerInvariant();
            query = price switch
            {
                "under_2" => query.Where(p => (p.PriceMin ?? 0) > 0 && (p.PriceMin ?? 0) < 2),
                "2_5" => query.Where(p => (p.PriceMin ?? 0) >= 2 && (p.PriceMin ?? 0) <= 5),
                "5_10" => query.Where(p => (p.PriceMin ?? 0) > 5 && (p.PriceMin ?? 0) <= 10),
                "over_10" => query.Where(p => (p.PriceMin ?? 0) > 10),
                "unknown" => query.Where(p => p.PriceMin == null && p.PriceMax == null),
                _ => query
            };

            int totalResults = await query.CountAsync();

            int allCount = await _context.Projects.CountAsync(p => p.IsDeleted == false);
            int publicCount = await _context.Projects.CountAsync(p =>
                p.IsDeleted == false &&
                (p.ApprovalStatus == "Approved" || p.ApprovalStatus == "Đã duyệt"));
            int hiddenCount = await _context.Projects.CountAsync(p =>
                p.IsDeleted == false &&
                (p.ApprovalStatus == "Hidden" ||
                 p.ApprovalStatus == "Ẩn" ||
                 p.ApprovalStatus == "NotPublic" ||
                 p.ApprovalStatus == "Không công khai"));

            query = (sort ?? "newest").Trim().ToLowerInvariant() switch
            {
                "oldest" => query.OrderBy(p => p.CreatedAt),
                "name_asc" => query.OrderBy(p => p.ProjectName),
                "name_desc" => query.OrderByDescending(p => p.ProjectName),
                "price_asc" => query.OrderBy(p => p.PriceMin ?? decimal.MaxValue),
                "price_desc" => query.OrderByDescending(p => p.PriceMax ?? 0),
                "views_desc" => query.OrderByDescending(p => p.Views).ThenByDescending(p => p.CreatedAt),
                "published_desc" => query.OrderByDescending(p => p.PublishedAt),
                _ => query.OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt).ThenByDescending(p => p.CreatedAt)
            };

            int totalPages = (int)Math.Ceiling(totalResults / (double)pageSize);
            if (totalPages < 1) totalPages = 1;
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;

            var projects = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            await LoadAdminProjectFilterDataAsync(areaId, wardId, ownerId);

            ViewBag.Visibility = visibility;
            ViewBag.Keyword = keyword;
            ViewBag.AreaId = areaId;
            ViewBag.WardId = wardId;
            ViewBag.OwnerId = ownerId;
            ViewBag.ProjectStatus = projectStatus;
            ViewBag.ProjectType = projectType;
            ViewBag.Legal = legal;
            ViewBag.Price = price;
            ViewBag.Sort = sort;
            ViewBag.TotalResults = totalResults;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.AllCount = allCount;
            ViewBag.PublicCount = publicCount;
            ViewBag.HiddenCount = hiddenCount;

            return View(projects);
        }

        // 2. CHI TIẾT DỰ ÁN
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();

            var project = await _context.Projects
                .Include(p => p.Owner)
                .Include(p => p.Area)
                .Include(p => p.Ward)
                .FirstOrDefaultAsync(m => m.ProjectID == id);

            if (project == null) return NotFound();
            return View(project);
        }

        // 3. TẠO MỚI (GET)
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            ViewBag.Owners = await GetBusinessOwnersSelectList();
            ViewBag.Areas = new SelectList(await _context.Areas.OrderBy(a => a.AreaName).ToListAsync(), "AreaID", "AreaName");
            return View();
        }

        // 4. TẠO MỚI (POST)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Project model, IFormFile MainImageFile, IFormFile LegalDocsFile)
        {
            if (ModelState.IsValid)
            {
                string projectDir = Path.Combine(_env.WebRootPath, "uploads", "projects");
                string legalDir = Path.Combine(_env.WebRootPath, "uploads", "legals");

                if (!Directory.Exists(projectDir)) Directory.CreateDirectory(projectDir);
                if (!Directory.Exists(legalDir)) Directory.CreateDirectory(legalDir);

                if (MainImageFile != null && MainImageFile.Length > 0)
                {
                    string fileName = Guid.NewGuid().ToString() + Path.GetExtension(MainImageFile.FileName);
                    string path = Path.Combine(projectDir, fileName);

                    using (var stream = new FileStream(path, FileMode.Create))
                    {
                        await MainImageFile.CopyToAsync(stream);
                    }

                    model.MainImage = "/uploads/projects/" + fileName;
                }

                if (LegalDocsFile != null && LegalDocsFile.Length > 0)
                {
                    string fileName = "LEGAL_" + Guid.NewGuid().ToString() + Path.GetExtension(LegalDocsFile.FileName);
                    string path = Path.Combine(legalDir, fileName);

                    using (var stream = new FileStream(path, FileMode.Create))
                    {
                        await LegalDocsFile.CopyToAsync(stream);
                    }

                    model.LegalDocs = "/uploads/legals/" + fileName;
                }

                model.ApprovalStatus = "Approved";
                model.CreatedAt = DateTime.Now;
                model.PublishedAt = DateTime.Now;
                model.IsDeleted = false;

                _context.Projects.Add(model);
                await _context.SaveChangesAsync();

                TempData["Success"] = "🎉 Chúc mừng! Dự án '" + model.ProjectName + "' đã được đăng tải thành công.";
                return RedirectToAction(nameof(Index));
            }

            TempData["Error"] = "Lỗi: Vui lòng kiểm tra lại các trường dữ liệu bắt buộc.";
            ViewBag.Owners = await GetBusinessOwnersSelectList(model.OwnerUserID);
            ViewBag.Areas = new SelectList(await _context.Areas.OrderBy(a => a.AreaName).ToListAsync(), "AreaID", "AreaName", model.AreaID);
            return View(model);
        }

        // 5. CHỈNH SỬA (GET)
        [HttpGet]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var project = await _context.Projects.FindAsync(id);
            if (project == null) return NotFound();

            ViewBag.Owners = await GetBusinessOwnersSelectList(project.OwnerUserID);
            ViewBag.Areas = new SelectList(await _context.Areas.OrderBy(a => a.AreaName).ToListAsync(), "AreaID", "AreaName", project.AreaID);
            ViewBag.Wards = new SelectList(
                await _context.Wards
                    .Where(w => w.AreaID == project.AreaID)
                    .OrderBy(w => w.WardName)
                    .ToListAsync(),
                "WardID",
                "WardName",
                project.WardID
            );

            return View(project);
        }

        // 6. CHỈNH SỬA (POST)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, Project model, IFormFile? MainImageFile, IFormFile? LegalDocsFile)
        {
            if (id != model.ProjectID) return NotFound();

            var exist = await _context.Projects.FindAsync(id);
            if (exist == null) return NotFound();

            ModelState.Remove("MainImage");
            ModelState.Remove("Thumbnail");
            ModelState.Remove("LegalDocs");
            ModelState.Remove("LegalDocsJson");
            ModelState.Remove("TimelineJson");
            ModelState.Remove("Owner");
            ModelState.Remove("Area");
            ModelState.Remove("Ward");
            ModelState.Remove("Properties");
            ModelState.Remove("ProjectLeads");

            if (ModelState.IsValid)
            {
                try
                {
                    string projectDir = Path.Combine(_env.WebRootPath, "uploads", "projects");
                    string legalDir = Path.Combine(_env.WebRootPath, "uploads", "legals");

                    if (!Directory.Exists(projectDir)) Directory.CreateDirectory(projectDir);
                    if (!Directory.Exists(legalDir)) Directory.CreateDirectory(legalDir);

                    if (MainImageFile != null && MainImageFile.Length > 0)
                    {
                        string fileName = Guid.NewGuid().ToString() + Path.GetExtension(MainImageFile.FileName);

                        using (var stream = new FileStream(Path.Combine(projectDir, fileName), FileMode.Create))
                        {
                            await MainImageFile.CopyToAsync(stream);
                        }

                        exist.MainImage = "/uploads/projects/" + fileName;
                    }

                    if (LegalDocsFile != null && LegalDocsFile.Length > 0)
                    {
                        string fileName = "LEGAL_" + Guid.NewGuid().ToString() + Path.GetExtension(LegalDocsFile.FileName);

                        using (var stream = new FileStream(Path.Combine(legalDir, fileName), FileMode.Create))
                        {
                            await LegalDocsFile.CopyToAsync(stream);
                        }

                        exist.LegalDocs = "/uploads/legals/" + fileName;
                    }

                    exist.ProjectName = model.ProjectName;
                    exist.Investor = model.Investor;
                    exist.Description = model.Description;
                    exist.ContentHtml = model.ContentHtml;
                    exist.AddressDetail = model.AddressDetail;
                    exist.PriceMin = model.PriceMin;
                    exist.PriceMax = model.PriceMax;
                    exist.PriceUnit = model.PriceUnit;
                    exist.AreaMin = model.AreaMin;
                    exist.AreaMax = model.AreaMax;
                    exist.Scale = model.Scale;
                    exist.ConstructionDensity = model.ConstructionDensity;
                    exist.Utilities = model.Utilities;
                    exist.ProjectType = model.ProjectType;
                    exist.AreaID = model.AreaID;
                    exist.WardID = model.WardID;
                    exist.ProjectStatus = model.ProjectStatus;
                    exist.OwnerUserID = model.OwnerUserID;
                    exist.UpdatedAt = DateTime.Now;

                    await _context.SaveChangesAsync();

                    TempData["Success"] = "✅ Đã lưu thay đổi cho dự án: " + model.ProjectName;
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    TempData["Error"] = "Lỗi hệ thống khi lưu dữ liệu: " + ex.Message;
                }
            }
            else
            {
                var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
                TempData["Error"] = "Cập nhật thất bại. Vui lòng kiểm tra lại: " + string.Join(", ", errors);
            }

            ViewBag.Owners = await GetBusinessOwnersSelectList(model.OwnerUserID);
            ViewBag.Areas = new SelectList(await _context.Areas.OrderBy(a => a.AreaName).ToListAsync(), "AreaID", "AreaName", model.AreaID);
            ViewBag.Wards = new SelectList(
                await _context.Wards
                    .Where(w => w.AreaID == model.AreaID)
                    .OrderBy(w => w.WardName)
                    .ToListAsync(),
                "WardID",
                "WardName",
                model.WardID
            );

            model.MainImage = exist.MainImage;
            model.Thumbnail = exist.Thumbnail;
            model.LegalDocs = exist.LegalDocs;
            model.LegalDocsJson = exist.LegalDocsJson;
            model.TimelineJson = exist.TimelineJson;

            return View(model);
        }

        // ==========================================
        // 7. ẨN / CÔNG KHAI DỰ ÁN
        // Không dùng duyệt/từ chối vì đây là khu quản trị dự án do admin tạo.
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> TogglePublic(int id)
        {
            var project = await _context.Projects.FindAsync(id);
            if (project == null)
            {
                TempData["Error"] = "Không tìm thấy dữ liệu dự án trên hệ thống.";
                return RedirectToAction(nameof(Index));
            }

            bool isPublic = project.ApprovalStatus == "Approved" || project.ApprovalStatus == "Đã duyệt";

            if (isPublic)
            {
                project.ApprovalStatus = "Hidden";
                project.UpdatedAt = DateTime.Now;
                TempData["Success"] = $"Đã ẩn dự án khỏi trang công khai: {project.ProjectName}.";
            }
            else
            {
                project.ApprovalStatus = "Approved";
                project.PublishedAt ??= DateTime.Now;
                project.UpdatedAt = DateTime.Now;
                TempData["Success"] = $"Đã công khai lại dự án: {project.ProjectName}.";
            }

            await _context.SaveChangesAsync();

            string referer = Request.Headers["Referer"].ToString();
            if (!string.IsNullOrWhiteSpace(referer)) return Redirect(referer);
            return RedirectToAction(nameof(Index));
        }

        // Giữ lại action cũ để tránh lỗi route cũ nếu có form/nút cũ gọi tới.
        // Không hiển thị nút này ở Index mới nữa.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string newStatus)
        {
            var project = await _context.Projects.FindAsync(id);
            if (project == null)
            {
                TempData["Error"] = "Không tìm thấy dữ liệu dự án trên hệ thống.";
                return RedirectToAction(nameof(Index));
            }

            if (newStatus == "Approved" || newStatus == "Hidden")
            {
                project.ApprovalStatus = newStatus;
                project.UpdatedAt = DateTime.Now;
                if (newStatus == "Approved") project.PublishedAt ??= DateTime.Now;
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã cập nhật trạng thái hiển thị dự án.";
            }

            string referer = Request.Headers["Referer"].ToString();
            if (!string.IsNullOrWhiteSpace(referer)) return Redirect(referer);
            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // 8. XÓA MỀM DỰ ÁN
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var project = await _context.Projects.FindAsync(id);

            if (project != null)
            {
                project.IsDeleted = true;
                project.UpdatedAt = DateTime.Now;

                var linkedProperties = await _context.Properties.Where(p => p.ProjectID == id).ToListAsync();
                foreach (var prop in linkedProperties) prop.ProjectID = null;

                await _context.SaveChangesAsync();
                TempData["Success"] = "🗑️ Đã chuyển dự án vào thùng rác và gỡ liên kết các BĐS thành công.";
            }

            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // 9. AJAX LẤY PHƯỜNG XÃ
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> GetWardsByArea(int areaId)
        {
            var wards = await _context.Wards
                .AsNoTracking()
                .Where(w => w.AreaID == areaId)
                .OrderBy(w => w.WardName)
                .Select(w => new { id = w.WardID, name = w.WardName })
                .ToListAsync();

            return Json(wards);
        }

        // ==========================================
        // 10. UPLOAD ẢNH CKEDITOR
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> UploadImageCKEditor(IFormFile upload)
        {
            if (upload != null && upload.Length > 0)
            {
                string uploadDir = Path.Combine(_env.WebRootPath, "uploads", "ckeditor");
                if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

                string fileName = Guid.NewGuid().ToString() + Path.GetExtension(upload.FileName);
                string filePath = Path.Combine(uploadDir, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await upload.CopyToAsync(stream);
                }

                var url = $"/uploads/ckeditor/{fileName}";
                return Json(new { uploaded = true, url = url });
            }

            return Json(new
            {
                uploaded = false,
                error = new { message = "Không thể tải ảnh lên, vui lòng thử lại." }
            });
        }
    }
}
