using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class DashboardController : Controller
    {
        private readonly ApplicationDbContext _context;

        public DashboardController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            ViewBag.TotalProperties = await _context.Properties.CountAsync();
            ViewBag.PendingProperties = await _context.Properties.CountAsync(p => p.Status == "Pending");
            ViewBag.TotalUsers = await _context.Users.CountAsync();

            return View();
        }
    }
}