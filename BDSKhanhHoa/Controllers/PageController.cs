using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Controllers
{
    public class PageController : Controller
    {
        private readonly ApplicationDbContext _context;

        public PageController(ApplicationDbContext context)
        {
            _context = context;
        }

        // Định tuyến linh hoạt: /Page/privacy, /Page/faq, /Page/contact...
        [Route("Page/{pageKey}")]
        public async Task<IActionResult> Index(string pageKey)
        {
            if (string.IsNullOrEmpty(pageKey))
            {
                return NotFound();
            }

            // Truy vấn lấy nội dung trang tĩnh từ Database dựa vào pageKey (URL)
            var page = await _context.StaticPages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PageKey.ToLower() == pageKey.ToLower());

            if (page == null)
            {
                // Nếu không tìm thấy trang tĩnh trong DB, trả về giao diện lỗi 404
                return View("NotFound");
            }

            return View(page);
        }
    }
}