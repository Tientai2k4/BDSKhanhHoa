using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class ChatLogsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ChatLogsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            List<ChatConversationListItemVm> conversations = new();

            List<AIChatSession> sessions = await _context.Set<AIChatSession>()
                .AsNoTracking()
                .OrderByDescending(s => s.UpdatedAt)
                .Take(500)
                .ToListAsync();

            List<int> sessionIds = sessions
                .Select(s => s.SessionID)
                .ToList();

            List<AIChatMessage> allMessages = sessionIds.Any()
                ? await _context.Set<AIChatMessage>()
                    .AsNoTracking()
                    .Where(m => sessionIds.Contains(m.SessionID))
                    .OrderBy(m => m.CreatedAt)
                    .ToListAsync()
                : new List<AIChatMessage>();

            List<int> userIds = sessions
                .Where(s => s.UserID.HasValue && s.UserID.Value > 0)
                .Select(s => s.UserID!.Value)
                .Distinct()
                .ToList();

            Dictionary<int, string> userNames = await LoadUserNamesAsync(userIds);

            foreach (AIChatSession session in sessions)
            {
                List<AIChatMessage> messages = allMessages
                    .Where(m => m.SessionID == session.SessionID)
                    .OrderBy(m => m.CreatedAt)
                    .ToList();

                if (!messages.Any())
                {
                    continue;
                }

                AIChatMessage? firstUser = messages.FirstOrDefault(m => m.Role == "user");
                AIChatMessage? lastMessage = messages.LastOrDefault();

                int questionCount = messages.Count(m => m.Role == "user");
                int answerCount = messages.Count(m => m.Role == "assistant");

                string userName = "Khách vãng lai";

                if (session.UserID.HasValue &&
                    userNames.TryGetValue(session.UserID.Value, out string? foundName))
                {
                    userName = foundName;
                }

                conversations.Add(new ChatConversationListItemVm
                {
                    Id = "ai-" + session.SessionID,
                    SessionId = session.SessionID,
                    UserId = session.UserID,
                    UserName = userName,
                    SourceLabel = "Chatbot AI",
                    SourceClass = "bg-info-subtle text-info-emphasis",
                    Scenario = string.IsNullOrWhiteSpace(session.Scenario) ? "General" : session.Scenario,
                    Stage = string.IsNullOrWhiteSpace(session.Stage) ? "" : session.Stage,
                    PageTitle = session.PageTitle ?? "",
                    PageUrl = session.PageUrl ?? "",
                    FirstUserMessage = firstUser?.Content ?? messages.First().Content ?? "",
                    LastMessage = lastMessage?.Content ?? "",
                    LastRole = lastMessage?.Role ?? "",
                    QuestionCount = questionCount,
                    AnswerCount = answerCount,
                    TotalMessages = messages.Count,
                    StartedAt = session.CreatedAt,
                    UpdatedAt = session.UpdatedAt
                });
            }

            conversations = conversations
                .OrderByDescending(c => c.UpdatedAt)
                .Take(500)
                .ToList();

            ViewBag.TotalConversations = conversations.Count;
            ViewBag.TotalMessages = conversations.Sum(c => c.TotalMessages);
            ViewBag.MemberConversations = conversations.Count(c => c.UserId.HasValue && c.UserId.Value > 0);
            ViewBag.GuestConversations = conversations.Count(c => !c.UserId.HasValue || c.UserId.Value <= 0);

            return View(conversations);
        }

        [HttpGet]
        public async Task<IActionResult> Details(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest();
            }

            if (!id.StartsWith("ai-", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest();
            }

            if (!int.TryParse(id.Replace("ai-", ""), out int sessionId) || sessionId <= 0)
            {
                return BadRequest();
            }

            AIChatSession? session = await _context.Set<AIChatSession>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SessionID == sessionId);

            if (session == null)
            {
                return NotFound();
            }

            List<AIChatMessage> messages = await _context.Set<AIChatMessage>()
                .AsNoTracking()
                .Where(m => m.SessionID == sessionId)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync();

            string userName = await GetUserNameAsync(session.UserID);

            return Json(new
            {
                id,
                source = "Chatbot AI",
                userName,
                scenario = session.Scenario ?? "General",
                stage = session.Stage ?? "",
                lastIntent = session.LastIntent ?? "",
                pageTitle = session.PageTitle ?? "",
                pageUrl = session.PageUrl ?? "",
                collectedDataJson = session.CollectedDataJson ?? "",
                startedAt = session.CreatedAt.ToString("dd/MM/yyyy HH:mm:ss"),
                updatedAt = session.UpdatedAt.ToString("dd/MM/yyyy HH:mm:ss"),
                totalMessages = messages.Count,
                messages = messages.Select(m => new
                {
                    role = m.Role ?? "",
                    content = m.Content ?? "",
                    intent = m.Intent ?? "",
                    toolTrace = m.ToolTrace ?? "",
                    time = m.CreatedAt.ToString("dd/MM/yyyy HH:mm:ss")
                }).ToList()
            });
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return RedirectToAction(nameof(Index));
            }

            if (!id.StartsWith("ai-", StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction(nameof(Index));
            }

            if (!int.TryParse(id.Replace("ai-", ""), out int sessionId) || sessionId <= 0)
            {
                return RedirectToAction(nameof(Index));
            }

            AIChatSession? session = await _context.Set<AIChatSession>()
                .FirstOrDefaultAsync(s => s.SessionID == sessionId);

            if (session == null)
            {
                TempData["Error"] = "Không tìm thấy phiên chat cần xóa.";
                return RedirectToAction(nameof(Index));
            }
            List<AIChatMessage> messages = await _context.Set<AIChatMessage>()
                .Where(x => x.SessionID == sessionId)
                .ToListAsync();
            if (messages.Any())
            {
                _context.Set<AIChatMessage>().RemoveRange(messages);
            }

            _context.Set<AIChatSession>().Remove(session);

            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã xóa toàn bộ một phiên hội thoại Chatbot AI.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAll()
        {
            List<AIChatMessage> messages = await _context.Set<AIChatMessage>()
                .ToListAsync();

            List<AIChatSession> sessions = await _context.Set<AIChatSession>()
                .ToListAsync();
            if (messages.Any())
            {
                _context.Set<AIChatMessage>().RemoveRange(messages);
            }

            if (sessions.Any())
            {
                _context.Set<AIChatSession>().RemoveRange(sessions);
            }

            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã xóa toàn bộ lịch sử Chatbot AI.";
            return RedirectToAction(nameof(Index));
        }

        private async Task<Dictionary<int, string>> LoadUserNamesAsync(List<int> userIds)
        {
            if (!userIds.Any())
            {
                return new Dictionary<int, string>();
            }

            return await _context.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.UserID))
                .Select(u => new
                {
                    u.UserID,
                    Name = !string.IsNullOrWhiteSpace(u.FullName) ? u.FullName : u.Username
                })
                .ToDictionaryAsync(
                    u => u.UserID,
                    u => string.IsNullOrWhiteSpace(u.Name) ? "Người dùng" : u.Name
                );
        }

        private async Task<string> GetUserNameAsync(int? userId)
        {
            if (!userId.HasValue || userId.Value <= 0)
            {
                return "Khách vãng lai";
            }

            var user = await _context.Users
                .AsNoTracking()
                .Where(u => u.UserID == userId.Value)
                .Select(u => new
                {
                    u.FullName,
                    u.Username
                })
                .FirstOrDefaultAsync();

            if (user == null)
            {
                return "Người dùng không tồn tại";
            }

            if (!string.IsNullOrWhiteSpace(user.FullName))
            {
                return user.FullName;
            }

            if (!string.IsNullOrWhiteSpace(user.Username))
            {
                return user.Username;
            }

            return "Người dùng";
        }

        public class ChatConversationListItemVm
        {
            public string Id { get; set; } = "";
            public int? SessionId { get; set; }
            public int? UserId { get; set; }
            public string UserName { get; set; } = "Khách vãng lai";
            public string SourceLabel { get; set; } = "Chatbot AI";
            public string SourceClass { get; set; } = "bg-info-subtle text-info-emphasis";
            public string Scenario { get; set; } = "General";
            public string Stage { get; set; } = "";
            public string PageTitle { get; set; } = "";
            public string PageUrl { get; set; } = "";
            public string FirstUserMessage { get; set; } = "";
            public string LastMessage { get; set; } = "";
            public string LastRole { get; set; } = "";
            public int QuestionCount { get; set; }
            public int AnswerCount { get; set; }
            public int TotalMessages { get; set; }
            public DateTime StartedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
        }
    }
}