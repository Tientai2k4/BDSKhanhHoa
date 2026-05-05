using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")] // Chỉ Admin mới có quyền cấu hình hệ thống
    [Route("Admin/[controller]/[action]")]
    public class PagesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;

        public PagesController(ApplicationDbContext context, IWebHostEnvironment env)
        {
            _context = context;
            _env = env; // Bổ sung IWebHostEnvironment để lưu ảnh
        }

        // 1. Hiển thị danh sách các trang tĩnh
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var pages = await _context.StaticPages.AsNoTracking().OrderByDescending(p => p.UpdatedAt).ToListAsync();
            return View(pages);
        }

        // 2. Thêm trang mới (GET & POST)
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
                TempData["SuccessMessage"] = "Thêm trang tĩnh mới thành công!";
                return RedirectToAction(nameof(Index));
            }
            return View(model);
        }

        // 3. Chỉnh sửa trang (GET & POST)
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
                    // Tùy chọn: Có cho phép sửa PageKey không? (Thường là không nên để tránh hỏng SEO link)
                    // existingPage.PageKey = model.PageKey; 
                    existingPage.UpdatedAt = DateTime.Now;

                    _context.Update(existingPage);
                    await _context.SaveChangesAsync();

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

        // 4. Xóa trang tĩnh (AJAX)
        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var page = await _context.StaticPages.FindAsync(id);
            if (page == null) return Json(new { success = false, message = "Không tìm thấy trang." });

            _context.StaticPages.Remove(page);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Đã xóa trang tĩnh thành công!" });
        }

        // 5. API Upload Ảnh riêng cho Trình soạn thảo QuillJS
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