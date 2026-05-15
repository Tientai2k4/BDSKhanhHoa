using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class CommentsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditLogService _auditLogService;

        public CommentsController(ApplicationDbContext context, IAuditLogService auditLogService)
        {
            _context = context;
            _auditLogService = auditLogService;
        }

        public async Task<IActionResult> Index(
            string? searchString,
            string? status,
            string? dateSort = "desc",
            int page = 1)
        {
            const int pageSize = 12;

            IQueryable<Comment> query = _context.Comments
                .AsNoTracking()
                .Include(c => c.Property)
                .Include(c => c.User)
                .Include(c => c.Replies)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(searchString))
            {
                string keyword = searchString.Trim().ToLower();

                query = query.Where(c =>
                    c.Content.ToLower().Contains(keyword)
                    || (c.User != null && c.User.FullName != null && c.User.FullName.ToLower().Contains(keyword))
                    || (c.User != null && c.User.Username != null && c.User.Username.ToLower().Contains(keyword))
                    || (c.Property != null && c.Property.Title != null && c.Property.Title.ToLower().Contains(keyword)));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                status = status.Trim().ToLower();

                if (status == "visible")
                {
                    query = query.Where(c => c.IsHidden == false);
                }
                else if (status == "hidden")
                {
                    query = query.Where(c => c.IsHidden == true);
                }
                else if (status == "parent")
                {
                    query = query.Where(c => c.ParentID == null);
                }
                else if (status == "reply")
                {
                    query = query.Where(c => c.ParentID != null);
                }
            }

            query = dateSort == "asc"
                ? query.OrderBy(c => c.CreatedAt)
                : query.OrderByDescending(c => c.CreatedAt);

            int totalItems = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            if (totalPages <= 0)
            {
                totalPages = 1;
            }

            if (page < 1)
            {
                page = 1;
            }

            if (page > totalPages)
            {
                page = totalPages;
            }

            List<Comment> comments = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            DateTime today = DateTime.Now.Date;
            DateTime tomorrow = today.AddDays(1);

            ViewBag.TotalComments = await _context.Comments.CountAsync();
            ViewBag.VisibleComments = await _context.Comments.CountAsync(c => c.IsHidden == false);
            ViewBag.HiddenComments = await _context.Comments.CountAsync(c => c.IsHidden == true);
            ViewBag.TodayComments = await _context.Comments.CountAsync(c => c.CreatedAt >= today && c.CreatedAt < tomorrow);

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalItems = totalItems;
            ViewBag.PageSize = pageSize;

            ViewBag.SearchString = searchString ?? "";
            ViewBag.Status = status ?? "";
            ViewBag.DateSort = dateSort ?? "desc";

            return View(comments);
        }

        [HttpPost]
        public async Task<IActionResult> ApproveSelected([FromBody] List<int>? ids)
        {
            if (ids == null || !ids.Any())
            {
                return Json(new
                {
                    success = false,
                    message = "Chưa chọn bình luận nào."
                });
            }

            List<Comment> comments = await _context.Comments
                .Where(c => ids.Contains(c.CommentID))
                .ToListAsync();

            if (!comments.Any())
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy bình luận cần xử lý."
                });
            }

            foreach (Comment comment in comments)
            {
                comment.IsHidden = false;
            }

            await _context.SaveChangesAsync();

            int userId = GetCurrentUserId();

            await _auditLogService.LogAsync(
                userId,
                "Duyệt hàng loạt bình luận",
                "Comments",
                $"Đã duyệt hiển thị {comments.Count} bình luận.",
                severity: "Info");

            return Json(new
            {
                success = true,
                message = $"Đã duyệt hiển thị {comments.Count} bình luận."
            });
        }

        [HttpPost]
        public async Task<IActionResult> HideSelected([FromBody] List<int>? ids)
        {
            if (ids == null || !ids.Any())
            {
                return Json(new
                {
                    success = false,
                    message = "Chưa chọn bình luận nào."
                });
            }

            List<Comment> comments = await _context.Comments
                .Where(c => ids.Contains(c.CommentID))
                .ToListAsync();

            if (!comments.Any())
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy bình luận cần xử lý."
                });
            }

            foreach (Comment comment in comments)
            {
                comment.IsHidden = true;
            }

            await _context.SaveChangesAsync();

            int userId = GetCurrentUserId();

            await _auditLogService.LogAsync(
                userId,
                "Ẩn hàng loạt bình luận",
                "Comments",
                $"Đã ẩn {comments.Count} bình luận.",
                severity: "Warning");

            return Json(new
            {
                success = true,
                message = $"Đã ẩn {comments.Count} bình luận."
            });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteSelected([FromBody] List<int>? ids)
        {
            if (ids == null || !ids.Any())
            {
                return Json(new
                {
                    success = false,
                    message = "Chưa chọn bình luận nào."
                });
            }

            List<Comment> comments = await _context.Comments
                .Include(c => c.Replies)
                .Where(c => ids.Contains(c.CommentID))
                .ToListAsync();

            if (!comments.Any())
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy bình luận cần xóa."
                });
            }

            foreach (Comment comment in comments)
            {
                if (comment.Replies != null && comment.Replies.Any())
                {
                    _context.Comments.RemoveRange(comment.Replies);
                }
            }

            _context.Comments.RemoveRange(comments);
            await _context.SaveChangesAsync();

            int userId = GetCurrentUserId();

            await _auditLogService.LogAsync(
                userId,
                "Xóa hàng loạt bình luận",
                "Comments",
                $"Đã xóa {comments.Count} bình luận.",
                severity: "Warning");

            return Json(new
            {
                success = true,
                message = $"Đã xóa {comments.Count} bình luận."
            });
        }

        [HttpPost]
        public async Task<IActionResult> ToggleVisibility(int id)
        {
            Comment? comment = await _context.Comments.FindAsync(id);

            if (comment == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy bình luận."
                });
            }

            comment.IsHidden = !comment.IsHidden;
            await _context.SaveChangesAsync();

            int userId = GetCurrentUserId();

            await _auditLogService.LogAsync(
                userId,
                "Thay đổi trạng thái bình luận",
                "Comments",
                $"CommentID: {id}, IsHidden: {comment.IsHidden}",
                severity: "Info");

            return Json(new
            {
                success = true,
                isHidden = comment.IsHidden,
                message = comment.IsHidden ? "Đã ẩn bình luận." : "Đã cho phép hiển thị bình luận."
            });
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            Comment? comment = await _context.Comments
                .Include(c => c.Replies)
                .FirstOrDefaultAsync(c => c.CommentID == id);

            if (comment == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy bình luận."
                });
            }

            if (comment.Replies != null && comment.Replies.Any())
            {
                _context.Comments.RemoveRange(comment.Replies);
            }

            _context.Comments.Remove(comment);
            await _context.SaveChangesAsync();

            int userId = GetCurrentUserId();

            await _auditLogService.LogAsync(
                userId,
                "Xóa bình luận",
                "Comments",
                $"CommentID: {id}",
                severity: "Warning");

            return Json(new
            {
                success = true,
                message = "Đã xóa bình luận."
            });
        }

        public async Task<IActionResult> Details(int id)
        {
            Comment? comment = await _context.Comments
                .AsNoTracking()
                .Include(c => c.User)
                .Include(c => c.Property)
                .Include(c => c.Replies)
                    .ThenInclude(r => r.User)
                .FirstOrDefaultAsync(c => c.CommentID == id);

            if (comment == null)
            {
                return NotFound();
            }

            return View(comment);
        }

        private int GetCurrentUserId()
        {
            string? userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (int.TryParse(userIdValue, out int userId))
            {
                return userId;
            }

            return 0;
        }
    }
}