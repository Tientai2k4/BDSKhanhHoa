using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class ProjectVaultController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ProjectVaultController(ApplicationDbContext context)
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

            var projects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Area)
                .Include(p => p.Ward)
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.TotalProjects = projects.Count;
            ViewBag.HasLegalDocs = projects.Count(p => !string.IsNullOrWhiteSpace(p.LegalDocs));
            ViewBag.HasImages = projects.Count(p => !string.IsNullOrWhiteSpace(p.MainImage) || !string.IsNullOrWhiteSpace(p.Thumbnail));
            ViewBag.HasContent = projects.Count(p => !string.IsNullOrWhiteSpace(p.ContentHtml));

            return View(projects);
        }
    }
}