using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class CommentsController : Controller
    {
        private const int PageSize = 15;
        private const int BulkLimit = 500;
        private const int AutoHideLimit = 300;
        private const int BulkUserLimitPerDay = 8;
        private const int DuplicateLimit = 3;

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
            string normalizedStatus = NormalizeFilterValue(status);
            string normalizedSort = NormalizeFilterValue(dateSort);

            IQueryable<Comment> query = _context.Comments
                .AsNoTracking()
                .Include(c => c.Property)
                .Include(c => c.User)
                .Include(c => c.Replies)
                .AsQueryable();

            query = ApplySearch(query, searchString);
            query = ApplyStatusFilter(query, normalizedStatus);
            query = ApplySort(query, normalizedSort);

            int totalItems = await query.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)PageSize));

            page = Math.Clamp(page, 1, totalPages);

            List<Comment> comments = await query
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .ToListAsync();

            await LoadDashboardNumbersAsync();
            await LoadModerationInsightsAsync(comments);

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalItems = totalItems;
            ViewBag.PageSize = PageSize;
            ViewBag.SearchString = searchString ?? string.Empty;
            ViewBag.Status = normalizedStatus;
            ViewBag.DateSort = string.IsNullOrWhiteSpace(normalizedSort) ? "desc" : normalizedSort;

            return View(comments);
        }

        [HttpPost]
        public async Task<IActionResult> ApproveSelected([FromBody] List<int>? ids)
        {
            List<int> cleanIds = CleanIds(ids);

            if (!cleanIds.Any())
            {
                return JsonFail("Chưa chọn bình luận nào.");
            }

            List<Comment> comments = await _context.Comments
                .Where(c => cleanIds.Contains(c.CommentID))
                .ToListAsync();

            if (!comments.Any())
            {
                return JsonFail("Không tìm thấy bình luận cần xử lý.");
            }

            foreach (Comment comment in comments)
            {
                comment.IsHidden = false;
            }

            await _context.SaveChangesAsync();
            await WriteAuditAsync("Duyệt hàng loạt bình luận", $"Đã duyệt hiển thị {comments.Count} bình luận.", "Info");

            return JsonOk($"Đã duyệt hiển thị {comments.Count} bình luận.", new { count = comments.Count });
        }

        [HttpPost]
        public async Task<IActionResult> HideSelected([FromBody] List<int>? ids)
        {
            List<int> cleanIds = CleanIds(ids);

            if (!cleanIds.Any())
            {
                return JsonFail("Chưa chọn bình luận nào.");
            }

            List<Comment> comments = await _context.Comments
                .Where(c => cleanIds.Contains(c.CommentID))
                .ToListAsync();

            if (!comments.Any())
            {
                return JsonFail("Không tìm thấy bình luận cần xử lý.");
            }

            foreach (Comment comment in comments)
            {
                comment.IsHidden = true;
            }

            await _context.SaveChangesAsync();
            await WriteAuditAsync("Ẩn hàng loạt bình luận", $"Đã ẩn {comments.Count} bình luận.", "Warning");

            return JsonOk($"Đã ẩn {comments.Count} bình luận.", new { count = comments.Count });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteSelected([FromBody] List<int>? ids)
        {
            List<int> cleanIds = CleanIds(ids);

            if (!cleanIds.Any())
            {
                return JsonFail("Chưa chọn bình luận nào.");
            }

            List<Comment> comments = await _context.Comments
                .Include(c => c.Replies)
                .Where(c => cleanIds.Contains(c.CommentID))
                .ToListAsync();

            if (!comments.Any())
            {
                return JsonFail("Không tìm thấy bình luận cần xóa.");
            }

            HashSet<int> selectedIds = comments.Select(c => c.CommentID).ToHashSet();
            List<Comment> repliesFromSelectedParents = comments
                .Where(c => c.Replies != null && c.Replies.Any())
                .SelectMany(c => c.Replies)
                .Where(r => !selectedIds.Contains(r.CommentID))
                .ToList();

            if (repliesFromSelectedParents.Any())
            {
                _context.Comments.RemoveRange(repliesFromSelectedParents);
            }

            _context.Comments.RemoveRange(comments);
            await _context.SaveChangesAsync();

            int removedCount = comments.Count + repliesFromSelectedParents.Count;
            await WriteAuditAsync("Xóa hàng loạt bình luận", $"Đã xóa {removedCount} bình luận/phản hồi.", "Warning");

            return JsonOk($"Đã xóa {removedCount} bình luận/phản hồi.", new { count = removedCount });
        }

        [HttpPost]
        public async Task<IActionResult> HideSuspected(string mode = "risk")
        {
            string normalizedMode = NormalizeFilterValue(mode);

            IQueryable<Comment> query = _context.Comments
                .Where(c => c.IsHidden == false);

            query = normalizedMode switch
            {
                "toxic" => ApplyToxicFilter(query),
                "spam" => ApplySpamFilter(query),
                "duplicate" => ApplyDuplicateFilter(query),
                "bulk" => ApplyBulkUserFilter(query),
                _ => ApplyRiskFilter(query)
            };

            List<Comment> comments = await query
                .OrderByDescending(c => c.CreatedAt)
                .Take(AutoHideLimit)
                .ToListAsync();

            if (!comments.Any())
            {
                return JsonFail("Chưa phát hiện bình luận nghi vấn nào cần ẩn.");
            }

            foreach (Comment comment in comments)
            {
                comment.IsHidden = true;
            }

            await _context.SaveChangesAsync();

            string modeLabel = normalizedMode switch
            {
                "toxic" => "thô tục",
                "spam" => "spam/quảng cáo",
                "duplicate" => "trùng lặp",
                "bulk" => "gửi dồn số lượng lớn",
                _ => "nghi vấn"
            };

            await WriteAuditAsync(
                "Ẩn nhanh bình luận nghi vấn",
                $"Đã tự động ẩn {comments.Count} bình luận thuộc nhóm {modeLabel}.",
                "Warning");

            return JsonOk($"Đã ẩn nhanh {comments.Count} bình luận {modeLabel}.", new { count = comments.Count });
        }

        [HttpPost]
        public async Task<IActionResult> ToggleVisibility(int id)
        {
            Comment? comment = await _context.Comments.FindAsync(id);

            if (comment == null)
            {
                return JsonFail("Không tìm thấy bình luận.");
            }

            comment.IsHidden = !comment.IsHidden;
            await _context.SaveChangesAsync();

            await WriteAuditAsync(
                "Thay đổi trạng thái bình luận",
                $"CommentID: {id}, IsHidden: {comment.IsHidden}",
                "Info");

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
                return JsonFail("Không tìm thấy bình luận.");
            }

            int removedCount = 1;

            if (comment.Replies != null && comment.Replies.Any())
            {
                removedCount += comment.Replies.Count;
                _context.Comments.RemoveRange(comment.Replies);
            }

            _context.Comments.Remove(comment);
            await _context.SaveChangesAsync();

            await WriteAuditAsync("Xóa bình luận", $"CommentID: {id}, Removed: {removedCount}", "Warning");

            return JsonOk("Đã xóa bình luận.", new { count = removedCount });
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

            DateTime recentSince = DateTime.Now.AddHours(-24);
            int userRecentCount = await _context.Comments
                .AsNoTracking()
                .CountAsync(c => c.UserID == comment.UserID && c.CreatedAt >= recentSince);

            int duplicateCount = await _context.Comments
                .AsNoTracking()
                .CountAsync(c => c.Content == comment.Content);

            CommentModerationInsight insight = BuildInsight(comment, userRecentCount, duplicateCount);

            ViewBag.RiskScore = insight.Score;
            ViewBag.RiskLabel = insight.Label;
            ViewBag.RiskClass = insight.CssClass;
            ViewBag.RiskReasons = insight.ReasonText;
            ViewBag.UserRecentCount = userRecentCount;
            ViewBag.DuplicateCount = duplicateCount;

            return View(comment);
        }

        private IQueryable<Comment> ApplySearch(IQueryable<Comment> query, string? searchString)
        {
            if (string.IsNullOrWhiteSpace(searchString))
            {
                return query;
            }

            string keyword = searchString.Trim().ToLower();

            return query.Where(c =>
                c.Content.ToLower().Contains(keyword)
                || (c.User != null && c.User.FullName != null && c.User.FullName.ToLower().Contains(keyword))
                || (c.User != null && c.User.Username != null && c.User.Username.ToLower().Contains(keyword))
                || (c.Property != null && c.Property.Title != null && c.Property.Title.ToLower().Contains(keyword)));
        }

        private IQueryable<Comment> ApplyStatusFilter(IQueryable<Comment> query, string status)
        {
            return status switch
            {
                "visible" => query.Where(c => c.IsHidden == false),
                "hidden" => query.Where(c => c.IsHidden == true),
                "parent" => query.Where(c => c.ParentID == null),
                "reply" => query.Where(c => c.ParentID != null),
                "today" => query.Where(c => c.CreatedAt >= DateTime.Now.Date && c.CreatedAt < DateTime.Now.Date.AddDays(1)),
                "toxic" => ApplyToxicFilter(query),
                "spam" => ApplySpamFilter(query),
                "duplicate" => ApplyDuplicateFilter(query),
                "bulk" => ApplyBulkUserFilter(query),
                "risk" => ApplyRiskFilter(query),
                _ => query
            };
        }

        private static IQueryable<Comment> ApplySort(IQueryable<Comment> query, string dateSort)
        {
            return dateSort == "asc"
                ? query.OrderBy(c => c.CreatedAt).ThenBy(c => c.CommentID)
                : query.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.CommentID);
        }

        private IQueryable<Comment> ApplyRiskFilter(IQueryable<Comment> query)
        {
            IQueryable<int> noisyUserIds = GetNoisyUserIds();
            IQueryable<string> duplicateContents = GetDuplicateContents();

            return query.Where(c =>
                c.IsHidden == true
                || c.Content.Contains("http")
                || c.Content.Contains("www.")
                || c.Content.Contains(".com")
                || c.Content.Contains("zalo")
                || c.Content.Contains("Zalo")
                || c.Content.Contains("telegram")
                || c.Content.Contains("Telegram")
                || c.Content.Contains("t.me")
                || c.Content.Contains("casino")
                || c.Content.Contains("nhà cái")
                || c.Content.Contains("vay tiền")
                || c.Content.Contains("miễn phí")
                || c.Content.Contains("click")
                || c.Content.Contains("địt")
                || c.Content.Contains("djt")
                || c.Content.Contains("dcm")
                || c.Content.Contains("đcm")
                || c.Content.Contains("dmm")
                || c.Content.Contains("vcl")
                || c.Content.Contains("loz")
                || c.Content.Contains("fuck")
                || c.Content.Contains("shit")
                || noisyUserIds.Contains(c.UserID)
                || duplicateContents.Contains(c.Content));
        }

        private static IQueryable<Comment> ApplyToxicFilter(IQueryable<Comment> query)
        {
            return query.Where(c =>
                c.Content.Contains("địt")
                || c.Content.Contains("djt")
                || c.Content.Contains("dcm")
                || c.Content.Contains("đcm")
                || c.Content.Contains("dmm")
                || c.Content.Contains("vcl")
                || c.Content.Contains("clgt")
                || c.Content.Contains("loz")
                || c.Content.Contains("fuck")
                || c.Content.Contains("shit")
                || c.Content.Contains("mất dạy")
                || c.Content.Contains("súc vật"));
        }

        private static IQueryable<Comment> ApplySpamFilter(IQueryable<Comment> query)
        {
            return query.Where(c =>
                c.Content.Contains("http")
                || c.Content.Contains("www.")
                || c.Content.Contains(".com")
                || c.Content.Contains("zalo")
                || c.Content.Contains("Zalo")
                || c.Content.Contains("telegram")
                || c.Content.Contains("Telegram")
                || c.Content.Contains("t.me")
                || c.Content.Contains("casino")
                || c.Content.Contains("nhà cái")
                || c.Content.Contains("vay tiền")
                || c.Content.Contains("miễn phí")
                || c.Content.Contains("click")
                || c.Content.Contains("nhận ngay")
                || c.Content.Contains("cam kết"));
        }

        private IQueryable<Comment> ApplyDuplicateFilter(IQueryable<Comment> query)
        {
            IQueryable<string> duplicateContents = GetDuplicateContents();
            return query.Where(c => duplicateContents.Contains(c.Content));
        }

        private IQueryable<Comment> ApplyBulkUserFilter(IQueryable<Comment> query)
        {
            IQueryable<int> noisyUserIds = GetNoisyUserIds();
            return query.Where(c => noisyUserIds.Contains(c.UserID));
        }

        private IQueryable<int> GetNoisyUserIds()
        {
            DateTime since = DateTime.Now.AddHours(-24);

            return _context.Comments
                .AsNoTracking()
                .Where(c => c.CreatedAt >= since)
                .GroupBy(c => c.UserID)
                .Where(g => g.Count() >= BulkUserLimitPerDay)
                .Select(g => g.Key);
        }

        private IQueryable<string> GetDuplicateContents()
        {
            return _context.Comments
                .AsNoTracking()
                .Where(c => c.Content != null && c.Content != string.Empty)
                .GroupBy(c => c.Content)
                .Where(g => g.Count() >= DuplicateLimit)
                .Select(g => g.Key);
        }

        private async Task LoadDashboardNumbersAsync()
        {
            DateTime today = DateTime.Now.Date;
            DateTime tomorrow = today.AddDays(1);

            ViewBag.TotalComments = await _context.Comments.CountAsync();
            ViewBag.VisibleComments = await _context.Comments.CountAsync(c => c.IsHidden == false);
            ViewBag.HiddenComments = await _context.Comments.CountAsync(c => c.IsHidden == true);
            ViewBag.TodayComments = await _context.Comments.CountAsync(c => c.CreatedAt >= today && c.CreatedAt < tomorrow);
            ViewBag.RiskComments = await ApplyRiskFilter(_context.Comments.AsNoTracking().Where(c => c.IsHidden == false)).CountAsync();
            ViewBag.ToxicComments = await ApplyToxicFilter(_context.Comments.AsNoTracking().Where(c => c.IsHidden == false)).CountAsync();
            ViewBag.SpamComments = await ApplySpamFilter(_context.Comments.AsNoTracking().Where(c => c.IsHidden == false)).CountAsync();
            ViewBag.BulkUserComments = await ApplyBulkUserFilter(_context.Comments.AsNoTracking().Where(c => c.IsHidden == false)).CountAsync();
        }

        private async Task LoadModerationInsightsAsync(List<Comment> comments)
        {
            Dictionary<int, int> riskScores = new Dictionary<int, int>();
            Dictionary<int, string> riskLabels = new Dictionary<int, string>();
            Dictionary<int, string> riskClasses = new Dictionary<int, string>();
            Dictionary<int, string> riskReasons = new Dictionary<int, string>();

            if (!comments.Any())
            {
                ViewBag.RiskScores = riskScores;
                ViewBag.RiskLabels = riskLabels;
                ViewBag.RiskClasses = riskClasses;
                ViewBag.RiskReasons = riskReasons;
                return;
            }

            DateTime recentSince = DateTime.Now.AddHours(-24);
            List<int> userIds = comments.Select(c => c.UserID).Distinct().ToList();
            List<string> contents = comments
                .Select(c => c.Content)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct()
                .ToList();

            Dictionary<int, int> recentCounts = await _context.Comments
                .AsNoTracking()
                .Where(c => userIds.Contains(c.UserID) && c.CreatedAt >= recentSince)
                .GroupBy(c => c.UserID)
                .Select(g => new { UserID = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.UserID, x => x.Count);

            Dictionary<string, int> duplicateCounts = await _context.Comments
                .AsNoTracking()
                .Where(c => contents.Contains(c.Content))
                .GroupBy(c => c.Content)
                .Select(g => new { Content = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Content, x => x.Count);

            foreach (Comment comment in comments)
            {
                int userRecentCount = recentCounts.ContainsKey(comment.UserID) ? recentCounts[comment.UserID] : 0;
                int duplicateCount = duplicateCounts.ContainsKey(comment.Content) ? duplicateCounts[comment.Content] : 0;
                CommentModerationInsight insight = BuildInsight(comment, userRecentCount, duplicateCount);

                riskScores[comment.CommentID] = insight.Score;
                riskLabels[comment.CommentID] = insight.Label;
                riskClasses[comment.CommentID] = insight.CssClass;
                riskReasons[comment.CommentID] = insight.ReasonText;
            }

            ViewBag.RiskScores = riskScores;
            ViewBag.RiskLabels = riskLabels;
            ViewBag.RiskClasses = riskClasses;
            ViewBag.RiskReasons = riskReasons;
        }

        private static CommentModerationInsight BuildInsight(Comment comment, int userRecentCount, int duplicateCount)
        {
            string content = comment.Content ?? string.Empty;
            string normalized = NormalizeTextForModeration(content);
            List<string> reasons = new List<string>();
            int score = 0;

            if (comment.IsHidden)
            {
                score += 10;
                reasons.Add("đang ẩn");
            }

            if (ContainsToxicLanguage(normalized))
            {
                score += 70;
                reasons.Add("ngôn từ thô tục");
            }

            if (LooksLikeSpam(content, normalized))
            {
                score += 35;
                reasons.Add("spam/quảng cáo/link/số điện thoại");
            }

            if (duplicateCount >= DuplicateLimit)
            {
                score += 30;
                reasons.Add($"trùng nội dung {duplicateCount} lần");
            }

            if (userRecentCount >= BulkUserLimitPerDay)
            {
                score += 35;
                reasons.Add($"user gửi {userRecentCount} bình luận trong 24h");
            }

            if (HasRepeatedPattern(normalized))
            {
                score += 20;
                reasons.Add("lặp ký tự/từ bất thường");
            }

            if (content.Length >= 700)
            {
                score += 10;
                reasons.Add("nội dung quá dài");
            }

            score = Math.Clamp(score, 0, 100);

            string label;
            string cssClass;

            if (score >= 70)
            {
                label = "Nguy cơ cao";
                cssClass = "risk-high";
            }
            else if (score >= 40)
            {
                label = "Cần kiểm tra";
                cssClass = "risk-medium";
            }
            else if (score > 0)
            {
                label = "Theo dõi";
                cssClass = "risk-low";
            }
            else
            {
                label = "An toàn";
                cssClass = "risk-safe";
                reasons.Add("chưa phát hiện dấu hiệu bất thường");
            }

            return new CommentModerationInsight
            {
                Score = score,
                Label = label,
                CssClass = cssClass,
                ReasonText = string.Join(", ", reasons)
            };
        }

        private static bool ContainsToxicLanguage(string normalized)
        {
            string text = $" {normalized} ";
            string[] toxicTokens =
            {
                " dit ", " djt ", " dcm ", " dmm ", " dm ", " vcl ", " clgt ",
                " loz ", " fuck ", " shit ", " mat day ", " suc vat ", " cai lon "
            };

            return toxicTokens.Any(text.Contains);
        }

        private static bool LooksLikeSpam(string original, string normalized)
        {
            string lower = original.ToLowerInvariant();
            string compactNumber = Regex.Replace(original, "\\D", string.Empty);

            if (lower.Contains("http")
                || lower.Contains("www.")
                || lower.Contains(".com")
                || normalized.Contains("zalo")
                || normalized.Contains("telegram")
                || normalized.Contains("t.me")
                || normalized.Contains("casino")
                || normalized.Contains("nha cai")
                || normalized.Contains("vay tien")
                || normalized.Contains("mien phi")
                || normalized.Contains("nhan ngay")
                || normalized.Contains("cam ket"))
            {
                return true;
            }

            return compactNumber.Length >= 10 && compactNumber.StartsWith("0");
        }

        private static bool HasRepeatedPattern(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (Regex.IsMatch(normalized, @"([a-z0-9])\1{5,}", RegexOptions.IgnoreCase))
            {
                return true;
            }

            return Regex.IsMatch(normalized, @"\b([a-z0-9]{2,})\b(?:\s+\1\b){2,}", RegexOptions.IgnoreCase);
        }

        private static string NormalizeTextForModeration(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string lower = text.ToLowerInvariant().Trim();
            string formD = lower.Normalize(NormalizationForm.FormD);
            StringBuilder builder = new StringBuilder(formD.Length);

            foreach (char ch in formD)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            string normalized = builder.ToString().Normalize(NormalizationForm.FormC);
            normalized = normalized.Replace('đ', 'd');
            normalized = Regex.Replace(normalized, @"\s+", " ");

            return normalized;
        }

        private static string NormalizeFilterValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }

        private static List<int> CleanIds(List<int>? ids)
        {
            return ids == null
                ? new List<int>()
                : ids.Where(id => id > 0).Distinct().Take(BulkLimit).ToList();
        }

        private async Task WriteAuditAsync(string action, string description, string severity)
        {
            int userId = GetCurrentUserId();

            await _auditLogService.LogAsync(
                userId,
                action,
                "Comments",
                description,
                severity: severity);
        }

        private JsonResult JsonOk(string message, object? extra = null)
        {
            if (extra == null)
            {
                return Json(new { success = true, message });
            }

            return Json(new { success = true, message, data = extra });
        }

        private JsonResult JsonFail(string message)
        {
            return Json(new { success = false, message });
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

        private sealed class CommentModerationInsight
        {
            public int Score { get; set; }
            public string Label { get; set; } = "An toàn";
            public string CssClass { get; set; } = "risk-safe";
            public string ReasonText { get; set; } = string.Empty;
        }
    }
}
