using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class UserMessagesController : Controller
    {
        private readonly ApplicationDbContext _context;

        public UserMessagesController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? keyword, int? propertyId, int page = 1)
        {
            const int pageSize = 15;

            keyword = string.IsNullOrWhiteSpace(keyword) ? "" : keyword.Trim();

            IQueryable<UserMessage> query = _context.UserMessages
                .AsNoTracking()
                .Include(m => m.Sender)
                .Include(m => m.Receiver)
                .Include(m => m.Property);

            if (propertyId.HasValue && propertyId.Value > 0)
            {
                query = query.Where(m => m.PropertyID == propertyId.Value);
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                query = query.Where(m =>
                    (m.MessageContent != null && EF.Functions.Like(m.MessageContent, $"%{keyword}%")) ||

                    (m.Sender != null &&
                        (
                            EF.Functions.Like(m.Sender.FullName ?? "", $"%{keyword}%") ||
                            EF.Functions.Like(m.Sender.Username ?? "", $"%{keyword}%") ||
                            EF.Functions.Like(m.Sender.Phone ?? "", $"%{keyword}%") ||
                            EF.Functions.Like(m.Sender.Email ?? "", $"%{keyword}%")
                        )
                    ) ||

                    (m.Receiver != null &&
                        (
                            EF.Functions.Like(m.Receiver.FullName ?? "", $"%{keyword}%") ||
                            EF.Functions.Like(m.Receiver.Username ?? "", $"%{keyword}%") ||
                            EF.Functions.Like(m.Receiver.Phone ?? "", $"%{keyword}%") ||
                            EF.Functions.Like(m.Receiver.Email ?? "", $"%{keyword}%")
                        )
                    ) ||

                    (m.Property != null && EF.Functions.Like(m.Property.Title ?? "", $"%{keyword}%"))
                );
            }

            List<UserMessage> allMessages = await query
                .OrderByDescending(m => m.CreatedAt)
                .ToListAsync();

            var pendingReportDict = await _context.ConversationReports
                .AsNoTracking()
                .Where(r => r.Status == "Pending")
                .GroupBy(r => new
                {
                    UserA = r.ReporterID < r.ReportedUserID ? r.ReporterID : r.ReportedUserID,
                    UserB = r.ReporterID < r.ReportedUserID ? r.ReportedUserID : r.ReporterID,
                    r.PropertyID
                })
                .Select(g => new
                {
                    g.Key.UserA,
                    g.Key.UserB,
                    g.Key.PropertyID,
                    Count = g.Count()
                })
                .ToDictionaryAsync(
                    x => $"{x.UserA}_{x.UserB}_{x.PropertyID}",
                    x => x.Count
                );

            var conversations = allMessages
                .GroupBy(m => new
                {
                    UserA = Math.Min(m.SenderID, m.ReceiverID),
                    UserB = Math.Max(m.SenderID, m.ReceiverID),
                    m.PropertyID
                })
                .Select(g =>
                {
                    string key = $"{g.Key.UserA}_{g.Key.UserB}_{g.Key.PropertyID}";

                    return new AdminConversationRow
                    {
                        UserAID = g.Key.UserA,
                        UserBID = g.Key.UserB,
                        PropertyID = g.Key.PropertyID,
                        LastMessage = g.OrderByDescending(x => x.CreatedAt).First(),
                        MessageCount = g.Count(),
                        AttachmentCount = g.Count(x => x.MessageType != "Text"),
                        FirstAt = g.Min(x => x.CreatedAt),
                        LastAt = g.Max(x => x.CreatedAt),
                        PendingReportCount = pendingReportDict.ContainsKey(key) ? pendingReportDict[key] : 0
                    };
                })
                .OrderByDescending(x => x.PendingReportCount)
                .ThenByDescending(x => x.LastAt)
                .ToList();

            int totalItems = conversations.Count;
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
            page = Math.Clamp(page, 1, totalPages);

            var pagedRows = conversations
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            var recentReports = await _context.ConversationReports
                .AsNoTracking()
                .Include(r => r.Reporter)
                .Include(r => r.ReportedUser)
                .Include(r => r.Property)
                .Where(r => r.Status == "Pending")
                .OrderByDescending(r => r.CreatedAt)
                .Take(8)
                .ToListAsync();

            ViewBag.Keyword = keyword;
            ViewBag.PropertyId = propertyId;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalItems = totalItems;

            ViewBag.TotalMessages = await _context.UserMessages.AsNoTracking().CountAsync();
            ViewBag.TodayMessages = await _context.UserMessages.AsNoTracking().CountAsync(m => m.CreatedAt >= DateTime.Today);
            ViewBag.PendingReports = await _context.ConversationReports.AsNoTracking().CountAsync(r => r.Status == "Pending");
            ViewBag.RecentReports = recentReports;

            return View(pagedRows);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int userAId, int userBId, int propertyId)
        {
            var messages = await _context.UserMessages
                .AsNoTracking()
                .Include(m => m.Sender)
                .Include(m => m.Receiver)
                .Include(m => m.Property)
                .Where(m =>
                    m.PropertyID == propertyId &&
                    (
                        (m.SenderID == userAId && m.ReceiverID == userBId) ||
                        (m.SenderID == userBId && m.ReceiverID == userAId)
                    ))
                .OrderBy(m => m.CreatedAt)
                .ToListAsync();

            if (!messages.Any())
            {
                TempData["Error"] = "Không tìm thấy lịch sử tin nhắn.";
                return RedirectToAction(nameof(Index));
            }

            ViewBag.UserAId = userAId;
            ViewBag.UserBId = userBId;
            ViewBag.PropertyId = propertyId;
            ViewBag.PropertyTitle = messages.First().Property?.Title ?? "Bất động sản";
            ViewBag.MessageCount = messages.Count;
            ViewBag.AttachmentCount = messages.Count(x => x.MessageType != "Text");

            return View(messages);
        }

        public class AdminConversationRow
        {
            public int UserAID { get; set; }
            public int UserBID { get; set; }
            public int PropertyID { get; set; }

            public UserMessage LastMessage { get; set; } = new UserMessage();

            public int MessageCount { get; set; }
            public int AttachmentCount { get; set; }
            public int PendingReportCount { get; set; }

            public DateTime FirstAt { get; set; }
            public DateTime LastAt { get; set; }
        }
    }
}