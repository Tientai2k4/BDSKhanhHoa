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
    [Authorize(Roles = "Admin,Staff")]
    public class BlogController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _hostEnvironment;

        // Định nghĩa sẵn các danh mục chuẩn
        private readonly List<string> _blogCategories = new List<string>
        {
            "Tin tức thị trường",
            "Phân tích đầu tư",
            "Kiến thức phong thủy",
            "Kinh nghiệm mua bán",
            "Thông báo hệ thống"
        };

        public BlogController(ApplicationDbContext context, IWebHostEnvironment hostEnvironment)
        {
            _context = context;
            _hostEnvironment = hostEnvironment;
        }

        // ==========================================
        // 1. DANH SÁCH BÀI VIẾT (CÓ LỌC DANH MỤC)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index(string category = "")
        {
            var query = _context.Blogs
                .Include(b => b.User)
                .Where(b => !b.IsDeleted)
                .AsQueryable();

            if (!string.IsNullOrEmpty(category))
            {
                query = query.Where(b => b.Category == category);
            }

            var blogs = await query.OrderByDescending(b => b.CreatedAt).ToListAsync();

            ViewBag.TotalViews = blogs.Sum(b => b.Views);
            ViewBag.TotalPosts = blogs.Count;

            // Gửi danh sách Category ra View để làm thẻ Select Lọc
            ViewBag.Categories = _blogCategories;
            ViewBag.CurrentCategory = category;

            return View(blogs);
        }

        // ==========================================
        // 2. THÊM MỚI BÀI VIẾT
        // ==========================================
        [HttpGet]
        public IActionResult Create()
        {
            ViewBag.Categories = new SelectList(_blogCategories);
            return View(new Blog());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Blog blog, IFormFile? imageFile)
        {
            ModelState.Remove("User");
            ModelState.Remove("ImageURL");

            if (ModelState.IsValid)
            {
                try
                {
                    if (imageFile != null && imageFile.Length > 0)
                    {
                        blog.ImageURL = await SaveImage(imageFile, "covers");
                    }

                    var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
                    blog.UserID = !string.IsNullOrEmpty(userIdClaim) ? int.Parse(userIdClaim) : 1;

                    blog.CreatedAt = DateTime.Now;
                    blog.Views = 0;
                    blog.IsDeleted = false;

                    _context.Blogs.Add(blog);
                    await _context.SaveChangesAsync();

                    TempData["Success"] = "Đăng bài viết mới thành công!";
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    TempData["Error"] = "Lỗi hệ thống: " + ex.Message;
                }
            }
            ViewBag.Categories = new SelectList(_blogCategories);
            return View(blog);
        }

        // ==========================================
        // 3. CHỈNH SỬA BÀI VIẾT
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var blog = await _context.Blogs.FirstOrDefaultAsync(b => b.BlogID == id && !b.IsDeleted);
            if (blog == null) return NotFound();

            ViewBag.Categories = new SelectList(_blogCategories);
            return View(blog);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, Blog blog, IFormFile? imageFile)
        {
            if (id != blog.BlogID) return NotFound();

            ModelState.Remove("User");
            ModelState.Remove("ImageURL");

            if (ModelState.IsValid)
            {
                try
                {
                    if (imageFile != null && imageFile.Length > 0)
                    {
                        if (!string.IsNullOrEmpty(blog.ImageURL)) DeleteOldImage(blog.ImageURL);
                        blog.ImageURL = await SaveImage(imageFile, "covers");
                    }

                    _context.Entry(blog).Property(x => x.UserID).IsModified = false;
                    _context.Entry(blog).Property(x => x.Views).IsModified = false;
                    _context.Entry(blog).Property(x => x.CreatedAt).IsModified = false;

                    _context.Update(blog);
                    await _context.SaveChangesAsync();

                    TempData["Success"] = "Cập nhật bài viết thành công!";
                    return RedirectToAction(nameof(Index));
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!_context.Blogs.Any(e => e.BlogID == id)) return NotFound();
                    else throw;
                }
            }
            ViewBag.Categories = new SelectList(_blogCategories);
            TempData["Error"] = "Vui lòng kiểm tra lại thông tin nhập vào.";
            return View(blog);
        }

        // ==========================================
        // 4. XÓA BÀI VIẾT (XÓA MỀM)
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var blog = await _context.Blogs.FindAsync(id);
            if (blog != null)
            {
                blog.IsDeleted = true;
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã đưa bài viết vào thùng rác!";
            }
            else
            {
                TempData["Error"] = "Không tìm thấy bài viết!";
            }
            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // 5. CÁC HÀM HỖ TRỢ XỬ LÝ ẢNH
        // ==========================================
        private async Task<string> SaveImage(IFormFile file, string folder)
        {
            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowedExtensions.Contains(extension)) throw new Exception("Định dạng ảnh không hợp lệ.");

            string uploadDir = Path.Combine(_hostEnvironment.WebRootPath, "uploads", "blogs", folder);
            if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

            string fileName = Guid.NewGuid().ToString() + extension;
            string filePath = Path.Combine(uploadDir, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }
            return $"/uploads/blogs/{folder}/" + fileName;
        }

        private void DeleteOldImage(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath) || imagePath.Contains("default") || imagePath.Contains("no-image")) return;
            var fullPath = Path.Combine(_hostEnvironment.WebRootPath, imagePath.TrimStart('/'));
            if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
        }

        [HttpPost]
        public async Task<IActionResult> UploadImageContents(IFormFile file)
        {
            if (file != null && file.Length > 0)
            {
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
                var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

                if (!allowedExtensions.Contains(extension)) return Json(new { success = false, message = "Định dạng không hợp lệ." });

                string uploadDir = Path.Combine(_hostEnvironment.WebRootPath, "uploads", "blogs", "contents");
                if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

                string fileName = Guid.NewGuid().ToString() + extension;
                string filePath = Path.Combine(uploadDir, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create)) { await file.CopyToAsync(stream); }

                return Json(new { success = true, url = $"/uploads/blogs/contents/{fileName}" });
            }
            return Json(new { success = false, message = "Lỗi tải ảnh." });
        }
    }
}