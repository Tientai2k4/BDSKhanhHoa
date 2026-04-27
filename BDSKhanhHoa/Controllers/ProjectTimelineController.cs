using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class ProjectTimelineController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ProjectTimelineController(ApplicationDbContext context)
        {
            _context = context;
        }

        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (!TryGetCurrentUserId(out int userId))
                return Challenge();

            var businessProfile = await _context.BusinessProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.UserID == userId);

            var projects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Area)
                .Include(p => p.Ward)
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.BusinessName = businessProfile?.BusinessName ?? "Doanh nghiệp đối tác";
            ViewBag.TotalProjects = projects.Count;
            ViewBag.ActiveProjects = projects.Count(p => p.ProjectStatus == "Đang mở bán");
            ViewBag.CompletedProjects = projects.Count(p => p.ProjectStatus == "Đã bàn giao");
            ViewBag.PendingProjects = projects.Count(p => p.ApprovalStatus != "Approved");

            return View(projects);
        }
    }
}