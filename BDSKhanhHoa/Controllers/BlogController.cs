using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;

namespace BDSKhanhHoa.Controllers
{
    public class BlogController : Controller
    {
        private readonly ApplicationDbContext _context;

        public BlogController(ApplicationDbContext context)
        {
            _context = context;
        }

        // Danh sách Blog cho khách xem
        public async Task<IActionResult> Index()
        {
            var blogs = await _context.Blogs
                .Include(b => b.User)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();
            return View(blogs);
        }

        // Chi tiết bài viết
        public async Task<IActionResult> Details(int id)
        {
            var blog = await _context.Blogs
                .Include(b => b.User)
                .FirstOrDefaultAsync(m => m.BlogID == id);

            if (blog == null) return NotFound();

            // Tăng lượt xem (Logic đơn giản)
            blog.Views++;
            _context.Update(blog);
            await _context.SaveChangesAsync();

            return View(blog);
        }
    }
}