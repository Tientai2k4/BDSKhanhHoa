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

        [HttpGet]
        [Route("Page/{pageKey}")]
        public async Task<IActionResult> Index(string pageKey)
        {
            if (string.IsNullOrWhiteSpace(pageKey))
            {
                return View("NotFound");
            }

            pageKey = pageKey.Trim().ToLower();

            var page = await _context.StaticPages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PageKey.ToLower() == pageKey);

            if (page == null)
            {
                return View("NotFound");
            }

            return View(page);
        }
    }
}