using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

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
            string? userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        public class MilestoneItem
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public DateTime Date { get; set; } = DateTime.Now;
            public string Title { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        private static List<MilestoneItem> ReadMilestones(string? timelineJson)
        {
            if (string.IsNullOrWhiteSpace(timelineJson))
            {
                return new List<MilestoneItem>();
            }

            try
            {
                var milestones = JsonSerializer.Deserialize<List<MilestoneItem>>(timelineJson, JsonOptions)
                    ?? new List<MilestoneItem>();

                foreach (var item in milestones)
                {
                    if (string.IsNullOrWhiteSpace(item.Id))
                    {
                        item.Id = Guid.NewGuid().ToString("N");
                    }

                    item.Title = item.Title?.Trim() ?? string.Empty;
                    item.Description = item.Description?.Trim() ?? string.Empty;

                    if (item.Date == DateTime.MinValue)
                    {
                        item.Date = DateTime.Now;
                    }
                }

                return milestones
                    .Where(x => !string.IsNullOrWhiteSpace(x.Title) || !string.IsNullOrWhiteSpace(x.Description))
                    .OrderByDescending(x => x.Date)
                    .ToList();
            }
            catch
            {
                return new List<MilestoneItem>();
            }
        }

        private static string WriteMilestones(List<MilestoneItem> milestones)
        {
            var cleanList = milestones
                .Where(x => !string.IsNullOrWhiteSpace(x.Title) && !string.IsNullOrWhiteSpace(x.Description))
                .OrderByDescending(x => x.Date)
                .ToList();

            return JsonSerializer.Serialize(cleanList, JsonOptions);
        }

        private async Task<Project?> GetOwnedProjectAsync(int projectId, int userId, bool tracking = true)
        {
            IQueryable<Project> query = _context.Projects;

            if (!tracking)
            {
                query = query.AsNoTracking();
            }

            return await query
                .Include(p => p.Area)
                .Include(p => p.Ward)
                .FirstOrDefaultAsync(p =>
                    p.ProjectID == projectId &&
                    p.OwnerUserID == userId &&
                    !p.IsDeleted);
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return RedirectToAction("Login", "Account");
            }

            var projects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Area)
                .Include(p => p.Ward)
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted)
                .OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt)
                .ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.TotalProjects = projects.Count;
            ViewBag.ActiveProjects = projects.Count(p => p.ProjectStatus == "Đang mở bán");
            ViewBag.CompletedProjects = projects.Count(p => p.ProjectStatus == "Đã bàn giao" || p.ProjectStatus == "Hoàn thành");
            ViewBag.PendingProjects = projects.Count(p => p.ProjectStatus == "Sắp mở bán" || p.ProjectStatus == "Đang xây dựng");

            return View(projects);
        }

        [HttpGet]
        public async Task<IActionResult> ManageTimeline(int id)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return RedirectToAction("Login", "Account");
            }

            var project = await GetOwnedProjectAsync(id, userId, tracking: false);

            if (project == null)
            {
                TempData["Error"] = "Dự án không tồn tại hoặc bạn không có quyền quản lý tiến độ dự án này.";
                return RedirectToAction(nameof(Index));
            }

            var milestones = ReadMilestones(project.TimelineJson);

            ViewBag.Milestones = milestones;
            ViewBag.TotalMilestones = milestones.Count;
            ViewBag.LatestMilestone = milestones.OrderByDescending(x => x.Date).FirstOrDefault();

            return View(project);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddMilestone(
            int projectId,
            DateTime? milestoneDate,
            string? title,
            string? description)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return RedirectToAction("Login", "Account");
            }

            title = title?.Trim() ?? string.Empty;
            description = description?.Trim() ?? string.Empty;

            if (projectId <= 0)
            {
                TempData["Error"] = "Dữ liệu dự án không hợp lệ.";
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                TempData["Error"] = "Vui lòng nhập tiêu đề mốc tiến độ.";
                return RedirectToAction(nameof(ManageTimeline), new { id = projectId });
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                TempData["Error"] = "Vui lòng nhập nội dung chi tiết của mốc tiến độ.";
                return RedirectToAction(nameof(ManageTimeline), new { id = projectId });
            }

            if (title.Length > 180)
            {
                title = title.Substring(0, 180);
            }

            if (description.Length > 3000)
            {
                description = description.Substring(0, 3000);
            }

            var project = await GetOwnedProjectAsync(projectId, userId, tracking: true);

            if (project == null)
            {
                TempData["Error"] = "Dự án không tồn tại hoặc bạn không có quyền cập nhật.";
                return RedirectToAction(nameof(Index));
            }

            var milestones = ReadMilestones(project.TimelineJson);

            milestones.Add(new MilestoneItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Date = milestoneDate?.Date.Add(DateTime.Now.TimeOfDay) ?? DateTime.Now,
                Title = title,
                Description = description
            });

            project.TimelineJson = WriteMilestones(milestones);
            project.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã thêm mốc tiến độ dự án thành công.";
            return RedirectToAction(nameof(ManageTimeline), new { id = projectId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateMilestone(
            int projectId,
            string milestoneId,
            DateTime? milestoneDate,
            string? title,
            string? description)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return RedirectToAction("Login", "Account");
            }

            title = title?.Trim() ?? string.Empty;
            description = description?.Trim() ?? string.Empty;
            milestoneId = milestoneId?.Trim() ?? string.Empty;

            if (projectId <= 0 || string.IsNullOrWhiteSpace(milestoneId))
            {
                TempData["Error"] = "Dữ liệu mốc tiến độ không hợp lệ.";
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
            {
                TempData["Error"] = "Vui lòng nhập đầy đủ tiêu đề và nội dung tiến độ.";
                return RedirectToAction(nameof(ManageTimeline), new { id = projectId });
            }

            if (title.Length > 180)
            {
                title = title.Substring(0, 180);
            }

            if (description.Length > 3000)
            {
                description = description.Substring(0, 3000);
            }

            var project = await GetOwnedProjectAsync(projectId, userId, tracking: true);

            if (project == null)
            {
                TempData["Error"] = "Dự án không tồn tại hoặc bạn không có quyền cập nhật.";
                return RedirectToAction(nameof(Index));
            }

            var milestones = ReadMilestones(project.TimelineJson);
            var milestone = milestones.FirstOrDefault(x => x.Id == milestoneId);

            if (milestone == null)
            {
                TempData["Error"] = "Không tìm thấy mốc tiến độ cần sửa.";
                return RedirectToAction(nameof(ManageTimeline), new { id = projectId });
            }

            milestone.Title = title;
            milestone.Description = description;
            milestone.Date = milestoneDate?.Date.Add(DateTime.Now.TimeOfDay) ?? milestone.Date;

            project.TimelineJson = WriteMilestones(milestones);
            project.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã cập nhật mốc tiến độ thành công.";
            return RedirectToAction(nameof(ManageTimeline), new { id = projectId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMilestone(int projectId, string milestoneId)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return RedirectToAction("Login", "Account");
            }

            milestoneId = milestoneId?.Trim() ?? string.Empty;

            if (projectId <= 0 || string.IsNullOrWhiteSpace(milestoneId))
            {
                TempData["Error"] = "Dữ liệu xóa không hợp lệ.";
                return RedirectToAction(nameof(Index));
            }

            var project = await GetOwnedProjectAsync(projectId, userId, tracking: true);

            if (project == null)
            {
                TempData["Error"] = "Dự án không tồn tại hoặc bạn không có quyền xóa mốc tiến độ.";
                return RedirectToAction(nameof(Index));
            }

            var milestones = ReadMilestones(project.TimelineJson);
            var itemToRemove = milestones.FirstOrDefault(x => x.Id == milestoneId);

            if (itemToRemove == null)
            {
                TempData["Error"] = "Không tìm thấy mốc tiến độ cần xóa.";
                return RedirectToAction(nameof(ManageTimeline), new { id = projectId });
            }

            milestones.Remove(itemToRemove);

            project.TimelineJson = WriteMilestones(milestones);
            project.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã xóa mốc tiến độ thành công.";
            return RedirectToAction(nameof(ManageTimeline), new { id = projectId });
        }
    }
}