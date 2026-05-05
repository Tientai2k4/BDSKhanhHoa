using System.Threading.Tasks;

namespace BDSKhanhHoa.Services
{
    public interface IEmailService
    {
        Task SendEmailAsync(string toEmail, string subject, string htmlMessage);
    }
}