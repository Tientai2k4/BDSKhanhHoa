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
                .Include(p => p.User)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.IsDeleted == false)
                .AsQueryable();

            if (!string.IsNullOrEmpty(status))
            {
                query = query.Where(p => p.ApprovalStatus == status);
            }

            var projects = await query
                .OrderBy(p => p.ApprovalStatus == "Pending" ? 0 : 1) // Ưu tiên xếp các dự án đang chờ duyệt lên đầu
                .ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.CurrentStatus = status;

            // Đếm số lượng để hiển thị Badge thống kê nhanh
            ViewBag.PendingCount = await _context.Projects.CountAsync(p => p.ApprovalStatus == "Pending" && p.IsDeleted == false);
            ViewBag.ApprovedCount = await _context.Projects.CountAsync(p => p.ApprovalStatus == "Approved" && p.IsDeleted == false);

            return View(projects);
        }

        // ==========================================
        // 2. MÀN HÌNH KIỂM DUYỆT NHANH (VERIFY)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Verify()
        {
            // Tái sử dụng lại hàm Index nhưng tự động gán status = "Pending"
            return await Index("Pending");
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

                if (newStatus == "Approved")
                {
                    TempData["Success"] = $"Đã duyệt dự án: {project.ProjectName}. Dự án hiện đã public trên hệ thống.";
                }
                else if (newStatus == "Rejected")
                {
                    TempData["Error"] = $"Đã TỪ CHỐI hồ sơ dự án: {project.ProjectName} do pháp lý không đảm bảo.";
                }
            }

            // Quay lại trang gốc (nếu đang ở trang Verify thì về Verify, nếu ở Index thì về Index)
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
                await _context.SaveChangesAsync();

                // Đồng thời set thuộc tính ProjectID của các Tin đăng liên quan về NULL để tránh lỗi dữ liệu
                var linkedProperties = await _context.Properties.Where(p => p.ProjectID == id).ToListAsync();
                foreach (var prop in linkedProperties)
                {
                    prop.ProjectID = null;
                }
                await _context.SaveChangesAsync();

                TempData["Success"] = "Đã xóa dự án thành công khỏi hệ thống!";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}