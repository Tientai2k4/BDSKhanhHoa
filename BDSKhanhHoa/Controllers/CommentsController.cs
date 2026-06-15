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

        // =====================================================
        // TRANG TƯƠNG TÁC KHÁCH HÀNG CỦA NGƯỜI BÁN
        // =====================================================
        public async Task<IActionResult> MyPropertyComments(
            string? searchString,
            string dateSort = "desc",
            string filter = "all",
            int? propertyId = null,
            int page = 1)
        {
            if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int currentUserId))
                return RedirectToAction("Login", "Account");

            const int pageSize = 8;

            searchString = searchString?.Trim();
            dateSort = string.IsNullOrWhiteSpace(dateSort) ? "desc" : dateSort;
            filter = string.IsNullOrWhiteSpace(filter) ? "all" : filter;

            // Danh sách tin của người bán để đổ vào combobox lọc
            var myProperties = await _context.Properties
                .AsNoTracking()
                .Where(p => p.UserID == currentUserId)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new
                {
                    p.PropertyID,
                    p.Title
                })
                .Take(300)
                .ToListAsync();

            // Query gốc: chỉ lấy bình luận cha trên tin của chính người bán
            var baseQuery = _context.Comments
                .AsNoTracking()
                .Where(c =>
                    c.ParentID == null &&
                    c.Property != null &&
                    c.Property.UserID == currentUserId);

            // Thống kê tổng quan
            int totalAll = await baseQuery.CountAsync();

            int totalAnswered = await baseQuery.CountAsync(c =>
                c.Replies.Any(r => r.UserID == currentUserId && !r.IsHidden));

            int totalUnanswered = await baseQuery.CountAsync(c =>
                !c.Replies.Any(r => r.UserID == currentUserId && !r.IsHidden));

            int totalHidden = await baseQuery.CountAsync(c =>
                c.IsHidden || c.Replies.Any(r => r.IsHidden));

            int totalPropertiesHasComments = await baseQuery
                .Select(c => c.PropertyID)
                .Distinct()
                .CountAsync();

            // Query chính có Include để hiển thị dữ liệu
            var query = _context.Comments
                .AsNoTracking()
                .Include(c => c.Property)
                .Include(c => c.User)
                .Include(c => c.Replies)
                    .ThenInclude(r => r.User)
                .Where(c =>
                    c.ParentID == null &&
                    c.Property != null &&
                    c.Property.UserID == currentUserId)
                .AsQueryable();

            // Lọc theo tin đăng
            if (propertyId.HasValue && propertyId.Value > 0)
            {
                query = query.Where(c => c.PropertyID == propertyId.Value);
            }

            // Tìm kiếm theo nội dung, tên người bình luận, tiêu đề tin, nội dung phản hồi
            if (!string.IsNullOrWhiteSpace(searchString))
            {
                string keyword = $"%{searchString}%";

                query = query.Where(c =>
                    EF.Functions.Like(c.Content ?? "", keyword) ||
                    EF.Functions.Like(c.User!.FullName ?? "", keyword) ||
                    EF.Functions.Like(c.Property!.Title ?? "", keyword) ||
                    c.Replies.Any(r =>
                        EF.Functions.Like(r.Content ?? "", keyword) ||
                        EF.Functions.Like(r.User!.FullName ?? "", keyword)
                    ));
            }

            // Bộ lọc trạng thái
            switch (filter)
            {
                case "answered":
                    query = query.Where(c => c.Replies.Any(r => r.UserID == currentUserId && !r.IsHidden));
                    break;

                case "unanswered":
                    query = query.Where(c => !c.Replies.Any(r => r.UserID == currentUserId && !r.IsHidden));
                    break;

                case "hidden":
                    query = query.Where(c => c.IsHidden || c.Replies.Any(r => r.IsHidden));
                    break;

                case "mine":
                    query = query.Where(c => c.UserID == currentUserId || c.Replies.Any(r => r.UserID == currentUserId));
                    break;

                default:
                    filter = "all";
                    break;
            }

            // Sắp xếp
            query = dateSort == "asc"
                ? query.OrderBy(c => c.CreatedAt)
                : query.OrderByDescending(c => c.CreatedAt);

            int totalItems = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            if (totalPages <= 0)
                totalPages = 1;

            page = Math.Clamp(page, 1, totalPages);

            var comments = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.CurrentUserId = currentUserId;

            ViewBag.SearchString = searchString ?? "";
            ViewBag.DateSort = dateSort;
            ViewBag.Filter = filter;
            ViewBag.PropertyId = propertyId;

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalItems = totalItems;

            ViewBag.TotalAll = totalAll;
            ViewBag.TotalAnswered = totalAnswered;
            ViewBag.TotalUnanswered = totalUnanswered;
            ViewBag.TotalHidden = totalHidden;
            ViewBag.TotalPropertiesHasComments = totalPropertiesHasComments;

            ViewBag.MyProperties = myProperties;

            return View(comments);
        }

        // =====================================================
        // NGƯỜI BÁN TRẢ LỜI BÌNH LUẬN TRÊN TIN CỦA MÌNH
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reply(int parentId, string content)
        {
            if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int currentUserId))
            {
                return Json(new
                {
                    success = false,
                    message = "Vui lòng đăng nhập để phản hồi."
                });
            }

            content = content?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(content))
            {
                return Json(new
                {
                    success = false,
                    message = "Nội dung phản hồi không được để trống."
                });
            }

            if (content.Length > 1000)
            {
                return Json(new
                {
                    success = false,
                    message = "Nội dung phản hồi tối đa 1000 ký tự."
                });
            }

            var parentComment = await _context.Comments
                .Include(c => c.Property)
                .FirstOrDefaultAsync(c => c.CommentID == parentId && c.ParentID == null);

            if (parentComment == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy bình luận cần phản hồi."
                });
            }

            if (parentComment.Property == null || parentComment.Property.UserID != currentUserId)
            {
                return Json(new
                {
                    success = false,
                    message = "Bạn không có quyền phản hồi bình luận này."
                });
            }

            if (parentComment.IsHidden)
            {
                return Json(new
                {
                    success = false,
                    message = "Bình luận này đã bị ẩn, không thể phản hồi."
                });
            }

            var replyComment = new Comment
            {
                PropertyID = parentComment.PropertyID,
                UserID = currentUserId,
                ParentID = parentComment.CommentID,
                Content = content,
                CreatedAt = DateTime.Now,
                IsHidden = false
            };

            _context.Comments.Add(replyComment);
            await _context.SaveChangesAsync();

            bool notificationCreated = false;

            try
            {
                notificationCreated = await CreateReplyNotificationIfNeededAsync(parentComment.CommentID, currentUserId, content);
            }
            catch
            {
                notificationCreated = false;
            }

            return Json(new
            {
                success = true,
                message = notificationCreated
                    ? "Đã gửi phản hồi và thông báo cho người bình luận."
                    : "Đã gửi phản hồi thành công."
            });
        }
        // =====================================================
        // TẠO THÔNG BÁO KHI CÓ NGƯỜI TRẢ LỜI BÌNH LUẬN
        // Không cần thêm cột / bảng SQL. Dùng bảng Notifications hiện có.
        // =====================================================
        private async Task<bool> CreateReplyNotificationIfNeededAsync(int parentCommentId, int replierUserId, string replyContent)
        {
            var parentComment = await _context.Comments
                .AsNoTracking()
                .Include(c => c.Property)
                .FirstOrDefaultAsync(c => c.CommentID == parentCommentId && c.ParentID == null);

            if (parentComment == null)
            {
                return false;
            }

            if (parentComment.UserID <= 0 || parentComment.UserID == replierUserId)
            {
                return false;
            }

            string propertyTitle = string.IsNullOrWhiteSpace(parentComment.Property?.Title)
                ? $"tin bất động sản #{parentComment.PropertyID}"
                : parentComment.Property.Title.Trim();

            string preview = BuildNotificationPreview(replyContent, 160);

            string content = $@"Bình luận của bạn tại tin ""{propertyTitle}"" vừa có phản hồi mới.

Nội dung phản hồi: {preview}

Bấm để xem bình luận và phản hồi lại khi cần.";

            var notification = new Notification
            {
                UserID = parentComment.UserID,
                Title = "Có người trả lời bình luận của bạn",
                Content = content,
                ActionUrl = $"/Property/Details/{parentComment.PropertyID}#comment-{parentComment.CommentID}",
                ActionText = "Xem và phản hồi",
                IsRead = false,
                CreatedAt = DateTime.Now
            };

            _context.Notifications.Add(notification);
            await _context.SaveChangesAsync();

            return true;
        }
        private static string BuildNotificationPreview(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Không có nội dung.";
            }

            string text = value.Trim();

            while (text.Contains("  "))
            {
                text = text.Replace("  ", " ");
            }

            if (text.Length > maxLength)
            {
                text = text.Substring(0, maxLength).Trim() + "...";
            }

            return text;
        }
        // =====================================================
        // CHỈ ĐƯỢC XÓA BÌNH LUẬN / PHẢN HỒI CỦA CHÍNH MÌNH
        // ADMIN ĐƯỢC XÓA TẤT CẢ
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int currentUserId))
                return Json(new { success = false, message = "Vui lòng đăng nhập." });

            var comment = await _context.Comments
                .Include(c => c.Replies)
                .FirstOrDefaultAsync(c => c.CommentID == id);

            if (comment == null)
                return Json(new { success = false, message = "Không tìm thấy bình luận." });

            bool isAdmin = User.IsInRole("Admin");
            bool isOwnerOfComment = comment.UserID == currentUserId;

            if (!isOwnerOfComment && !isAdmin)
            {
                return Json(new
                {
                    success = false,
                    message = "Bạn chỉ được xóa bình luận hoặc phản hồi do chính bạn tạo."
                });
            }

            if (comment.Replies != null && comment.Replies.Any())
            {
                _context.Comments.RemoveRange(comment.Replies);
            }

            _context.Comments.Remove(comment);
            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Đã xóa thành công."
            });
        }
    }
}