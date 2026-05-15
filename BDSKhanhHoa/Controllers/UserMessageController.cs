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

        // 1. GIAO DIỆN CHAT CHÍNH
        public async Task<IActionResult> Index(int? receiverId, int? propertyId)
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            // Lấy danh sách các cuộc hội thoại (Nhóm theo Người đối diện và BĐS)
            var messages = await _context.UserMessages
                .Include(m => m.Sender)
                .Include(m => m.Receiver)
                .Include(m => m.Property)
                .Where(m => m.SenderID == userId || m.ReceiverID == userId)
                .OrderByDescending(m => m.CreatedAt)
                .ToListAsync();

            // Lọc ra tin nhắn cuối cùng của mỗi cuộc hội thoại để làm Sidebar
            var conversations = messages
                .GroupBy(m => new {
                    PartnerId = m.SenderID == userId ? m.ReceiverID : m.SenderID,
                    m.PropertyID
                })
                .Select(g => g.First())
                .ToList();

            ViewBag.CurrentUserId = userId;
            ViewBag.OpenReceiverId = receiverId;
            ViewBag.OpenPropertyId = propertyId;

            return View(conversations);
        }

        // 2. API LOAD LỊCH SỬ CHAT (ĐƯỢC GỌI BẰNG AJAX)
        [HttpGet]
        public async Task<IActionResult> GetChatHistory(int partnerId, int propertyId)
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var chatHistory = await _context.UserMessages
                .Include(m => m.Sender)
                .Where(m => m.PropertyID == propertyId &&
                            ((m.SenderID == userId && m.ReceiverID == partnerId) ||
                             (m.SenderID == partnerId && m.ReceiverID == userId)))
                .OrderBy(m => m.CreatedAt)
                .Select(m => new {
                    m.MessageID,
                    m.SenderID,
                    SenderName = m.Sender!.FullName ?? m.Sender.Username,
                    SenderAvatar = m.Sender.Avatar ?? "/images/avatars/default-user.png",
                    m.MessageContent,
                    m.AttachmentUrl,
                    m.MessageType,
                    Time = m.CreatedAt.ToString("HH:mm dd/MM"),
                    IsMine = m.SenderID == userId
                })
                .ToListAsync();

            // Đánh dấu đã đọc
            var unreadMessages = await _context.UserMessages
                .Where(m => m.ReceiverID == userId && m.SenderID == partnerId && m.PropertyID == propertyId && !m.IsRead)
                .ToListAsync();

            if (unreadMessages.Any())
            {
                unreadMessages.ForEach(m => m.IsRead = true);
                await _context.SaveChangesAsync();
            }

            return Json(chatHistory);
        }

        // 3. API LẤY THÔNG TIN HEADER VÀ NHẬN DIỆN VAI TRÒ
        [HttpGet]
        public async Task<IActionResult> GetChatHeader(int partnerId, int propertyId)
        {
            var currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var partner = await _context.Users.FindAsync(partnerId);
            var property = await _context.Properties.FindAsync(propertyId);

            if (partner == null || property == null) return NotFound();

            // Nhận diện xem người đang chat có phải là Chủ của BĐS này không
            bool isOwner = property.UserID == currentUserId;

            return Json(new
            {
                PartnerName = partner.FullName ?? partner.Username,
                PartnerAvatar = partner.Avatar ?? "/images/avatars/default-user.png",
                PartnerPhone = partner.Phone,
                PropertyTitle = property.Title,
                PropertyPrice = property.Price?.ToString("N0") + " đ",
                IsOwner = isOwner
            });
        }

        // 4. API GỬI TIN NHẮN VÀ TẠO THÔNG BÁO (NOTIFICATION)
        [HttpPost]
        public async Task<IActionResult> SendMessage([FromForm] int receiverId, [FromForm] int propertyId, [FromForm] string? messageContent, IFormFile? attachment)
        {
            var senderIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(senderIdStr)) return Unauthorized();
            var senderId = int.Parse(senderIdStr);

            if (string.IsNullOrWhiteSpace(messageContent) && attachment == null)
                return Json(new { success = false, message = "Tin nhắn rỗng!" });

            var newMessage = new UserMessage
            {
                SenderID = senderId,
                ReceiverID = receiverId,
                PropertyID = propertyId,
                MessageContent = messageContent,
                IsRead = false,
                CreatedAt = DateTime.Now,
                MessageType = "Text"
            };

            // Xử lý upload file
            if (attachment != null && attachment.Length > 0)
            {
                string ext = Path.GetExtension(attachment.FileName).ToLower();
                bool isImage = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".webp" || ext == ".gif";

                newMessage.MessageType = isImage ? "Image" : "File";

                string uploadFolder = Path.Combine(_env.WebRootPath, "uploads", "chat");
                if (!Directory.Exists(uploadFolder)) Directory.CreateDirectory(uploadFolder);

                string fileName = Guid.NewGuid().ToString() + ext;
                string filePath = Path.Combine(uploadFolder, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await attachment.CopyToAsync(stream);
                }

                newMessage.AttachmentUrl = "/uploads/chat/" + fileName;
            }

            _context.UserMessages.Add(newMessage);

            // TẠO THÔNG BÁO QUẢ CHUÔNG CHO NGƯỜI NHẬN
            var senderUser = await _context.Users.FindAsync(senderId);
            var propertyInfo = await _context.Properties.FindAsync(propertyId);
            string senderName = senderUser?.FullName ?? "Một khách hàng";
            string notifContent = attachment != null ? $"[Đã gửi một tệp đính kèm]" : messageContent;

            // Kiểm tra xem đã có thông báo chưa đọc nào từ người này về BĐS này chưa, tránh spam liên tục
            bool hasRecentUnreadNotif = await _context.Notifications.AnyAsync(n =>
                n.UserID == receiverId &&
                n.ActionUrl.Contains($"receiverId={senderId}") &&
                n.IsRead == false);

            if (!hasRecentUnreadNotif)
            {
                var notification = new Notification
                {
                    UserID = receiverId,
                    Title = $"Tin nhắn mới từ {senderName}",
                    Content = $"BĐS: {propertyInfo?.Title}\nNội dung: {notifContent}",
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