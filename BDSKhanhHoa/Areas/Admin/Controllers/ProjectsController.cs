using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class ProjectsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ProjectsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // 1. TỔNG HỢP DANH SÁCH DỰ ÁN & LỌC TRẠNG THÁI
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index(string status = "")
        {
            var query = _context.Projects
                .Include(p => p.Owner)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.IsDeleted == false)
                .AsQueryable();

            if (!string.IsNullOrEmpty(status))
            {
                query = query.Where(p => p.ApprovalStatus == status);
            }

            var projects = await query
                .OrderBy(p => p.ApprovalStatus == "Pending" ? 0 : 1)
                .ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.CurrentStatus = status;
            ViewBag.PendingCount = await _context.Projects.CountAsync(p => p.ApprovalStatus == "Pending" && p.IsDeleted == false);
            ViewBag.ApprovedCount = await _context.Projects.CountAsync(p => p.ApprovalStatus == "Approved" && p.IsDeleted == false);

            return View(projects);
        }

        // ==========================================
        // 2. MÀN HÌNH KIỂM DUYỆT NHANH (SỬA LỖI TẠI ĐÂY)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Verify()
        {
            // Lấy danh sách chỉ có trạng thái Pending
            var pendingProjects = await _context.Projects
                .Include(p => p.Owner)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.ApprovalStatus == "Pending" && p.IsDeleted == false)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.CurrentStatus = "Pending";
            ViewBag.PendingCount = pendingProjects.Count;
            ViewBag.ApprovedCount = await _context.Projects.CountAsync(p => p.ApprovalStatus == "Approved" && p.IsDeleted == false);

            // BẮT BUỘC TRẢ VỀ VIEW "Index" CÙNG VỚI DATA
            return View("Index", pendingProjects);
        }

        // ==========================================
        // 3. XỬ LÝ CẬP NHẬT TRẠNG THÁI PHÊ DUYỆT
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string newStatus)
        {
            var project = await _context.Projects.FindAsync(id);
            if (project == null)
            {
                TempData["Error"] = "Không tìm thấy dữ liệu dự án trên hệ thống!";
                return RedirectToAction(nameof(Index));
            }

            if (newStatus == "Approved" || newStatus == "Rejected" || newStatus == "Pending")
            {
                project.ApprovalStatus = newStatus;
                project.UpdatedAt = DateTime.Now;
                await _context.SaveChangesAsync();

                if (newStatus == "Approved") TempData["Success"] = $"Đã phê duyệt dự án: {project.ProjectName}.";
                else if (newStatus == "Rejected") TempData["Error"] = $"Đã từ chối dự án: {project.ProjectName}.";
            }

            string referer = Request.Headers["Referer"].ToString();
            if (!string.IsNullOrEmpty(referer)) return Redirect(referer);
            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // 4. XÓA BỎ DỰ ÁN (XÓA MỀM)
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var project = await _context.Projects.FindAsync(id);
            if (project != null)
            {
                project.IsDeleted = true;
                project.UpdatedAt = DateTime.Now;

                var linkedProperties = await _context.Properties.Where(p => p.ProjectID == id).ToListAsync();
                foreach (var prop in linkedProperties) prop.ProjectID = null;

                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã xóa dự án thành công khỏi hệ thống!";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}