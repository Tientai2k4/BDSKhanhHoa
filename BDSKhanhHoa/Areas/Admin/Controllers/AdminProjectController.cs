using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

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

        // --- HÀM HỖ TRỢ LẤY DANH SÁCH DOANH NGHIỆP ĐÃ ĐƯỢC DUYỆT ---
        private async Task<SelectList> GetBusinessOwnersSelectList(int? selectedId = null)
        {
            var businessOwners = await _context.BusinessProfiles
                .Include(b => b.User)
                .Where(b => b.VerificationStatus == "Approved" && b.User != null && !b.User.IsDeleted)
                .Select(b => new
                {
                    UserID = b.UserID,
                    DisplayName = b.BusinessName + " (Tài khoản: " + b.User.Username + ")"
                })
                .ToListAsync();

            return new SelectList(businessOwners, "UserID", "DisplayName", selectedId);
        }

        // ==========================================
        // 1. TỔNG HỢP DANH SÁCH DỰ ÁN & LỌC TRẠNG THÁI (ĐÃ GỘP)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index(string status = "")
        {
            var query = _context.Projects
                .Include(p => p.Owner)
                .Include(p => p.Area)
                .Include(p => p.Ward)
                .Where(p => p.IsDeleted == false)
                .AsQueryable();

            if (!string.IsNullOrEmpty(status))
            {
                query = query.Where(p => p.ApprovalStatus == status);
            }

            var projects = await query
                .OrderBy(p => p.ApprovalStatus == "Pending" ? 0 : 1)
                .ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.CurrentStatus = status;
            ViewBag.PendingCount = await _context.Projects.CountAsync(p => p.ApprovalStatus == "Pending" && p.IsDeleted == false);
            ViewBag.ApprovedCount = await _context.Projects.CountAsync(p => p.ApprovalStatus == "Approved" && p.IsDeleted == false);

            return View(projects);
        }

        // 2. CHI TIẾT DỰ ÁN
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();
            var project = await _context.Projects
                .Include(p => p.Owner).Include(p => p.Area).Include(p => p.Ward)
                .FirstOrDefaultAsync(m => m.ProjectID == id);
            if (project == null) return NotFound();
            return View(project);
        }

        // 3. TẠO MỚI (GET)
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            ViewBag.Owners = await GetBusinessOwnersSelectList();
            ViewBag.Areas = new SelectList(await _context.Areas.ToListAsync(), "AreaID", "AreaName");
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

                if (MainImageFile != null)
                {
                    string fileName = Guid.NewGuid().ToString() + Path.GetExtension(MainImageFile.FileName);
                    string path = Path.Combine(projectDir, fileName);
                    using (var stream = new FileStream(path, FileMode.Create)) { await MainImageFile.CopyToAsync(stream); }
                    model.MainImage = "/uploads/projects/" + fileName;
                }

                if (LegalDocsFile != null)
                {
                    string fileName = "LEGAL_" + Guid.NewGuid().ToString() + Path.GetExtension(LegalDocsFile.FileName);
                    string path = Path.Combine(legalDir, fileName);
                    using (var stream = new FileStream(path, FileMode.Create)) { await LegalDocsFile.CopyToAsync(stream); }
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
            ViewBag.Areas = new SelectList(await _context.Areas.ToListAsync(), "AreaID", "AreaName", model.AreaID);
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
            ViewBag.Areas = new SelectList(await _context.Areas.ToListAsync(), "AreaID", "AreaName", project.AreaID);
            ViewBag.Wards = new SelectList(await _context.Wards.Where(w => w.AreaID == project.AreaID).ToListAsync(), "WardID", "WardName", project.WardID);
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
            ModelState.Remove("LegalDocs");
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
                        using (var stream = new FileStream(Path.Combine(projectDir, fileName), FileMode.Create)) { await MainImageFile.CopyToAsync(stream); }
                        exist.MainImage = "/uploads/projects/" + fileName;
                    }

                    if (LegalDocsFile != null && LegalDocsFile.Length > 0)
                    {
                        string fileName = "LEGAL_" + Guid.NewGuid().ToString() + Path.GetExtension(LegalDocsFile.FileName);
                        using (var stream = new FileStream(Path.Combine(legalDir, fileName), FileMode.Create)) { await LegalDocsFile.CopyToAsync(stream); }
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
            ViewBag.Areas = new SelectList(await _context.Areas.ToListAsync(), "AreaID", "AreaName", model.AreaID);
            ViewBag.Wards = new SelectList(await _context.Wards.Where(w => w.AreaID == model.AreaID).ToListAsync(), "WardID", "WardName", model.WardID);

            model.MainImage = exist.MainImage;
            model.LegalDocs = exist.LegalDocs;

            return View(model);
        }

        // ==========================================
        // 7. XỬ LÝ CẬP NHẬT TRẠNG THÁI PHÊ DUYỆT 
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string newStatus)
        {
            var project = await _context.Projects.FindAsync(id);
            if (project == null)
            {
                TempData["Error"] = "Không tìm thấy dữ liệu dự án trên hệ thống!";
                return RedirectToAction(nameof(Index));
            }

            if (newStatus == "Approved" || newStatus == "Rejected" || newStatus == "Pending")
            {
                project.ApprovalStatus = newStatus;
                project.UpdatedAt = DateTime.Now;
                await _context.SaveChangesAsync();

                if (newStatus == "Approved") TempData["Success"] = $"Đã phê duyệt dự án: {project.ProjectName}.";
                else if (newStatus == "Rejected") TempData["Error"] = $"Đã từ chối dự án: {project.ProjectName}.";
            }

            string referer = Request.Headers["Referer"].ToString();
            if (!string.IsNullOrEmpty(referer)) return Redirect(referer);
            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // 8. XÓA BỎ DỰ ÁN
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
                TempData["Success"] = "🗑️ Đã chuyển dự án vào thùng rác và gỡ liên kết các BĐS thành công!";
            }
            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // 9. AJAX LẤY PHƯỜNG XÃ
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> GetWardsByArea(int areaId)
        {
            var wards = await _context.Wards.Where(w => w.AreaID == areaId).Select(w => new { id = w.WardID, name = w.WardName }).ToListAsync();
            return Json(wards);
        }

        // ==========================================
        // 10. HÀM XỬ LÝ UPLOAD ẢNH TỪ CKEDITOR
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> UploadImageCKEditor(IFormFile upload)
        {
            if (upload != null && upload.Length > 0)
            {
                // Thư mục lưu ảnh bài viết CKEditor
                string uploadDir = Path.Combine(_env.WebRootPath, "uploads", "ckeditor");
                if (!Directory.Exists(uploadDir))
                {
                    Directory.CreateDirectory(uploadDir);
                }

                // Tạo tên file ngẫu nhiên để không bị trùng
                string fileName = Guid.NewGuid().ToString() + Path.GetExtension(upload.FileName);
                string filePath = Path.Combine(uploadDir, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await upload.CopyToAsync(stream);
                }

                // Trả về JSON đúng chuẩn mà CKEditor yêu cầu
                var url = $"/uploads/ckeditor/{fileName}";
                return Json(new { uploaded = true, url = url });
            }

            return Json(new { uploaded = false, error = new { message = "Không thể tải ảnh lên, vui lòng thử lại." } });
        }
    }
}