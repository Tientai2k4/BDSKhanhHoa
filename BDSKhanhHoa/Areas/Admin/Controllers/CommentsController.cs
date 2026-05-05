using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class CommentsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public CommentsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // 1. INDEX: Bổ sung lọc theo "Chờ duyệt"
        public async Task<IActionResult> Index(string searchString, string status, string dateSort = "desc", int page = 1)
        {
            int pageSize = 15;
            var query = _context.Comments
                .Include(c => c.Property)
                .Include(c => c.User)
                .AsQueryable();

            if (!string.IsNullOrEmpty(searchString))
            {
                var lowerSearch = searchString.ToLower();
                query = query.Where(c => c.Content.ToLower().Contains(lowerSearch) ||
                                         c.User.FullName.ToLower().Contains(lowerSearch));
            }

            // Lọc trạng thái hiện đại hơn
            if (!string.IsNullOrEmpty(status))
            {
                switch (status)
                {
                    case "pending": query = query.Where(c => c.IsHidden == true); break; // Giả định IsHidden=true là chưa duyệt
                    case "visible": query = query.Where(c => c.IsHidden == false); break;
                }
            }

            query = dateSort == "asc" ? query.OrderBy(c => c.CreatedAt) : query.OrderByDescending(c => c.CreatedAt);

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

        // 2. DUYỆT HÀNG LOẠT (SMART FEATURE)
        [HttpPost]
        public async Task<IActionResult> ApproveSelected([FromBody] List<int> ids)
        {
            if (ids == null || !ids.Any()) return Json(new { success = false, message = "Chưa chọn bình luận nào!" });

            var comments = await _context.Comments.Where(c => ids.Contains(c.CommentID)).ToListAsync();
            foreach (var c in comments)
            {
                c.IsHidden = false; // Hiện các bình luận đã chọn
            }
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = $"Đã duyệt {comments.Count} bình luận!" });
        }

        // 3. XÓA HÀNG LOẠT
        [HttpPost]
        public async Task<IActionResult> DeleteSelected([FromBody] List<int> ids)
        {
            if (ids == null || !ids.Any()) return Json(new { success = false, message = "Chưa chọn bình luận nào!" });

            var comments = await _context.Comments.Include(c => c.Replies).Where(c => ids.Contains(c.CommentID)).ToListAsync();

            // Xóa cả các reply liên quan
            foreach (var c in comments)
            {
                if (c.Replies.Any()) _context.Comments.RemoveRange(c.Replies);
            }

            _context.Comments.RemoveRange(comments);
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Đã xóa thành công các mục chọn!" });
        }

        [HttpPost]
        public async Task<IActionResult> ToggleVisibility(int id)
        {
            var comment = await _context.Comments.FindAsync(id);
            if (comment == null) return Json(new { success = false });
            comment.IsHidden = !comment.IsHidden;
            await _context.SaveChangesAsync();
            return Json(new { success = true, isHidden = comment.IsHidden });
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var comment = await _context.Comments.Include(c => c.Replies).FirstOrDefaultAsync(c => c.CommentID == id);
            if (comment == null) return Json(new { success = false });
            if (comment.Replies.Any()) _context.Comments.RemoveRange(comment.Replies);
            _context.Comments.Remove(comment);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

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
    }
}