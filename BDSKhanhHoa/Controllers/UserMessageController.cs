using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class UserMessageController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;

        public UserMessageController(ApplicationDbContext context, IWebHostEnvironment env)
        {
            _context = context;
            _env = env;
        }

        private int GetCurrentUserId()
        {
            string? userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int.TryParse(userIdStr, out int userId);
            return userId;
        }
        private async Task<List<int>> GetAdminAndStaffIdsAsync()
        {
            return await _context.Users
                .AsNoTracking()
                .Where(u =>
                    !u.IsDeleted &&
                    u.IsActive == true &&
                    (u.RoleID == 1 || u.RoleID == 2))
                .Select(u => u.UserID)
                .ToListAsync();
        }
        private static string SafeUserName(User? user)
        {
            return user?.FullName ?? user?.Username ?? "Người dùng";
        }

        public async Task<IActionResult> Index(int? receiverId, int? receiveId, int? propertyId)
        {
            int userId = GetCurrentUserId();

            if (userId <= 0)
            {
                return RedirectToAction("Login", "Account");
            }

            // Hỗ trợ cả URL đúng receiverId và URL cũ bị viết nhầm receiveId
            int? openPartnerId = receiverId ?? receiveId;

            var messages = await _context.UserMessages
                .AsNoTracking()
                .Include(m => m.Sender)
                .Include(m => m.Receiver)
                .Include(m => m.Property)
                .Where(m =>
                    (m.SenderID == userId && m.IsDeletedBySender == false) ||
                    (m.ReceiverID == userId && m.IsDeletedByReceiver == false))
                .OrderByDescending(m => m.CreatedAt)
                .ToListAsync();

            var conversations = messages
                .GroupBy(m => new
                {
                    PartnerId = m.SenderID == userId ? m.ReceiverID : m.SenderID,
                    m.PropertyID
                })
                .Select(g => g.OrderByDescending(x => x.CreatedAt).First())
                .OrderByDescending(m => m.CreatedAt)
                .ToList();

            /*
                Trường hợp người dùng bấm "Chat trực tiếp ngay" từ trang chi tiết tin đăng:
                - Có thể hai bên chưa từng nhắn tin.
                - Nếu chưa có lịch sử tin nhắn, danh sách bên trái chưa có cuộc trò chuyện.
                - Vì vậy cần tạo 1 dòng giả để giao diện có thể mở khung chat ngay.
                - Dòng giả này KHÔNG lưu vào CSDL, chỉ dùng để hiển thị.
            */
            if (openPartnerId.HasValue && openPartnerId.Value > 0 && propertyId.HasValue && propertyId.Value > 0)
            {
                bool existedInList = conversations.Any(m =>
                    m.PropertyID == propertyId.Value &&
                    (
                        (m.SenderID == userId && m.ReceiverID == openPartnerId.Value) ||
                        (m.SenderID == openPartnerId.Value && m.ReceiverID == userId)
                    ));

                if (!existedInList)
                {
                    var partner = await _context.Users
                        .AsNoTracking()
                        .FirstOrDefaultAsync(u => u.UserID == openPartnerId.Value && u.IsActive == true);

                    var property = await _context.Properties
                        .AsNoTracking()
                        .Include(p => p.User)
                        .FirstOrDefaultAsync(p =>
                            p.PropertyID == propertyId.Value &&
                            p.IsDeleted == false);

                    if (partner != null && property != null)
                    {
                        var virtualMessage = new UserMessage
                        {
                            MessageID = 0,
                            SenderID = openPartnerId.Value,
                            ReceiverID = userId,
                            PropertyID = property.PropertyID,
                            Sender = partner,
                            Receiver = null,
                            Property = property,
                            MessageContent = "Hãy bắt đầu cuộc trò chuyện.",
                            MessageType = "Text",
                            IsRead = true,
                            IsDeletedBySender = false,
                            IsDeletedByReceiver = false,
                            CreatedAt = DateTime.Now
                        };

                        conversations.Insert(0, virtualMessage);
                    }
                }
            }

            ViewBag.CurrentUserId = userId;
            ViewBag.OpenReceiverId = openPartnerId;
            ViewBag.OpenPropertyId = propertyId;

            return View(conversations);
        }
        [HttpGet]
        public async Task<IActionResult> GetChatHistory(int partnerId, int propertyId)
        {
            int userId = GetCurrentUserId();
            if (userId <= 0) return Unauthorized();

            bool propertyExists = await _context.Properties
                .AsNoTracking()
                .AnyAsync(p => p.PropertyID == propertyId && p.IsDeleted != true);

            if (!propertyExists) return NotFound();

            var property = await _context.Properties.AsNoTracking().FirstAsync(p => p.PropertyID == propertyId);
            bool currentUserIsSeller = property.UserID == userId;

            var chatHistory = await _context.UserMessages
                .AsNoTracking()
                .Include(m => m.Sender)
                .Where(m =>
                    m.PropertyID == propertyId &&
                    (
                        (m.SenderID == userId && m.ReceiverID == partnerId && m.IsDeletedBySender == false) ||
                        (m.SenderID == partnerId && m.ReceiverID == userId && m.IsDeletedByReceiver == false)
                    ))
                .OrderBy(m => m.CreatedAt)
                .Select(m => new
                {
                    m.MessageID,
                    m.SenderID,
                    SenderName = m.Sender!.FullName ?? m.Sender.Username,
                    SenderAvatar = m.Sender.Avatar ?? "/images/avatars/default-user.png",
                    m.MessageContent,
                    m.AttachmentUrl,
                    m.MessageType,
                    Time = m.CreatedAt.ToString("HH:mm dd/MM/yyyy"),
                    IsMine = m.SenderID == userId,
                    DirectionText = m.SenderID == userId
                        ? (currentUserIsSeller ? "Tôi trả lời" : "Tôi hỏi")
                        : (currentUserIsSeller ? "Khách hỏi" : "Người bán trả lời")
                })
                .ToListAsync();

            var unreadMessages = await _context.UserMessages
                .Where(m =>
                    m.ReceiverID == userId &&
                    m.SenderID == partnerId &&
                    m.PropertyID == propertyId &&
                    !m.IsRead &&
                    m.IsDeletedByReceiver == false)
                .ToListAsync();

            if (unreadMessages.Any())
            {
                unreadMessages.ForEach(m => m.IsRead = true);
                await _context.SaveChangesAsync();
            }

            return Json(chatHistory);
        }

        [HttpGet]
        public async Task<IActionResult> GetChatHeader(int partnerId, int propertyId)
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId <= 0) return Unauthorized();

            var partner = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserID == partnerId);
            var property = await _context.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.PropertyID == propertyId);

            if (partner == null || property == null) return NotFound();

            bool currentUserIsSeller = property.UserID == currentUserId;

            return Json(new
            {
                PartnerName = SafeUserName(partner),
                PartnerAvatar = partner.Avatar ?? "/images/avatars/default-user.png",
                PartnerPhone = partner.Phone,
                PropertyTitle = property.Title,
                PropertyPrice = property.Price.HasValue ? property.Price.Value.ToString("N0") + " đ" : "Đang cập nhật",
                IsOwner = currentUserIsSeller,
                CurrentRoleText = currentUserIsSeller ? "Bạn đang trả lời với vai trò người bán" : "Bạn đang hỏi với vai trò người mua",
                PartnerRoleText = currentUserIsSeller ? "Khách quan tâm" : "Người đăng tin",
                RoleMode = currentUserIsSeller ? "seller" : "buyer"
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendMessage([FromForm] int receiverId, [FromForm] int propertyId, [FromForm] string? messageContent, IFormFile? attachment)
        {
            int senderId = GetCurrentUserId();
            if (senderId <= 0) return Unauthorized();
            bool senderActive = await _context.Users
    .AsNoTracking()
    .AnyAsync(u => u.UserID == senderId && u.IsActive == true && !u.IsDeleted);

            if (!senderActive)
            {
                return Json(new
                {
                    success = false,
                    message = "Tài khoản của bạn đã bị khóa hoặc không còn hoạt động. Bạn không thể tiếp tục gửi tin nhắn."
                });
            }
            if (senderId == receiverId)
                return Json(new { success = false, message = "Bạn không thể tự gửi tin nhắn cho chính mình." });

            bool propertyExists = await _context.Properties.AnyAsync(p => p.PropertyID == propertyId && p.IsDeleted != true);
            if (!propertyExists)
                return Json(new { success = false, message = "Bất động sản không tồn tại hoặc đã bị xóa." });

            bool receiverExists = await _context.Users.AnyAsync(u => u.UserID == receiverId && u.IsActive == true);
            if (!receiverExists)
                return Json(new { success = false, message = "Người nhận không tồn tại hoặc tài khoản đã bị khóa." });

            if (string.IsNullOrWhiteSpace(messageContent) && attachment == null)
                return Json(new { success = false, message = "Tin nhắn không được để trống." });

            string cleanMessage = string.IsNullOrWhiteSpace(messageContent) ? "" : messageContent.Trim();
            if (cleanMessage.Length > 3000)
                return Json(new { success = false, message = "Nội dung tin nhắn không được vượt quá 3000 ký tự." });

            var newMessage = new UserMessage
            {
                SenderID = senderId,
                ReceiverID = receiverId,
                PropertyID = propertyId,
                MessageContent = string.IsNullOrWhiteSpace(cleanMessage) ? null : cleanMessage,
                IsRead = false,
                CreatedAt = DateTime.Now,
                MessageType = "Text",
                IsDeletedBySender = false,
                IsDeletedByReceiver = false
            };

            if (attachment != null && attachment.Length > 0)
            {
                const long maxFileSize = 10 * 1024 * 1024;
                if (attachment.Length > maxFileSize)
                    return Json(new { success = false, message = "Tệp đính kèm không được vượt quá 10MB." });

                string ext = Path.GetExtension(attachment.FileName).ToLowerInvariant();
                string[] allowedExts = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".txt" };

                if (!allowedExts.Contains(ext))
                    return Json(new { success = false, message = "Định dạng tệp không được hỗ trợ." });

                bool isImage = ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif";
                newMessage.MessageType = isImage ? "Image" : "File";

                string uploadFolder = Path.Combine(_env.WebRootPath, "uploads", "chat");
                Directory.CreateDirectory(uploadFolder);

                string fileName = $"{Guid.NewGuid():N}{ext}";
                string filePath = Path.Combine(uploadFolder, fileName);

                await using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await attachment.CopyToAsync(stream);
                }

                newMessage.AttachmentUrl = "/uploads/chat/" + fileName;
            }

            _context.UserMessages.Add(newMessage);

            var senderUser = await _context.Users.FindAsync(senderId);
            var propertyInfo = await _context.Properties.FindAsync(propertyId);
            string senderName = SafeUserName(senderUser);
            string notificationPreview = attachment != null ? "[Đã gửi một tệp đính kèm]" : cleanMessage;

            bool hasRecentUnreadNotification = await _context.Notifications.AnyAsync(n =>
                n.UserID == receiverId &&
                n.ActionUrl != null &&
                n.ActionUrl.Contains($"receiverId={senderId}") &&
                n.ActionUrl.Contains($"propertyId={propertyId}") &&
                n.IsRead == false);

            if (!hasRecentUnreadNotification)
            {
                _context.Notifications.Add(new Notification
                {
                    UserID = receiverId,
                    Title = $"Tin nhắn mới từ {senderName}",
                    Content = $"BĐS: {propertyInfo?.Title}\nNội dung: {notificationPreview}",
                    ActionUrl = $"/UserMessage/Index?receiverId={senderId}&propertyId={propertyId}",
                    ActionText = "Trả lời ngay",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReportConversation(
       [FromForm] int reportedUserId,
       [FromForm] int propertyId,
       [FromForm] string reason,
       [FromForm] string? description)
        {
            int reporterId = GetCurrentUserId();

            if (reporterId <= 0)
            {
                return Unauthorized();
            }

            if (reportedUserId <= 0 || propertyId <= 0)
            {
                return Json(new
                {
                    success = false,
                    message = "Thiếu thông tin cuộc trò chuyện cần báo cáo."
                });
            }

            if (reporterId == reportedUserId)
            {
                return Json(new
                {
                    success = false,
                    message = "Bạn không thể tự báo cáo chính mình."
                });
            }

            reason = string.IsNullOrWhiteSpace(reason)
                ? "Khác"
                : reason.Trim();

            description = string.IsNullOrWhiteSpace(description)
                ? null
                : description.Trim();

            if (reason.Length > 120)
            {
                reason = reason[..120];
            }

            if (description != null && description.Length > 2000)
            {
                description = description[..2000];
            }

            var reporter = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.UserID == reporterId && !u.IsDeleted);

            var reportedUser = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.UserID == reportedUserId && !u.IsDeleted);

            var property = await _context.Properties
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PropertyID == propertyId && p.IsDeleted != true);

            if (reporter == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy tài khoản người báo cáo."
                });
            }

            if (reportedUser == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy người dùng bị báo cáo."
                });
            }

            if (property == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy tin bất động sản liên quan."
                });
            }

            bool hasConversation = await _context.UserMessages.AnyAsync(m =>
                m.PropertyID == propertyId &&
                (
                    (m.SenderID == reporterId && m.ReceiverID == reportedUserId) ||
                    (m.SenderID == reportedUserId && m.ReceiverID == reporterId)
                ));

            if (!hasConversation)
            {
                return Json(new
                {
                    success = false,
                    message = "Không tìm thấy cuộc trò chuyện để báo cáo."
                });
            }

            bool alreadyPending = await _context.ConversationReports.AnyAsync(r =>
                r.ReporterID == reporterId &&
                r.ReportedUserID == reportedUserId &&
                r.PropertyID == propertyId &&
                r.Status == "Pending");

            if (alreadyPending)
            {
                return Json(new
                {
                    success = false,
                    message = "Bạn đã gửi báo cáo cuộc trò chuyện này. Admin sẽ xem xét sớm nhất."
                });
            }

            var report = new ConversationReport
            {
                ReporterID = reporterId,
                ReportedUserID = reportedUserId,
                PropertyID = propertyId,
                Reason = reason,
                Description = description,
                Status = "Pending",
                CreatedAt = DateTime.Now
            };

            _context.ConversationReports.Add(report);

            await _context.SaveChangesAsync();

            /*
                1. Thông báo cho Admin/Staff.
            */
            var adminIds = await GetAdminAndStaffIdsAsync();

            foreach (int adminId in adminIds)
            {
                _context.Notifications.Add(new Notification
                {
                    UserID = adminId,
                    Title = "Có báo cáo cuộc trò chuyện mới",
                    Content =
                        $"Người báo cáo: {SafeUserName(reporter)}\n" +
                        $"Người bị báo cáo: {SafeUserName(reportedUser)}\n" +
                        $"Tin liên quan: {property.Title}\n" +
                        $"Lý do: {reason}\n" +
                        "Vui lòng kiểm tra lịch sử tin nhắn và chọn hình thức xử lý phù hợp.",
                    ActionUrl = $"/Admin/ConversationReports/Details/{report.ReportID}",
                    ActionText = "Xem và xử lý",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }

            /*
                2. Thông báo xác nhận cho người gửi báo cáo.
                Người bị báo cáo CHƯA nhận thông báo ở bước này,
                tránh làm căng thẳng khi Admin chưa xác minh.
            */
            _context.Notifications.Add(new Notification
            {
                UserID = reporterId,
                Title = "Đã tiếp nhận báo cáo cuộc trò chuyện",
                Content =
                    $"Hệ thống đã tiếp nhận báo cáo của bạn đối với cuộc trò chuyện liên quan đến tin: {property.Title}.\n" +
                    $"Lý do báo cáo: {reason}\n" +
                    "Admin/Staff sẽ kiểm tra lịch sử tin nhắn và phản hồi kết quả xử lý trong thời gian sớm nhất.",
                ActionUrl = $"/UserMessage/Index?receiverId={reportedUserId}&propertyId={propertyId}",
                ActionText = "Xem lại cuộc trò chuyện",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Đã gửi báo cáo cho Admin. Hệ thống sẽ kiểm tra và phản hồi kết quả xử lý."
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConversationForMe([FromForm] int partnerId, [FromForm] int propertyId)
        {
            int userId = GetCurrentUserId();
            if (userId <= 0) return Json(new { success = false, message = "Vui lòng đăng nhập lại." });

            if (partnerId <= 0 || propertyId <= 0)
                return Json(new { success = false, message = "Thiếu thông tin cuộc trò chuyện." });

            var messages = await _context.UserMessages
                .Where(m =>
                    m.PropertyID == propertyId &&
                    ((m.SenderID == userId && m.ReceiverID == partnerId) ||
                     (m.SenderID == partnerId && m.ReceiverID == userId)))
                .ToListAsync();

            if (!messages.Any())
                return Json(new { success = false, message = "Không tìm thấy lịch sử tin nhắn cần xóa." });

            foreach (var message in messages)
            {
                if (message.SenderID == userId) message.IsDeletedBySender = true;
                if (message.ReceiverID == userId) message.IsDeletedByReceiver = true;
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Đã xóa cuộc trò chuyện khỏi hộp thư của bạn. Hệ thống vẫn lưu lịch sử để phục vụ xử lý vi phạm khi cần." });
        }
    }
}
