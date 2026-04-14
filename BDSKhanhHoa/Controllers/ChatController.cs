using Microsoft.AspNetCore.Mvc;
using BDSKhanhHoa.ViewModels;
using BDSKhanhHoa.Services;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly ChatbotService _chatbotService;

        public ChatController(ChatbotService chatbotService)
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
                int? currentUserId = null;
                var userIdClaim = User.FindFirst("UserID")?.Value;

                if (!string.IsNullOrEmpty(userIdClaim))
                {
                    currentUserId = int.Parse(userIdClaim);
                }

                request.UserId = currentUserId;

                // Xử lý thông qua Service
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