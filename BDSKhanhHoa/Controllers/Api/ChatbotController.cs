using Microsoft.AspNetCore.Mvc;
using BDSKhanhHoa.ViewModels;
using BDSKhanhHoa.Services;
using System.Security.Claims;
using System.Threading.Tasks;
using System;

namespace BDSKhanhHoa.Controllers.Api
{
    [ApiController]
    // Sử dụng route "api/chatbot" để KHÔNG BỊ TRÙNG (AmbiguousMatchException) với Controller chat giữa người dùng
    [Route("api/chatbot")]
    public class ChatbotController : ControllerBase
    {
        private readonly ChatbotService _chatbotService;

        public ChatbotController(ChatbotService chatbotService)
        {
            _chatbotService = chatbotService;
        }

        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromBody] ChatRequest request)
        {
            // Kiểm tra đầu vào
            if (request == null || string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest(new { message = "Nội dung tin nhắn không được để trống." });
            }

            try
            {
                // Xác định UserId từ hệ thống Authentication của ASP.NET Core
                int currentUserId = 0; // Mặc định là 0 (Đại diện cho Guest/Khách vãng lai)
                var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

                if (!string.IsNullOrEmpty(userIdClaim) && int.TryParse(userIdClaim, out int parsedId))
                {
                    currentUserId = parsedId;
                }

                // FIX LỖI ÉP KIỂU Ở ĐÂY: Gán giá trị int an toàn vào request
                request.UserId = currentUserId;

                // Xử lý thông qua Service (Đã nhận diện đủ Message, UserId và PageContext)
                var result = await _chatbotService.ProcessChatAsync(request);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống nội bộ: " + ex.Message });
            }
        }
    }
}