using Microsoft.AspNetCore.Mvc;
using BDSKhanhHoa.ViewModels;
using BDSKhanhHoa.Services;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers.Api
{
    [ApiController]
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
            if (request == null || string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest(new
                {
                    message = "Nội dung tin nhắn không được để trống."
                });
            }

            try
            {
                int currentUserId = 0;
                string? userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

                if (!string.IsNullOrWhiteSpace(userIdClaim) && int.TryParse(userIdClaim, out int parsedUserId))
                {
                    currentUserId = parsedUserId;
                }

                request.UserId = currentUserId;

                ChatResponse result = await _chatbotService.ProcessChatAsync(request);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Lỗi hệ thống nội bộ: " + ex.Message
                });
            }
        }
    }
}