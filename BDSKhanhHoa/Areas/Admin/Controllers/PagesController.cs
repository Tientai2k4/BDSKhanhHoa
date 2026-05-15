using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    [Route("Admin/[controller]/[action]")]
    public class PagesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;
        private readonly IAuditLogService _auditLogService; // Thêm Service Log

        public PagesController(ApplicationDbContext context, IWebHostEnvironment env, IAuditLogService auditLogService)
        {
            _context = context;
            _env = env;
            _auditLogService = auditLogService;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var pages = await _context.StaticPages.AsNoTracking().OrderByDescending(p => p.UpdatedAt).ToListAsync();
            return View(pages);
        }

        [HttpGet]
        public IActionResult Create()
        {
            return View(new StaticPage());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(StaticPage model)
        {
            if (await _context.StaticPages.AnyAsync(p => p.PageKey == model.PageKey))
            {
                ModelState.AddModelError("PageKey", "Mã định danh (PageKey) này đã tồn tại.");
            }

            if (ModelState.IsValid)
            {
                model.UpdatedAt = DateTime.Now;
                _context.StaticPages.Add(model);
                await _context.SaveChangesAsync();

                // GHI LOG
                int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                await _auditLogService.LogAsync(userId, "Thêm trang tĩnh mới", "Pages", $"PageKey: {model.PageKey}", severity: "Info");

                TempData["SuccessMessage"] = "Thêm trang tĩnh mới thành công!";
                return RedirectToAction(nameof(Index));
            }
            return View(model);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> Edit(int id)
        {
            var page = await _context.StaticPages.FindAsync(id);
            if (page == null) return NotFound();
            return View(page);
        }

        [HttpPost("{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, StaticPage model)
        {
            if (id != model.PageID) return BadRequest();

            if (ModelState.IsValid)
            {
                try
                {
                    var existingPage = await _context.StaticPages.FindAsync(id);
                    if (existingPage == null) return NotFound();

                    existingPage.Title = model.Title;
                    existingPage.Description = model.Description;
                    existingPage.Content = model.Content;
                    existingPage.UpdatedAt = DateTime.Now;

                    _context.Update(existingPage);
                    await _context.SaveChangesAsync();

                    // GHI LOG
                    int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                    await _auditLogService.LogAsync(userId, "Cập nhật trang tĩnh", "Pages", $"PageID: {id} - {model.Title}", severity: "Info");

                    TempData["SuccessMessage"] = "Cập nhật trang tĩnh thành công!";
                    return RedirectToAction(nameof(Index));
                }
                catch (DbUpdateConcurrencyException)
                {
                    ModelState.AddModelError("", "Đã xảy ra lỗi khi lưu dữ liệu.");
                }
            }
            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var page = await _context.StaticPages.FindAsync(id);
            if (page == null) return Json(new { success = false, message = "Không tìm thấy trang." });

            _context.StaticPages.Remove(page);
            await _context.SaveChangesAsync();

            // GHI LOG
            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
            await _auditLogService.LogAsync(userId, "Xóa trang tĩnh", "Pages", $"PageID: {id} - {page.Title}", severity: "Warning");

            return Json(new { success = true, message = "Đã xóa trang tĩnh thành công!" });
        }

        [HttpPost]
        public async Task<IActionResult> UploadImage(IFormFile upload)
        {
            if (upload != null && upload.Length > 0)
            {
                string uploadDir = Path.Combine(_env.WebRootPath, "uploads", "pages");
                if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

                string fileName = Guid.NewGuid().ToString() + Path.GetExtension(upload.FileName);
                string filePath = Path.Combine(uploadDir, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await upload.CopyToAsync(stream);
                }

                return Json(new { uploaded = true, url = "/uploads/pages/" + fileName });
            }
            return Json(new { uploaded = false, error = new { message = "Lỗi tải ảnh." } });
        }
    }
}