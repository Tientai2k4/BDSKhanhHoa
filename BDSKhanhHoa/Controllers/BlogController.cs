using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;

namespace BDSKhanhHoa.Controllers
{
    public class BlogController : Controller
    {
        private readonly ApplicationDbContext _context;

        public BlogController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // 1. DANH SÁCH BLOG (CÓ TÌM KIẾM & BỘ LỌC)
        // ==========================================
        public async Task<IActionResult> Index(string category = "", string keyword = "", int page = 1)
        {
            int pageSize = 9; // 9 bài 1 trang

            var query = _context.Blogs
                .Include(b => b.User)
                .Where(b => !b.IsDeleted)
                .AsQueryable();

            if (!string.IsNullOrEmpty(keyword))
                query = query.Where(b => b.Title.Contains(keyword) || b.Summary.Contains(keyword));

            if (!string.IsNullOrEmpty(category))
                query = query.Where(b => b.Category == category);

            // Phân trang
            int totalItems = await query.CountAsync();
            var blogs = await query
                .OrderByDescending(x => x.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Lấy danh sách các danh mục đang có trong DB (loại bỏ trùng lặp)
            ViewBag.Categories = await _context.Blogs
                .Where(b => !b.IsDeleted && !string.IsNullOrEmpty(b.Category))
                .Select(b => b.Category)
                .Distinct()
                .ToListAsync();

            ViewBag.CurrentCategory = category;
            ViewBag.Keyword = keyword;
            ViewBag.TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            ViewBag.CurrentPage = page;

            return View(blogs);
        }

        // ==========================================
        // 2. CHI TIẾT BÀI VIẾT & BÀI VIẾT LIÊN QUAN
        // ==========================================
        public async Task<IActionResult> Details(int id)
        {
            var blog = await _context.Blogs
                .Include(b => b.User)
                .FirstOrDefaultAsync(m => m.BlogID == id && !m.IsDeleted);

            if (blog == null) return NotFound();

            // Tăng lượt xem
            blog.Views++;
            _context.Update(blog);
            await _context.SaveChangesAsync();

            // Lấy danh sách 5 bài viết mới nhất cho Sidebar
            ViewBag.RecentBlogs = await _context.Blogs
                .Where(b => !b.IsDeleted && b.BlogID != id)
                .OrderByDescending(b => b.CreatedAt)
                .Take(5)
                .ToListAsync();

            // Lấy danh sách danh mục cho Sidebar
            ViewBag.Categories = await _context.Blogs
                .Where(b => !b.IsDeleted && !string.IsNullOrEmpty(b.Category))
                .Select(b => b.Category)
                .Distinct()
                .ToListAsync();

            return View(blog);
        }
    }
}