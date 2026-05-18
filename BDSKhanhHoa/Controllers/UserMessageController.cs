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

        public async Task<IActionResult> Index(int? receiverId, int? propertyId)
        {
            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var messages = await _context.UserMessages
                .Include(m => m.Sender)
                .Include(m => m.Receiver)
                .Include(m => m.Property)
                .Where(m => m.SenderID == userId || m.ReceiverID == userId)
                .OrderByDescending(m => m.CreatedAt)
                .ToListAsync();

            var conversations = messages
                .GroupBy(m => new
                {
                    PartnerId = m.SenderID == userId ? m.ReceiverID : m.SenderID,
                    m.PropertyID
                })
                .Select(g => g.First())
                .OrderByDescending(m => m.CreatedAt)
                .ToList();

            ViewBag.CurrentUserId = userId;
            ViewBag.OpenReceiverId = receiverId;
            ViewBag.OpenPropertyId = propertyId;

            return View(conversations);
        }

        [HttpGet]
        public async Task<IActionResult> GetChatHistory(int partnerId, int propertyId)
        {
            int userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var property = await _context.Properties
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PropertyID == propertyId);

            if (property == null)
            {
                return NotFound();
            }

            bool currentUserIsSeller = property.UserID == userId;

            var chatHistory = await _context.UserMessages
                .Include(m => m.Sender)
                .Where(m => m.PropertyID == propertyId &&
                            ((m.SenderID == userId && m.ReceiverID == partnerId) ||
                             (m.SenderID == partnerId && m.ReceiverID == userId)))
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
                    Time = m.CreatedAt.ToString("HH:mm dd/MM"),
                    IsMine = m.SenderID == userId,
                    DirectionText =
                        m.SenderID == userId
                            ? (currentUserIsSeller ? "Tôi trả lời" : "Tôi hỏi")
                            : (currentUserIsSeller ? "Khách hỏi" : "Người bán trả lời")
                })
                .ToListAsync();

            var unreadMessages = await _context.UserMessages
                .Where(m => m.ReceiverID == userId &&
                            m.SenderID == partnerId &&
                            m.PropertyID == propertyId &&
                            !m.IsRead)
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
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var partner = await _context.Users.FindAsync(partnerId);
            var property = await _context.Properties.FindAsync(propertyId);

            if (partner == null || property == null)
            {
                return NotFound();
            }

            bool currentUserIsSeller = property.UserID == currentUserId;

            return Json(new
            {
                PartnerName = partner.FullName ?? partner.Username,
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
            string? senderIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrEmpty(senderIdStr))
            {
                return Unauthorized();
            }

            int senderId = int.Parse(senderIdStr);

            if (senderId == receiverId)
            {
                return Json(new { success = false, message = "Bạn không thể tự gửi tin nhắn cho chính mình." });
            }

            bool propertyExists = await _context.Properties.AnyAsync(p => p.PropertyID == propertyId);
            if (!propertyExists)
            {
                return Json(new { success = false, message = "Bất động sản không tồn tại hoặc đã bị xóa." });
            }

            bool receiverExists = await _context.Users.AnyAsync(u => u.UserID == receiverId);
            if (!receiverExists)
            {
                return Json(new { success = false, message = "Người nhận không tồn tại." });
            }

            if (string.IsNullOrWhiteSpace(messageContent) && attachment == null)
            {
                return Json(new { success = false, message = "Tin nhắn không được để trống." });
            }

            var newMessage = new UserMessage
            {
                SenderID = senderId,
                ReceiverID = receiverId,
                PropertyID = propertyId,
                MessageContent = string.IsNullOrWhiteSpace(messageContent) ? null : messageContent.Trim(),
                IsRead = false,
                CreatedAt = DateTime.Now,
                MessageType = "Text"
            };

            if (attachment != null && attachment.Length > 0)
            {
                const long maxFileSize = 10 * 1024 * 1024;

                if (attachment.Length > maxFileSize)
                {
                    return Json(new { success = false, message = "Tệp đính kèm không được vượt quá 10MB." });
                }

                string ext = Path.GetExtension(attachment.FileName).ToLowerInvariant();

                string[] allowedExts =
                {
                    ".jpg", ".jpeg", ".png", ".webp", ".gif",
                    ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".txt"
                };

                if (!allowedExts.Contains(ext))
                {
                    return Json(new { success = false, message = "Định dạng tệp không được hỗ trợ." });
                }

                bool isImage = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".webp" || ext == ".gif";
                newMessage.MessageType = isImage ? "Image" : "File";

                string uploadFolder = Path.Combine(_env.WebRootPath, "uploads", "chat");

                if (!Directory.Exists(uploadFolder))
                {
                    Directory.CreateDirectory(uploadFolder);
                }

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

            string senderName = senderUser?.FullName ?? senderUser?.Username ?? "Một khách hàng";
            string notificationPreview = attachment != null
                ? "[Đã gửi một tệp đính kèm]"
                : (messageContent ?? string.Empty);

            bool hasRecentUnreadNotification = await _context.Notifications.AnyAsync(n =>
                n.UserID == receiverId &&
                n.ActionUrl.Contains($"receiverId={senderId}") &&
                n.ActionUrl.Contains($"propertyId={propertyId}") &&
                n.IsRead == false);

            if (!hasRecentUnreadNotification)
            {
                var notification = new Notification
                {
                    UserID = receiverId,
                    Title = $"Tin nhắn mới từ {senderName}",
                    Content = $"BĐS: {propertyInfo?.Title}\nNội dung: {notificationPreview}",
                    ActionUrl = $"/UserMessage/Index?receiverId={senderId}&propertyId={propertyId}",
                    ActionText = "Trả lời ngay",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                };

                _context.Notifications.Add(notification);
            }

            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
    }
}
