using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class ConsultationsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ConsultationsController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            var consultations = await _context.Consultations
                .Include(c => c.Property) // Kéo theo thông tin nhà đất mà khách đang hỏi
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            ViewBag.NewLeads = consultations.Count(c => c.Status == "New");
            ViewData["Title"] = "Quản lý Khách hàng Tiềm năng (Leads)";
            return View(consultations);
        }

        [HttpPost]
        public async Task<IActionResult> UpdateStatus(int id, string status)
        {
            var consult = await _context.Consultations.FindAsync(id);
            if (consult != null)
            {
                consult.Status = status; // Trạng thái: New, Contacted, Resolved
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã cập nhật trạng thái tư vấn!";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}