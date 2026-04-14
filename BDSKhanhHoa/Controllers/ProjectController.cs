using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class ProjectController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _hostEnvironment;

        public ProjectController(ApplicationDbContext context, IWebHostEnvironment hostEnvironment)
        {
            _context = context;
            _hostEnvironment = hostEnvironment;
        }

        // ==========================================
        // 1. QUẢN LÝ DỰ ÁN CÁ NHÂN (Dành cho Member)
        // ==========================================
        public async Task<IActionResult> MyProjects()
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdClaim, out int userId)) return RedirectToAction("Login", "Account");

            var projects = await _context.Projects
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.UserID == userId && p.IsDeleted == false)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            return View(projects);
        }

        // ==========================================
        // 2. GỬI HỒ SƠ ĐĂNG DỰ ÁN MỚI
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            ViewBag.Areas = new SelectList(await _context.Areas.OrderBy(a => a.AreaName).ToListAsync(), "AreaID", "AreaName");
            return View(new Project());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Project project, IFormFile MainImageFile, IFormFile LegalDocsFile)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdClaim, out int userId)) return RedirectToAction("Login", "Account");

            project.UserID = userId;
            project.ApprovalStatus = "Pending"; // Mặc định phải chờ Admin kiểm duyệt hồ sơ
            project.CreatedAt = DateTime.Now;
            project.UpdatedAt = DateTime.Now;

            // Xử lý Ảnh phối cảnh
            if (MainImageFile != null && MainImageFile.Length > 0)
            {
                string uploadDir = Path.Combine(_hostEnvironment.WebRootPath, "uploads/projects/images");
                if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);
                string fileName = Guid.NewGuid().ToString() + Path.GetExtension(MainImageFile.FileName);
                using (var stream = new FileStream(Path.Combine(uploadDir, fileName), FileMode.Create)) { await MainImageFile.CopyToAsync(stream); }
                project.MainImage = "/uploads/projects/images/" + fileName;
            }

            // Xử lý Hồ sơ pháp lý (BẮT BUỘC: Hợp đồng, GPKD, 1/500...)
            if (LegalDocsFile != null && LegalDocsFile.Length > 0)
            {
                string uploadDir = Path.Combine(_hostEnvironment.WebRootPath, "uploads/projects/legals");
                if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);
                string fileName = Guid.NewGuid().ToString() + Path.GetExtension(LegalDocsFile.FileName);
                using (var stream = new FileStream(Path.Combine(uploadDir, fileName), FileMode.Create)) { await LegalDocsFile.CopyToAsync(stream); }
                project.LegalDocs = "/uploads/projects/legals/" + fileName;
            }
            else
            {
                ModelState.AddModelError("LegalDocs", "Hệ thống bắt buộc bạn phải tải lên hồ sơ pháp lý (GPKD, QĐ 1/500,...) để bảo vệ quyền lợi người mua.");
                ViewBag.Areas = new SelectList(await _context.Areas.OrderBy(a => a.AreaName).ToListAsync(), "AreaID", "AreaName");
                return View(project);
            }

            _context.Projects.Add(project);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã gửi hồ sơ dự án! Ban quản trị sẽ tiến hành xác minh tính pháp lý và phản hồi trong 24h.";
            return RedirectToAction("MyProjects");
        }

        // ==========================================
        // 3. TÌM KIẾM & DANH SÁCH DỰ ÁN NỔI BẬT
        // ==========================================
        [AllowAnonymous]
        [Route("Project/Search")]
        public async Task<IActionResult> Search(string keyword, int? areaId, string status, int page = 1)
        {
            int pageSize = 9;
            var query = _context.Projects
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.ApprovalStatus == "Approved" && p.IsDeleted == false)
                .AsQueryable();

            if (!string.IsNullOrEmpty(keyword)) query = query.Where(p => p.ProjectName.Contains(keyword) || p.Investor.Contains(keyword));
            if (areaId.HasValue) query = query.Where(p => p.AreaID == areaId);
            if (!string.IsNullOrEmpty(status)) query = query.Where(p => p.ProjectStatus == status);

            int totalItems = await query.CountAsync();
            ViewBag.TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            ViewBag.CurrentPage = page;
            ViewBag.Keyword = keyword;
            ViewBag.AreaId = areaId;
            ViewBag.Status = status;
            ViewBag.TotalItems = totalItems;

            ViewBag.Areas = await _context.Areas.OrderBy(a => a.AreaName).ToListAsync();

            var projects = await query.OrderByDescending(p => p.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            return View(projects);
        }

        // ==========================================
        // 4. XEM CHI TIẾT DỰ ÁN & CÁC BĐS TRONG DỰ ÁN
        // ==========================================
        [AllowAnonymous]
        [Route("Project/Details/{id}")]
        public async Task<IActionResult> Details(int id)
        {
            var project = await _context.Projects
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.User)
                .FirstOrDefaultAsync(p => p.ProjectID == id && p.ApprovalStatus == "Approved" && p.IsDeleted == false);

            if (project == null) return RedirectToAction("Search");

            // Lấy danh sách các BĐS (Tin đăng) đang thuộc dự án này
            ViewBag.PropertiesInProject = await _context.Properties
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.ProjectID == id && p.Status == "Approved" && p.IsDeleted == false)
                .OrderByDescending(p => p.PackageID).ThenByDescending(p => p.CreatedAt)
                .Take(6)
                .ToListAsync();

            return View(project);
        }
    }
}