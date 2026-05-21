using BDSKhanhHoa.Services;
using BDSKhanhHoa.ViewModels;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers.Api
{
    [ApiController]
    [Route("api/chatbot")]
    public class ChatbotController : ControllerBase
    {
        private readonly ChatbotService _chatbotService;
        private readonly ILogger<ChatbotController> _logger;

        public ChatbotController(ChatbotService chatbotService, ILogger<ChatbotController> logger)
        {
            _chatbotService = chatbotService;
            _logger = logger;
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
                request.Message = request.Message.Trim();

                ChatResponse result = await _chatbotService.ProcessChatAsync(request);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý tin nhắn chatbot.");

                return StatusCode(500, new
                {
                    message = "Hiện tại trợ lý AI chưa phản hồi được. Bạn vui lòng thử lại sau ít phút."
                });
            }
        }
    }
}