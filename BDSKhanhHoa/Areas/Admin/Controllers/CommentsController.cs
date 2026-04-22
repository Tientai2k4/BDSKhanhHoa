using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")] // Bắt buộc phải là Admin
    public class CommentsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public CommentsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // 1. GIAO DIỆN QUẢN LÝ CHO ADMIN (CÓ BỘ LỌC)
        // ==========================================
        public async Task<IActionResult> Index(string searchString, string status, string dateSort = "desc", int page = 1)
        {
            int pageSize = 15;
            var query = _context.Comments
                .Include(c => c.Property)
                .Include(c => c.User)
                .AsQueryable();

            // Lọc theo từ khóa (Người bình luận hoặc nội dung)
            if (!string.IsNullOrEmpty(searchString))
            {
                var lowerSearch = searchString.ToLower();
                query = query.Where(c => c.Content.ToLower().Contains(lowerSearch) ||
                                         c.User.FullName.ToLower().Contains(lowerSearch));
            }

            // Lọc theo trạng thái ẩn/hiện
            if (!string.IsNullOrEmpty(status))
            {
                if (status == "hidden") query = query.Where(c => c.IsHidden == true);
                if (status == "visible") query = query.Where(c => c.IsHidden == false);
            }

            // Sắp xếp thời gian
            if (dateSort == "asc")
                query = query.OrderBy(c => c.CreatedAt);
            else
                query = query.OrderByDescending(c => c.CreatedAt);

            int totalItems = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            page = Math.Clamp(page, 1, totalPages > 0 ? totalPages : 1);

            var comments = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.SearchString = searchString;
            ViewBag.Status = status;
            ViewBag.DateSort = dateSort;

            return View(comments);
        }

        // ==========================================
        // 2. ADMIN XEM CHI TIẾT BÌNH LUẬN VÀ NGỮ CẢNH
        // ==========================================
        public async Task<IActionResult> Details(int id)
        {
            var comment = await _context.Comments
                .Include(c => c.User)
                .Include(c => c.Property)
                .Include(c => c.Replies).ThenInclude(r => r.User)
                .FirstOrDefaultAsync(c => c.CommentID == id);

            if (comment == null) return NotFound();
            return View(comment);
        }

        // ==========================================
        // 3. ADMIN: ẨN / HIỆN BÌNH LUẬN
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> ToggleVisibility(int id)
        {
            var comment = await _context.Comments.FindAsync(id);
            if (comment == null) return Json(new { success = false, message = "Không tìm thấy bình luận!" });

            comment.IsHidden = !comment.IsHidden;
            await _context.SaveChangesAsync();
            return Json(new { success = true, isHidden = comment.IsHidden });
        }

        // ==========================================
        // 4. ADMIN: XÓA BẤT KỲ BÌNH LUẬN NÀO
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var comment = await _context.Comments.Include(c => c.Replies).FirstOrDefaultAsync(c => c.CommentID == id);
            if (comment == null) return Json(new { success = false, message = "Không tìm thấy dữ liệu!" });

            if (comment.Replies.Any()) _context.Comments.RemoveRange(comment.Replies);

            _context.Comments.Remove(comment);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Đã xóa bình luận thành công!" });
        }
    }
}