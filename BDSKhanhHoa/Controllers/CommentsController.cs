using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class CommentsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public CommentsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // 1. GIAO DIỆN TƯƠNG TÁC (CÓ BỘ LỌC TÌM KIẾM)
        // ==========================================
        public async Task<IActionResult> MyPropertyComments(string searchString, string dateSort = "desc", int page = 1)
        {
            if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int currentUserId))
                return RedirectToAction("Login", "Account");

            int pageSize = 10;

            var query = _context.Comments
                .Include(c => c.Property)
                .Include(c => c.User)
                .Include(c => c.Replies).ThenInclude(r => r.User)
                .Where(c => c.Property != null && c.Property.UserID == currentUserId && c.ParentID == null)
                .AsQueryable();

            if (!string.IsNullOrEmpty(searchString))
            {
                var lowerSearch = searchString.ToLower();
                query = query.Where(c => (c.Content != null && c.Content.ToLower().Contains(lowerSearch)) ||
                                         (c.User != null && c.User.FullName.ToLower().Contains(lowerSearch)));
            }

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
            ViewBag.DateSort = dateSort;
            ViewBag.CurrentUserId = currentUserId;

            return View(comments);
        }

        [HttpPost]
        public async Task<IActionResult> Reply(int parentId, int propertyId, string content)
        {
            if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int currentUserId))
                return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            if (string.IsNullOrWhiteSpace(content))
                return Json(new { success = false, message = "Nội dung trả lời không được để trống!" });

            var replyComment = new Comment
            {
                PropertyID = propertyId,
                UserID = currentUserId,
                ParentID = parentId,
                Content = content,
                CreatedAt = DateTime.Now,
                IsHidden = false
            };

            _context.Comments.Add(replyComment);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Đã gửi câu trả lời thành công!" });
        }

        // ==========================================
        // 2. CHỈ ĐƯỢC XÓA BÌNH LUẬN CỦA CHÍNH MÌNH
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int currentUserId))
                return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            var comment = await _context.Comments.Include(c => c.Replies).FirstOrDefaultAsync(c => c.CommentID == id);
            if (comment == null) return Json(new { success = false, message = "Không tìm thấy dữ liệu!" });

            // KIỂM TRA QUYỀN TRUY CẬP TRƯỚC KHI XÓA
            if (comment.UserID != currentUserId && !User.IsInRole("Admin"))
            {
                return Json(new { success = false, message = "Lỗi quyền: Bạn không thể xóa bình luận của người khác!" });
            }

            if (comment.Replies.Any()) _context.Comments.RemoveRange(comment.Replies);

            _context.Comments.Remove(comment);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Đã xóa bình luận thành công!" });
        }
    }
}