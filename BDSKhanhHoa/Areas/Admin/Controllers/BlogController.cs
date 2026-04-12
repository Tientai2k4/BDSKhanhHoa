using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.Security.Claims;
using System.IO;
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    public class BlogController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _hostEnvironment;

        public BlogController(ApplicationDbContext context, IWebHostEnvironment hostEnvironment)
        {
            _context = context;
            _hostEnvironment = hostEnvironment;
        }

        // --- 1. DANH SÁCH BÀI VIẾT ---
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var blogs = await _context.Blogs
                .Include(b => b.User)
                .OrderByDescending(b => b.CreatedAt)
                .ToListAsync();
            return View(blogs);
        }

        // --- 2. GIAO DIỆN THÊM MỚI ---
        [HttpGet]
        public IActionResult Create()
        {
            return View();
        }

        // --- 3. XỬ LÝ LƯU THÊM MỚI ---
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Blog blog, IFormFile? imageFile)
        {
            ModelState.Remove("User");
            ModelState.Remove("ImageURL");

            if (ModelState.IsValid)
            {
                if (imageFile != null && imageFile.Length > 0)
                {
                    blog.ImageURL = await SaveImage(imageFile);
                }

                // Lấy UserID, nếu chưa đăng nhập gán tạm = 1 để không lỗi
                var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
                blog.UserID = !string.IsNullOrEmpty(userIdClaim) ? int.Parse(userIdClaim) : 1;

                blog.CreatedAt = DateTime.Now;
                blog.Views = 0;

                _context.Blogs.Add(blog);
                await _context.SaveChangesAsync();

                return RedirectToAction(nameof(Index));
            }
            return View(blog);
        }

        // --- 4. GIAO DIỆN CHỈNH SỬA ---
        [HttpGet]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var blog = await _context.Blogs.FindAsync(id);
            if (blog == null) return NotFound();

            return View(blog);
        }

        // --- 5. XỬ LÝ LƯU CHỈNH SỬA ---
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
                        blog.ImageURL = await SaveImage(imageFile);
                    }

                    _context.Update(blog);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!_context.Blogs.Any(e => e.BlogID == id)) return NotFound();
                    throw;
                }
                return RedirectToAction(nameof(Index));
            }
            return View(blog);
        }

        // --- 6. XỬ LÝ XÓA ---
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var blog = await _context.Blogs.FindAsync(id);
            if (blog != null)
            {
                if (!string.IsNullOrEmpty(blog.ImageURL)) DeleteOldImage(blog.ImageURL);
                _context.Blogs.Remove(blog);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        // --- HÀM HỖ TRỢ LƯU ẢNH ---
        private async Task<string> SaveImage(IFormFile file)
        {
            string wwwRootPath = _hostEnvironment.WebRootPath;
            string fileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);
            string path = Path.Combine(wwwRootPath, "uploads", "blogs");

            if (!Directory.Exists(path)) Directory.CreateDirectory(path);

            using (var fileStream = new FileStream(Path.Combine(path, fileName), FileMode.Create))
            {
                await file.CopyToAsync(fileStream);
            }
            return "/uploads/blogs/" + fileName;
        }

        // --- HÀM HỖ TRỢ XÓA ẢNH CŨ ---
        private void DeleteOldImage(string path)
        {
            var fullPath = Path.Combine(_hostEnvironment.WebRootPath, path.TrimStart('/'));
            if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
        }
    }
}