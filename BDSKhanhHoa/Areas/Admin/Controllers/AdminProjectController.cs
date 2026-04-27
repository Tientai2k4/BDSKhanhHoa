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

        // 1. DANH SÁCH DỰ ÁN
        public async Task<IActionResult> Index()
        {
            var projects = await _context.Projects
                .Include(p => p.Owner)
                .Include(p => p.Area)
                .Where(p => !p.IsDeleted)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();
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
            ViewBag.Owners = new SelectList(await _context.Users.Where(u => !u.IsDeleted).ToListAsync(), "UserID", "Username");
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
                // Xử lý tạo thư mục vật lý để tránh lỗi DirectoryNotFound
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

                // GỬI THÔNG BÁO THÀNH CÔNG
                TempData["Success"] = "🎉 Chúc mừng! Dự án '" + model.ProjectName + "' đã được đăng tải thành công.";
                return RedirectToAction(nameof(Index));
            }
            TempData["Error"] = "Lỗi: Vui lòng kiểm tra lại các trường dữ liệu bắt buộc.";
            ViewBag.Owners = new SelectList(await _context.Users.Where(u => !u.IsDeleted).ToListAsync(), "UserID", "Username");
            ViewBag.Areas = new SelectList(await _context.Areas.ToListAsync(), "AreaID", "AreaName");
            return View(model);
        }

        // 5. CHỈNH SỬA (GET)
        [HttpGet]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();
            var project = await _context.Projects.FindAsync(id);
            if (project == null) return NotFound();

            ViewBag.Owners = new SelectList(await _context.Users.Where(u => !u.IsDeleted).ToListAsync(), "UserID", "Username", project.OwnerUserID);
            ViewBag.Areas = new SelectList(await _context.Areas.ToListAsync(), "AreaID", "AreaName", project.AreaID);
            ViewBag.Wards = new SelectList(await _context.Wards.Where(w => w.AreaID == project.AreaID).ToListAsync(), "WardID", "WardName", project.WardID);
            return View(project);
        }

        // 6. CHỈNH SỬA (POST)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, Project model, IFormFile MainImageFile, IFormFile LegalDocsFile)
        {
            if (id != model.ProjectID) return NotFound();
            var exist = await _context.Projects.FindAsync(id);
            if (exist == null) return NotFound();

            if (ModelState.IsValid)
            {
                string projectDir = Path.Combine(_env.WebRootPath, "uploads", "projects");
                string legalDir = Path.Combine(_env.WebRootPath, "uploads", "legals");
                if (!Directory.Exists(projectDir)) Directory.CreateDirectory(projectDir);
                if (!Directory.Exists(legalDir)) Directory.CreateDirectory(legalDir);

                if (MainImageFile != null)
                {
                    string fileName = Guid.NewGuid().ToString() + Path.GetExtension(MainImageFile.FileName);
                    using (var stream = new FileStream(Path.Combine(projectDir, fileName), FileMode.Create)) { await MainImageFile.CopyToAsync(stream); }
                    exist.MainImage = "/uploads/projects/" + fileName;
                }

                if (LegalDocsFile != null)
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
            return View(model);
        }

        // 7. XÓA MỀM (POST)
        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var project = await _context.Projects.FindAsync(id);
            if (project != null)
            {
                project.IsDeleted = true;
                await _context.SaveChangesAsync();
                TempData["Success"] = "🗑️ Đã chuyển dự án vào thùng rác.";
            }
            return RedirectToAction(nameof(Index));
        }

        // 8. AJAX LẤY PHƯỜNG XÃ
        [HttpGet]
        public async Task<IActionResult> GetWardsByArea(int areaId)
        {
            var wards = await _context.Wards.Where(w => w.AreaID == areaId).Select(w => new { id = w.WardID, name = w.WardName }).ToListAsync();
            return Json(wards);
        }
    }
}