using System.Threading;
using System.Threading.Tasks;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public interface ISecureStorageService
{
    Task SaveConfigAsync(AppConfig config, CancellationToken cancellationToken = default);
    Task<AppConfig> LoadConfigAsync(CancellationToken cancellationToken = default);
    byte[] EncryptData(byte[] plaintext);
    byte[] DecryptData(byte[] encryptedPayload);
}

