using System.Threading.Tasks;

namespace BDSKhanhHoa.Services
{
    public interface IAuditLogService
    {
        Task LogAsync(
            int userId,
            string action,
            string moduleName,
            string target,
            string? oldValues = null,
            string? newValues = null,
            string severity = "Info");
    }
}