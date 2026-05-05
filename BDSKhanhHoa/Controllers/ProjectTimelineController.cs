using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json; // Bổ sung thư viện này để xử lý JSON

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

        // --- KHAI BÁO CLASS ẢO (DTO) NGAY TRONG CONTROLLER TRÁNH TẠO MODEL MỚI ---
        public class MilestoneItem
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N"); // Tạo ID ảo độc nhất
            public DateTime Date { get; set; } = DateTime.Now;
            public string Title { get; set; }
            public string Description { get; set; }
        }
        // -------------------------------------------------------------------------

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            var projects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Area)
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            return View(projects);
        }

        // 1. HIỂN THỊ CHI TIẾT TIẾN ĐỘ DỰ ÁN
        [HttpGet]
        public async Task<IActionResult> ManageTimeline(int id)
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            var project = await _context.Projects
                .FirstOrDefaultAsync(p => p.ProjectID == id && p.OwnerUserID == userId && !p.IsDeleted);

            if (project == null) return NotFound("Dự án không tồn tại hoặc bạn không có quyền.");

            // Đọc và Giải mã chuỗi JSON từ Database thành Danh sách (List)
            var milestones = new List<MilestoneItem>();
            if (!string.IsNullOrWhiteSpace(project.TimelineJson))
            {
                try
                {
                    milestones = JsonSerializer.Deserialize<List<MilestoneItem>>(project.TimelineJson) ?? new List<MilestoneItem>();
                }
                catch
                {
                    // Lỗi parse JSON thì trả về list rỗng
                }
            }

            // Truyền danh sách qua ViewBag, sắp xếp ngày mới nhất lên đầu
            ViewBag.Milestones = milestones.OrderByDescending(m => m.Date).ToList();

            return View(project);
        }

        // 2. THÊM MỐC TIẾN ĐỘ MỚI
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddMilestone(int projectId, string title, string description)
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            var project = await _context.Projects.FirstOrDefaultAsync(p => p.ProjectID == projectId && p.OwnerUserID == userId);
            if (project == null) return Unauthorized("Lỗi bảo mật.");

            try
            {
                // Lấy list cũ ra
                var milestones = new List<MilestoneItem>();
                if (!string.IsNullOrWhiteSpace(project.TimelineJson))
                {
                    milestones = JsonSerializer.Deserialize<List<MilestoneItem>>(project.TimelineJson) ?? new List<MilestoneItem>();
                }

                // Thêm cái mới vào
                milestones.Add(new MilestoneItem
                {
                    Title = title.Trim(),
                    Description = description.Trim(),
                    Date = DateTime.Now
                });

                // Đóng gói lại thành chuỗi JSON và Lưu DB
                project.TimelineJson = JsonSerializer.Serialize(milestones);
                project.UpdatedAt = DateTime.Now;

                _context.Update(project);
                await _context.SaveChangesAsync();

                TempData["Success"] = "Cập nhật mốc tiến độ dự án thành công.";
            }
            catch (Exception)
            {
                TempData["Error"] = "Có lỗi xảy ra, vui lòng thử lại.";
            }

            return RedirectToAction(nameof(ManageTimeline), new { id = projectId });
        }

        // 3. XÓA MỐC TIẾN ĐỘ (Phòng khi CDT nhập sai)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMilestone(int projectId, string milestoneId)
        {
            if (!TryGetCurrentUserId(out int userId)) return Challenge();

            var project = await _context.Projects.FirstOrDefaultAsync(p => p.ProjectID == projectId && p.OwnerUserID == userId);
            if (project == null) return Unauthorized("Lỗi bảo mật.");

            try
            {
                if (!string.IsNullOrWhiteSpace(project.TimelineJson))
                {
                    var milestones = JsonSerializer.Deserialize<List<MilestoneItem>>(project.TimelineJson);

                    // Xóa item có ID khớp
                    var itemToRemove = milestones?.FirstOrDefault(m => m.Id == milestoneId);
                    if (itemToRemove != null)
                    {
                        milestones.Remove(itemToRemove);
                        // Cập nhật lại chuỗi JSON
                        project.TimelineJson = JsonSerializer.Serialize(milestones);
                        _context.Update(project);
                        await _context.SaveChangesAsync();

                        TempData["Success"] = "Đã xóa mốc tiến độ thành công.";
                    }
                }
            }
            catch (Exception)
            {
                TempData["Error"] = "Lỗi khi xóa dữ liệu.";
            }

            return RedirectToAction(nameof(ManageTimeline), new { id = projectId });
        }
    }
}