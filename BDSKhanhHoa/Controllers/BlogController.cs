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

        // ==========================================================
        // 1. DANH SÁCH BLOG
        // - Có tìm kiếm
        // - Có lọc danh mục
        // - Có phân trang
        // - Tối ưu bằng AsNoTracking
        // ==========================================================
        public async Task<IActionResult> Index(string category = "", string keyword = "", int page = 1)
        {
            const int pageSize = 9;

            category = (category ?? "").Trim();
            keyword = (keyword ?? "").Trim();

            if (page < 1)
            {
                page = 1;
            }

            var baseQuery = _context.Blogs
                .AsNoTracking()
                .Include(b => b.User)
                .Where(b => !b.IsDeleted);

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                string likeKeyword = $"%{keyword}%";

                baseQuery = baseQuery.Where(b =>
                    EF.Functions.Like(b.Title, likeKeyword) ||
                    (b.Summary != null && EF.Functions.Like(b.Summary, likeKeyword)) ||
                    EF.Functions.Like(b.Category, likeKeyword)
                );
            }

            if (!string.IsNullOrWhiteSpace(category))
            {
                baseQuery = baseQuery.Where(b => b.Category == category);
            }

            int totalItems = await baseQuery.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            if (totalPages < 1)
            {
                totalPages = 1;
            }

            if (page > totalPages)
            {
                page = totalPages;
            }

            var blogs = await baseQuery
                .OrderByDescending(b => b.CreatedAt)
                .ThenByDescending(b => b.BlogID)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var categoryGroups = await _context.Blogs
                .AsNoTracking()
                .Where(b => !b.IsDeleted && !string.IsNullOrEmpty(b.Category))
                .GroupBy(b => b.Category)
                .Select(g => new
                {
                    Name = g.Key,
                    Count = g.Count()
                })
                .OrderBy(x => x.Name)
                .ToListAsync();

            ViewBag.Categories = categoryGroups.Select(x => x.Name).ToList();
            ViewBag.CategoryCounts = categoryGroups.ToDictionary(x => x.Name, x => x.Count);

            ViewBag.CurrentCategory = category;
            ViewBag.Keyword = keyword;
            ViewBag.TotalItems = totalItems;
            ViewBag.TotalPages = totalPages;
            ViewBag.CurrentPage = page;
            ViewBag.PageSize = pageSize;

            return View(blogs);
        }

        // ==========================================================
        // 2. CHI TIẾT BÀI VIẾT
        // - Tăng lượt xem
        // - Lấy bài mới nhất
        // - Lấy bài liên quan cùng danh mục
        // - Lấy danh mục sidebar
        // ==========================================================
        public async Task<IActionResult> Details(int id)
        {
            var blog = await _context.Blogs
                .Include(b => b.User)
                .FirstOrDefaultAsync(b => b.BlogID == id && !b.IsDeleted);

            if (blog == null)
            {
                return NotFound();
            }

            blog.Views += 1;
            await _context.SaveChangesAsync();

            ViewBag.RecentBlogs = await _context.Blogs
                .AsNoTracking()
                .Where(b => !b.IsDeleted && b.BlogID != id)
                .OrderByDescending(b => b.CreatedAt)
                .ThenByDescending(b => b.BlogID)
                .Take(5)
                .ToListAsync();

            ViewBag.RelatedBlogs = await _context.Blogs
                .AsNoTracking()
                .Where(b =>
                    !b.IsDeleted &&
                    b.BlogID != id &&
                    b.Category == blog.Category
                )
                .OrderByDescending(b => b.CreatedAt)
                .ThenByDescending(b => b.BlogID)
                .Take(4)
                .ToListAsync();

            var categoryGroups = await _context.Blogs
                .AsNoTracking()
                .Where(b => !b.IsDeleted && !string.IsNullOrEmpty(b.Category))
                .GroupBy(b => b.Category)
                .Select(g => new
                {
                    Name = g.Key,
                    Count = g.Count()
                })
                .OrderBy(x => x.Name)
                .ToListAsync();

            ViewBag.Categories = categoryGroups.Select(x => x.Name).ToList();
            ViewBag.CategoryCounts = categoryGroups.ToDictionary(x => x.Name, x => x.Count);

            return View(blog);
        }
    }
}